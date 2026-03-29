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
}

export interface ProvidersResponse {
  providers: ExternalProvider[];
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
