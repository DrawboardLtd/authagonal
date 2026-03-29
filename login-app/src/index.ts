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
export { login, logout, forgotPassword, resetPassword, getSession, ssoCheck, getProviders, getPasswordPolicy, ApiRequestError } from './api';

// Types
export type { LoginResponse, ApiError, SessionResponse, SsoCheckResponse, ExternalProvider, ProvidersResponse, PasswordPolicyRule, PasswordPolicyResponse } from './types';

// i18n — re-export so consumers use the same react-i18next instance
export { default as i18n } from './i18n';
export { useTranslation } from 'react-i18next';

// Styles — import '@drawboard/authagonal-login/src/styles.css' in your entry point
