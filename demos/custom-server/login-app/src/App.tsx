import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { ForgotPasswordPage, ResetPasswordPage } from '@drawboard/authagonal-login';
import AcmeAuthLayout from './AcmeAuthLayout';
import AcmeLoginPage from './AcmeLoginPage';

// This app reuses ForgotPasswordPage and ResetPasswordPage from the base package
// but replaces the LoginPage and AuthLayout with custom versions.

export default function App() {
  return (
    <BrowserRouter>
      <AcmeAuthLayout>
        <Routes>
          <Route path="/login" element={<AcmeLoginPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
          <Route path="*" element={<Navigate to="/login" replace />} />
        </Routes>
      </AcmeAuthLayout>
    </BrowserRouter>
  );
}
