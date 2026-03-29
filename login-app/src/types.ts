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
