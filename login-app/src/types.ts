export interface LoginResponse {
  userId: string;
  email: string;
  name: string;
}

export interface ApiError {
  error: string;
  message?: string;
  retryAfter?: number;
  redirectUrl?: string;
}

export interface SessionResponse {
  authenticated: boolean;
  userId: string;
  email: string;
  name: string;
}

/** One "back to app" target: an enabled client with a home URI configured. */
export interface AppLinkResponse {
  clientId: string;
  clientName: string;
  homeUri: string;
  logoUri?: string;
  isDefault: boolean;
}

export interface SsoCheckResponse {
  ssoRequired: boolean;
  providerType?: string;
  connectionId?: string;
  redirectUrl?: string;
}

export interface ExternalProvider {
  connectionId: string;
  name: string;
  loginUrl: string;
  /** Connection protocol: "oidc" or "saml". */
  type?: string;
  /** Optional branding icon URL shown on the "Continue with {name}" button. */
  iconUrl?: string;
}

export interface ProvidersResponse {
  providers: ExternalProvider[];
  /** Cloudflare Turnstile site key when configured; absent = Turnstile disabled (render no widget). */
  turnstileSiteKey?: string;
}

export interface PasswordPolicyRule {
  rule: string;
  value: number | null;
  label: string;
}

export interface PasswordPolicyResponse {
  rules: PasswordPolicyRule[];
}

// MFA types
export interface MfaLoginResponse {
  mfaRequired?: boolean;
  mfaSetupRequired?: boolean;
  mfaAvailable?: boolean;
  clientId?: string;
  challengeId?: string;
  setupToken?: string;
  methods?: string[];
  webAuthn?: PublicKeyCredentialRequestOptionsJSON;
  userId?: string;
  email?: string;
  name?: string;
}

// WebAuthn types (simplified from WebAuthn L2 spec)
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type PublicKeyCredentialRequestOptionsJSON = any;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type PublicKeyCredentialCreationOptionsJSON = any;

export interface MfaVerifyResponse {
  userId: string;
  email: string;
  name: string;
}

export interface MfaStatusResponse {
  enabled: boolean;
  methods: MfaMethod[];
  /** Whether MFA is offered for this tenant at all (some client policy != Disabled). Absent on older
   *  servers — treat only an explicit false as "not offered". */
  offered?: boolean;

  /** Whether this is a FORCED enrolment: the caller reached the endpoints with an enrolment token
   *  rather than a session, so it has no session until it enrols. The server decides — this used to be
   *  inferred from a `setupToken` query parameter, which is why the token was in the URL. */
  forced?: boolean;
}

export interface MfaMethod {
  id: string;
  type: string;
  name: string;
  createdAt: string;
  lastUsedAt: string | null;
  isConsumed?: boolean | null;
}

export interface MfaTotpSetupResponse {
  setupToken: string;
  qrCodeDataUri: string;
  manualKey: string;
}

export interface MfaRecoveryGenerateResponse {
  codes: string[];
}

export interface MfaWebAuthnSetupResponse {
  setupToken: string;
  options: PublicKeyCredentialCreationOptionsJSON;
}

export interface MfaWebAuthnConfirmResponse {
  success: boolean;
  credentialId: string;
}

export interface RegisterResponse {
  /** True when no verification email was sent (invite redemption / auto-confirmed domain). */
  emailVerified?: boolean;
  success: boolean;
  userId: string;
}

/** The authenticated user's own profile (GET /api/auth/profile). Email is read-only here. */
export interface ProfileResponse {
  email?: string;
  emailConfirmed: boolean;
  firstName?: string;
  lastName?: string;
  companyName?: string;
  phone?: string;
  /** Preferred UI/communication language (BCP-47); drives localised emails + the OIDC locale claim. */
  locale?: string;
}

/** One of the signed-in user's active server-side sessions. */
export interface ActiveSession {
  sessionId: string;
  /** True for the caller's current session (the one making the request). */
  current: boolean;
  createdAt: string;
  lastSeenAt: string;
  expiresAt: string | null;
  ip: string;
  userAgent: string;
}

/** GET /api/auth/sessions — empty when the tenant doesn't track server-side sessions. */
export interface ActiveSessionsResponse {
  sessions: ActiveSession[];
}

/** Result of a session revocation (single or bulk). */
export interface RevokeSessionsResponse {
  revoked: number;
}

/** Self-service profile update (PATCH /api/auth/profile). Omitted fields are left unchanged. */
export interface ProfileUpdateRequest {
  firstName?: string;
  lastName?: string;
  companyName?: string;
  phone?: string;
  locale?: string;
}
