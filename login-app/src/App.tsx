import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import AuthLayout from './components/AuthLayout';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import ForgotPasswordPage from './pages/ForgotPasswordPage';
import ResetPasswordPage from './pages/ResetPasswordPage';
import MfaChallengePage from './pages/MfaChallengePage';
import MfaSetupPage from './pages/MfaSetupPage';
import DevicePage from './pages/DevicePage';
import ConsentPage from './pages/ConsentPage';
import GrantsPage from './pages/GrantsPage';

export default function App() {
  return (
    <BrowserRouter>
      <AuthLayout>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/register" element={<RegisterPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
          <Route path="/mfa-challenge" element={<MfaChallengePage />} />
          <Route path="/mfa-setup" element={<MfaSetupPage />} />
          <Route path="/device" element={<DevicePage />} />
          <Route path="/consent" element={<ConsentPage />} />
          <Route path="/grants" element={<GrantsPage />} />
          <Route path="*" element={<Navigate to="/login" replace />} />
        </Routes>
      </AuthLayout>
    </BrowserRouter>
  );
}
