import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { ForgotPasswordPage, ResetPasswordPage, MfaChallengePage, MfaSetupPage } from '@authagonal/login';
import AcmeAuthLayout from './AcmeAuthLayout';
import AcmeLoginPage from './AcmeLoginPage';
import RegisterPage from './RegisterPage';

export default function App() {
  return (
    <BrowserRouter>
      <AcmeAuthLayout>
        <Routes>
          <Route path="/login" element={<AcmeLoginPage />} />
          <Route path="/register" element={<RegisterPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
          <Route path="/mfa-challenge" element={<MfaChallengePage />} />
          <Route path="/mfa-setup" element={<MfaSetupPage />} />
          <Route path="*" element={<Navigate to="/login" replace />} />
        </Routes>
      </AcmeAuthLayout>
    </BrowserRouter>
  );
}
