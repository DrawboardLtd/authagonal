import type { AuthagonalBffOptions } from './options.js';
import { type HttpCtx, buildDeps, routeBff, serializeCookie, parseCookies } from './core.js';

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
    const handled = await routeBff(ctx, deps);
    if (!handled) ctx.text('not_found', 404);
    return finalize();
  };
  return { GET: handler, POST: handler };
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
