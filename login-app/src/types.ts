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
