import type { LoginResponse, ApiError, SessionResponse, SsoCheckResponse, ProvidersResponse, PasswordPolicyResponse } from './types';

const API_URL = import.meta.env.VITE_API_URL || '';

class ApiRequestError extends Error {
  public error: string;
  public retryAfter?: number;
  public redirectUrl?: string;

  constructor(apiError: ApiError) {
    super(apiError.message || apiError.error);
    this.name = 'ApiRequestError';
    this.error = apiError.error;
    this.retryAfter = apiError.retryAfter;
    this.redirectUrl = apiError.redirectUrl;
  }
}

async function api<T>(path: string, options?: RequestInit): Promise<T> {
  const url = `${API_URL}${path}`;

  const response = await fetch(url, {
    ...options,
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      ...options?.headers,
    },
  });

  if (!response.ok) {
    let apiError: ApiError;
    try {
      apiError = await response.json() as ApiError;
    } catch {
      apiError = { error: 'unknown_error', message: `Request failed with status ${response.status}` };
    }
    throw new ApiRequestError(apiError);
  }

  return response.json() as Promise<T>;
}

export function login(email: string, password: string): Promise<LoginResponse> {
  return api<LoginResponse>('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
  });
}

export function logout(): Promise<{ success: true }> {
  return api<{ success: true }>('/api/auth/logout', {
    method: 'POST',
  });
}

export function forgotPassword(email: string): Promise<{ success: true }> {
  return api<{ success: true }>('/api/auth/forgot-password', {
    method: 'POST',
    body: JSON.stringify({ email }),
  });
}

export function resetPassword(token: string, newPassword: string): Promise<{ success: true }> {
  return api<{ success: true }>('/api/auth/reset-password', {
    method: 'POST',
    body: JSON.stringify({ token, newPassword }),
  });
}

export function getSession(): Promise<SessionResponse> {
  return api<SessionResponse>('/api/auth/session');
}

export function ssoCheck(email: string): Promise<SsoCheckResponse> {
  return api<SsoCheckResponse>(`/api/auth/sso-check?email=${encodeURIComponent(email)}`);
}

export function getProviders(): Promise<ProvidersResponse> {
  return api<ProvidersResponse>('/api/auth/providers');
}

export function getPasswordPolicy(): Promise<PasswordPolicyResponse> {
  return api<PasswordPolicyResponse>('/api/auth/password-policy');
}

export { ApiRequestError };
