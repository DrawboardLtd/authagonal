import type { ReactNode } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router';
import AuthLayout from './components/AuthLayout';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import ForgotPasswordPage from './pages/ForgotPasswordPage';
import ResetPasswordPage from './pages/ResetPasswordPage';
import MfaChallengePage from './pages/MfaChallengePage';
import MfaSetupPage from './pages/MfaSetupPage';
import DevicePage from './pages/DevicePage';
import ConsentPage from './pages/ConsentPage';
import AgentConsentPage from './pages/AgentConsentPage';
import GrantsPage from './pages/GrantsPage';
import AccountPage from './pages/AccountPage';

/**
 * The login SPA. `extraRoutes` lets the host app inject product-specific routes
 * (e.g. the cloud's /support page) that render inside AuthLayout alongside the
 * auth routes — so those surfaces live in the consumer, not in this auth library.
 */
export default function App({ extraRoutes }: { extraRoutes?: ReactNode }) {
  return (
    <BrowserRouter basename="/login">
      <AuthLayout>
        <Routes>
          <Route path="/" element={<LoginPage />} />
          <Route path="/register" element={<RegisterPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
          <Route path="/mfa-challenge" element={<MfaChallengePage />} />
          <Route path="/mfa-setup" element={<MfaSetupPage />} />
          <Route path="/device" element={<DevicePage />} />
          <Route path="/consent" element={<ConsentPage />} />
          {/* Granting an agent standing RFC 9396 authority. Distinct from /consent, which grants
              OAuth scopes — the two authorize different things and must not share a screen. */}
          <Route path="/consent/agents/:clientId" element={<AgentConsentPage />} />
          <Route path="/grants" element={<GrantsPage />} />
          <Route path="/account" element={<AccountPage />} />
          {extraRoutes}
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </AuthLayout>
    </BrowserRouter>
  );
}
