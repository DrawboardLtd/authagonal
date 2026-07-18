import type { AuthagonalBffOptions } from './options.js';
import { type CookieOptions, type HttpCtx, buildDeps, routeBff, serializeCookie, parseCookies, isProxyPath, authorizeProxy, PROXY_STRIP } from './core.js';

// Duck-typed Express req/res so this package needs no dependency on express or @types/express.
interface ExpressReq {
  method?: string;
  url?: string;
  originalUrl?: string;
  headers: Record<string, string | string[] | undefined>;
  socket?: { encrypted?: boolean };
  body?: unknown;
  on(event: string, cb: (chunk?: unknown) => void): void;
}
interface ExpressRes {
  statusCode: number;
  setHeader(name: string, value: string | string[]): void;
  getHeader(name: string): string | number | string[] | undefined;
  end(body?: string | Uint8Array): void;
}
type NextFn = (err?: unknown) => void;

/**
 * Express middleware that serves the Authagonal BFF endpoints and calls `next()` for everything else:
 *
 * ```ts
 * app.use(authagonalBff({ authority, clientId, clientSecret, cookieSecret }));
 * ```
 */
export function authagonalBff(options: AuthagonalBffOptions): (req: ExpressReq, res: ExpressRes, next: NextFn) => void {
  const deps = buildDeps(options);
  return (req, res, next) => {
    const ctx = expressCtx(req, res);
    void (async () => {
      if (isProxyPath(ctx.path, deps.o)) {
        const decision = await authorizeProxy(ctx, deps);
        if ('error' in decision) { res.statusCode = decision.error; res.end(); return; }
        await forwardProxy(req, res, decision.targetUrl, decision.accessToken);
        return;
      }
      const handled = await routeBff(ctx, deps);
      if (!handled) next();
    })().catch(next);
  };
}

// Buffered forward (no node:stream dependency). Fine for typical JSON/text APIs; very large uploads
// buffer in memory — swap for a streaming forward if that matters.
async function forwardProxy(req: ExpressReq, res: ExpressRes, targetUrl: string, accessToken: string): Promise<void> {
  const method = req.method ?? 'GET';
  const headers: Record<string, string> = {};
  for (const [k, v] of Object.entries(req.headers)) {
    if (PROXY_STRIP.has(k.toLowerCase())) continue;
    headers[k] = Array.isArray(v) ? v.join(', ') : String(v ?? '');
  }
  headers.authorization = `Bearer ${accessToken}`;

  let body: Uint8Array | undefined;
  if (method !== 'GET' && method !== 'HEAD') {
    const chunks: Uint8Array[] = [];
    await new Promise<void>((resolve, reject) => {
      req.on('data', (c) => chunks.push(c as Uint8Array));
      req.on('end', () => resolve());
      req.on('error', (e) => reject(e instanceof Error ? e : new Error(String(e))));
    });
    if (chunks.length) {
      const total = chunks.reduce((n, c) => n + c.length, 0);
      body = new Uint8Array(total);
      let offset = 0;
      for (const c of chunks) { body.set(c, offset); offset += c.length; }
    }
  }

  const upstream = await fetch(targetUrl, { method, headers, body: body as BodyInit | undefined });
  res.statusCode = upstream.status;
  upstream.headers.forEach((value, key) => {
    const lk = key.toLowerCase();
    if (!PROXY_STRIP.has(lk) && lk !== 'content-length') res.setHeader(key, value);
  });
  res.end(new Uint8Array(await upstream.arrayBuffer()));
}

function header(req: ExpressReq, name: string): string | undefined {
  const v = req.headers[name.toLowerCase()];
  return Array.isArray(v) ? v[0] : v;
}

function expressCtx(req: ExpressReq, res: ExpressRes): HttpCtx {
  const rawUrl = req.originalUrl ?? req.url ?? '/';
  const proto = header(req, 'x-forwarded-proto') ?? (req.socket?.encrypted ? 'https' : 'http');
  const host = header(req, 'x-forwarded-host') ?? header(req, 'host') ?? 'localhost';
  const url = new URL(rawUrl, `${proto}://${host}`);
  const cookies = parseCookies(header(req, 'cookie'));

  const appendSetCookie = (cookie: string) => {
    const prev = res.getHeader('Set-Cookie');
    const arr = Array.isArray(prev) ? prev.slice() : prev !== undefined ? [String(prev)] : [];
    arr.push(cookie);
    res.setHeader('Set-Cookie', arr);
  };

  return {
    method: req.method ?? 'GET',
    path: url.pathname,
    query: url.searchParams,
    origin: `${proto}://${host}`,
    getCookie: (name) => cookies[name],
    setCookie: (name, value, opts) => appendSetCookie(serializeCookie(name, value, opts)),
    deleteCookie: (name, opts) => appendSetCookie(serializeCookie(name, '', { ...opts, maxAgeSeconds: 0 })),
    getHeader: (name) => header(req, name),
    setHeader: (name, value) => res.setHeader(name, value),
    readForm: async () => {
      if (req.body && typeof req.body === 'object' && !ArrayBuffer.isView(req.body)) {
        const p = new URLSearchParams();
        for (const [k, v] of Object.entries(req.body as Record<string, unknown>)) p.set(k, String(v));
        return p;
      }
      const raw = await new Promise<string>((resolve, reject) => {
        let data = '';
        req.on('data', (c) => { data += c; });
        req.on('end', () => resolve(data));
        req.on('error', reject);
      });
      return new URLSearchParams(raw);
    },
    redirect: (location) => { res.statusCode = 302; res.setHeader('Location', location); res.end(); },
    json: (body, status = 200) => { res.statusCode = status; res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(body)); },
    text: (body, status = 200, contentType = 'text/plain') => { res.statusCode = status; res.setHeader('Content-Type', contentType); res.end(body); },
  };
}
