import type { ApiError, SessionResponse, SsoCheckResponse, ProvidersResponse, PasswordPolicyResponse, MfaLoginResponse, MfaVerifyResponse, MfaStatusResponse, MfaTotpSetupResponse, MfaRecoveryGenerateResponse, MfaWebAuthnSetupResponse, MfaWebAuthnConfirmResponse } from './types';

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

function setupTokenHeaders(token?: string): Record<string, string> {
  return token ? { 'X-MFA-Setup-Token': token } : {};
}

export function login(email: string, password: string, returnUrl?: string): Promise<MfaLoginResponse> {
  const url = returnUrl ? `/api/auth/login?returnUrl=${encodeURIComponent(returnUrl)}` : '/api/auth/login';
  return api<MfaLoginResponse>(url, {
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

export function mfaVerify(challengeId: string, method: string, code?: string, assertion?: string): Promise<MfaVerifyResponse> {
  return api<MfaVerifyResponse>('/api/auth/mfa/verify', {
    method: 'POST',
    body: JSON.stringify({ challengeId, method, code, assertion }),
  });
}

export function mfaStatus(mfaSetupToken?: string): Promise<MfaStatusResponse> {
  return api<MfaStatusResponse>('/api/auth/mfa/status', {
    headers: setupTokenHeaders(mfaSetupToken),
  });
}

export function mfaTotpSetup(mfaSetupToken?: string): Promise<MfaTotpSetupResponse> {
  return api<MfaTotpSetupResponse>('/api/auth/mfa/totp/setup', {
    method: 'POST',
    headers: setupTokenHeaders(mfaSetupToken),
  });
}

export function mfaTotpConfirm(setupToken: string, code: string, mfaSetupToken?: string): Promise<{ success: true }> {
  return api<{ success: true }>('/api/auth/mfa/totp/confirm', {
    method: 'POST',
    body: JSON.stringify({ setupToken, code }),
    headers: setupTokenHeaders(mfaSetupToken),
  });
}

export function mfaRecoveryGenerate(mfaSetupToken?: string): Promise<MfaRecoveryGenerateResponse> {
  return api<MfaRecoveryGenerateResponse>('/api/auth/mfa/recovery/generate', {
    method: 'POST',
    headers: setupTokenHeaders(mfaSetupToken),
  });
}

export function mfaWebAuthnSetup(mfaSetupToken?: string): Promise<MfaWebAuthnSetupResponse> {
  return api<MfaWebAuthnSetupResponse>('/api/auth/mfa/webauthn/setup', {
    method: 'POST',
    headers: setupTokenHeaders(mfaSetupToken),
  });
}

export function mfaWebAuthnConfirm(setupToken: string, attestationResponse: string, mfaSetupToken?: string): Promise<MfaWebAuthnConfirmResponse> {
  return api<MfaWebAuthnConfirmResponse>('/api/auth/mfa/webauthn/confirm', {
    method: 'POST',
    body: JSON.stringify({ setupToken, attestationResponse }),
    headers: setupTokenHeaders(mfaSetupToken),
  });
}

export function mfaDeleteCredential(credentialId: string, mfaSetupToken?: string): Promise<{ success: true }> {
  return api<{ success: true }>(`/api/auth/mfa/credentials/${credentialId}`, {
    method: 'DELETE',
    headers: setupTokenHeaders(mfaSetupToken),
  });
}

export { ApiRequestError };
