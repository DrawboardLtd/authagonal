/**
 * Where the MFA enrolment token lives between the login response and the setup page.
 *
 * It used to live in the URL — `/mfa-setup?setupToken=…` — which is the one place it must not be. The
 * token is not a handle: it is the sole identity the enrolment endpoints accept (see the server's
 * `MfaSetupEndpoints.ResolveUserIdAsync`), and completing an enrolment it accepted signs a full session
 * cookie for that user. A URL carrying it ends up in browser history, in the `Referer` header of any
 * cross-origin subresource the page loads, and in the access log of every hop that records a request line.
 *
 * So: router state across the navigation, with sessionStorage as the reload fallback — history state does
 * not survive F5, and losing the token would strand the user mid-enrolment with no way back. sessionStorage
 * is same-origin and tab-scoped, and it is cleared as soon as enrolment completes.
 *
 * The federated path does not use this at all: the server sets an HttpOnly cookie the SPA cannot read, and
 * `mfaStatus().forced` is what tells the page it is in a forced enrolment.
 */
const KEY = 'mfa-setup-token';

export function rememberMfaSetupToken(token: string | undefined): void {
  if (token) sessionStorage.setItem(KEY, token);
}

export function readMfaSetupToken(): string | undefined {
  return sessionStorage.getItem(KEY) ?? undefined;
}

export function forgetMfaSetupToken(): void {
  sessionStorage.removeItem(KEY);
}
