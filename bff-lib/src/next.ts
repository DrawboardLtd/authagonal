import type { AuthagonalBffOptions } from './options.js';
import { type HttpCtx, buildDeps, routeBff, serializeCookie, parseCookies, isProxyPath, authorizeProxy, PROXY_STRIP } from './core.js';

/**
 * Next.js App Router handlers for the Authagonal BFF. Mount at `app/bff/[...bff]/route.ts`:
 *
 * ```ts
 * import { createBffRoute } from '@authagonal/bff/next';
 * export const { GET, POST } = createBffRoute({ authority, clientId, clientSecret, cookieSecret });
 * ```
 *
 * Run this route on the Node.js runtime (it uses a server-side session store + client secret).
 */
export function createBffRoute(options: AuthagonalBffOptions): {
  GET: (req: Request) => Promise<Response>;
  POST: (req: Request) => Promise<Response>;
} {
  const deps = buildDeps(options);
  const handler = async (req: Request): Promise<Response> => {
    const { ctx, finalize } = nextCtx(req);
    if (isProxyPath(ctx.path, deps.o)) {
      const decision = await authorizeProxy(ctx, deps);
      if ('error' in decision) return new Response('', { status: decision.error });
      return forwardProxy(req, decision.targetUrl, decision.accessToken, decision.forwarded);
    }
    const handled = await routeBff(ctx, deps);
    if (!handled) ctx.text('not_found', 404);
    return finalize();
  };
  return { GET: handler, POST: handler };
}

async function forwardProxy(
  req: Request, targetUrl: string, accessToken: string, forwarded: Record<string, string>,
): Promise<Response> {
  const headers = new Headers();
  req.headers.forEach((value, key) => { if (!PROXY_STRIP.has(key.toLowerCase())) headers.set(key, value); });
  headers.set('authorization', `Bearer ${accessToken}`);
  // After the filter, so an asserted value replaces a caller's rather than colliding with it. A Web
  // `Request` has no socket, so x-forwarded-for is here only if the host supplied `clientIp`.
  for (const [name, value] of Object.entries(forwarded)) headers.set(name, value);
  const hasBody = req.method !== 'GET' && req.method !== 'HEAD';
  const upstream = await fetch(targetUrl, {
    method: req.method,
    headers,
    body: hasBody ? req.body : undefined,
    duplex: 'half',
  } as RequestInit & { duplex?: 'half' });

  const respHeaders = new Headers();
  upstream.headers.forEach((value, key) => {
    const lk = key.toLowerCase();
    if (!PROXY_STRIP.has(lk) && lk !== 'content-length') respHeaders.set(key, value);
  });
  return new Response(upstream.body, { status: upstream.status, headers: respHeaders });
}

function nextCtx(req: Request): { ctx: HttpCtx; finalize: () => Response } {
  const url = new URL(req.url);
  const proto = req.headers.get('x-forwarded-proto') ?? url.protocol.replace(':', '');
  const host = req.headers.get('x-forwarded-host') ?? req.headers.get('host') ?? url.host;
  const origin = `${proto}://${host}`;
  const cookies = parseCookies(req.headers.get('cookie') ?? undefined);

  const headers = new Headers();
  let status = 200;
  let body = '';

  const ctx: HttpCtx = {
    method: req.method,
    path: url.pathname,
    query: url.searchParams,
    origin,
    getCookie: (name) => cookies[name],
    setCookie: (name, value, opts) => headers.append('Set-Cookie', serializeCookie(name, value, opts)),
    deleteCookie: (name, opts) => headers.append('Set-Cookie', serializeCookie(name, '', { ...opts, maxAgeSeconds: 0 })),
    getHeader: (name) => req.headers.get(name) ?? undefined,
    setHeader: (name, value) => headers.set(name, value),
    readForm: async () => new URLSearchParams(await req.text()),
    redirect: (location) => { status = 302; headers.set('Location', location); },
    json: (b, s = 200) => { status = s; headers.set('Content-Type', 'application/json'); body = JSON.stringify(b); },
    text: (b, s = 200, contentType = 'text/plain') => { status = s; headers.set('Content-Type', contentType); body = b; },
  };

  return { ctx, finalize: () => new Response(body, { status, headers }) };
}
