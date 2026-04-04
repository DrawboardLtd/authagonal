import { AuthLayout } from '@drawboard/authagonal-login';
import type { ReactNode } from 'react';

// Wraps the base AuthLayout to add a branded footer with legal links.

interface AcmeAuthLayoutProps {
  children: ReactNode;
}

export default function AcmeAuthLayout({ children }: AcmeAuthLayoutProps) {
  return (
    <>
      <AuthLayout>{children}</AuthLayout>
      <footer style={{
        position: 'fixed',
        bottom: 0,
        left: 0,
        right: 0,
        textAlign: 'center',
        padding: '12px',
        fontSize: '12px',
        color: '#6b7280',
      }}>
        &copy; {new Date().getFullYear()} Acme Corp &mdash;{' '}
        <a href="https://acme.example.com/terms" style={{ color: '#6b7280' }}>Terms</a>
        {' | '}
        <a href="https://acme.example.com/privacy" style={{ color: '#6b7280' }}>Privacy</a>
      </footer>
    </>
  );
}
