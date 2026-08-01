import type { ReactNode } from 'react';
import { BrowserRouter, Routes, Route, Navigate, useLocation } from 'react-router';
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
 * Catch-all: send an unmatched path to the sign-in page, KEEPING the query string.
 *
 * It used to be a bare `<Navigate to="/" replace />`, which discarded the search. Every parameter this
 * app carries between screens rides in the query — returnUrl above all — so any path that failed to
 * match (a typo, a route renamed, a link built without the router basename) silently dropped the
 * user's destination and landed them on a bare login form that would send them to the default app
 * afterwards. Preserving the search means the same slip degrades into "wrong page, right destination".
 */
function NotFoundRedirect() {
  const location = useLocation();
  return <Navigate to={{ pathname: '/', search: location.search }} replace />;
}

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
          <Route path="*" element={<NotFoundRedirect />} />
        </Routes>
      </AuthLayout>
    </BrowserRouter>
  );
}
