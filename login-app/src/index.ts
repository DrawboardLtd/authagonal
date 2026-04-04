// Public API — consumers import from '@drawboard/authagonal-login'
//
// Usage:
//   import { AuthLayout, LoginPage, useBranding } from '@drawboard/authagonal-login';

// Components
export { default as AuthLayout } from './components/AuthLayout';
export { Button } from './components/ui/button';
export type { ButtonProps } from './components/ui/button';
export { Input } from './components/ui/input';
export { Label } from './components/ui/label';
export { Card, CardHeader, CardTitle, CardDescription, CardContent, CardFooter } from './components/ui/card';
export { Alert } from './components/ui/alert';
export { Separator } from './components/ui/separator';
export { cn } from './lib/utils';

// Pages
export { default as LoginPage } from './pages/LoginPage';
export { default as ForgotPasswordPage } from './pages/ForgotPasswordPage';
export { default as ResetPasswordPage } from './pages/ResetPasswordPage';
export { default as MfaChallengePage } from './pages/MfaChallengePage';
export { default as MfaSetupPage } from './pages/MfaSetupPage';

// Branding
export { loadBranding, BrandingContext, useBranding, resolveLocalized } from './branding';
export type { BrandingConfig, LocalizedString } from './branding';

// API client
export { login, logout, forgotPassword, resetPassword, getSession, ssoCheck, getProviders, getPasswordPolicy, mfaVerify, mfaStatus, mfaTotpSetup, mfaTotpConfirm, mfaWebAuthnSetup, mfaWebAuthnConfirm, mfaRecoveryGenerate, mfaDeleteCredential, ApiRequestError } from './api';

// Types
export type { LoginResponse, ApiError, SessionResponse, SsoCheckResponse, ExternalProvider, ProvidersResponse, PasswordPolicyRule, PasswordPolicyResponse, MfaLoginResponse, MfaVerifyResponse, MfaStatusResponse, MfaMethod, MfaTotpSetupResponse, MfaRecoveryGenerateResponse, MfaWebAuthnSetupResponse, MfaWebAuthnConfirmResponse } from './types';

// i18n — re-export so consumers use the same react-i18next instance
export { default as i18n } from './i18n';
export { useTranslation } from 'react-i18next';

// Styles — bundled into dist/style.css via the side-effect import below.
// Consumers: import '@drawboard/authagonal-login/styles.css'
import './styles.css';
