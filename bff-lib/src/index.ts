// Core + seams. Framework adapters live in the subpath entrypoints:
//   import { authagonalBff } from '@authagonal/bff/express';
//   import { createBffRoute } from '@authagonal/bff/next';
export type { AuthagonalBffOptions, ResolvedBffOptions, BffSessionMode } from './options.js';
export { resolveOptions } from './options.js';
export type { BffSession, IBffSessionStore } from './session.js';
export { MemorySessionStore } from './session.js';
export type { ICookieProtector } from './cookies.js';
export { JoseCookieProtector } from './cookies.js';
export { OidcClient, BffTokenError, base64url, randomToken, codeChallenge, type TokenResult } from './oidc.js';
export { RefreshCoordinator } from './refresh.js';
export type { HttpCtx, CookieOptions, BffDeps } from './core.js';
export { buildDeps, routeBff, serializeCookie, parseCookies } from './core.js';
