import type { AuthagonalBffOptions } from './options.js';
import { type CookieOptions, type HttpCtx, buildDeps, routeBff, serializeCookie, parseCookies } from './core.js';

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
  end(body?: string): void;
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
    routeBff(ctx, deps)
      .then((handled) => { if (!handled) next(); })
      .catch((err) => next(err));
  };
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
