// Public API — consumers import from '@drawboard/authagonal-login'
//
// Usage:
//   import { AuthLayout, LoginPage, useBranding } from '@drawboard/authagonal-login';

// Components
export { default as AuthLayout } from './components/AuthLayout';

// Pages
export { default as LoginPage } from './pages/LoginPage';
export { default as ForgotPasswordPage } from './pages/ForgotPasswordPage';
export { default as ResetPasswordPage } from './pages/ResetPasswordPage';

// Branding
export { loadBranding, BrandingContext, useBranding } from './branding';
export type { BrandingConfig } from './branding';

// API client
export { login, logout, forgotPassword, resetPassword, getSession, ssoCheck, getPasswordPolicy, ApiRequestError } from './api';

// Types
export type { LoginResponse, ApiError, SessionResponse, SsoCheckResponse, PasswordPolicyRule, PasswordPolicyResponse } from './types';

// Styles — import '@drawboard/authagonal-login/src/styles.css' in your entry point
