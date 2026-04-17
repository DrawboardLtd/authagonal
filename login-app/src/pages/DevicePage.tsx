import { useState, useEffect } from 'react';
import { useSearchParams, useNavigate } from 'react-router-dom';
import { getSession } from '../api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle } from '@/components/ui/card';

const API_URL = import.meta.env.VITE_API_URL || '';

export default function DevicePage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [userCode, setUserCode] = useState(searchParams.get('user_code') || '');
  const [loading, setLoading] = useState(false);
  const [checking, setChecking] = useState(true);
  const [authenticated, setAuthenticated] = useState(false);
  const [approved, setApproved] = useState(false);
  const [error, setError] = useState('');

  // Check if user is already authenticated
  useEffect(() => {
    getSession()
      .then((session) => {
        setAuthenticated(!!session?.userId);
      })
      .catch(() => setAuthenticated(false))
      .finally(() => setChecking(false));
  }, []);

  async function handleApprove(e: React.FormEvent) {
    e.preventDefault();
    setLoading(true);
    setError('');

    const code = userCode.trim().toUpperCase();
    if (!code) {
      setError('Please enter the code shown on your device.');
      setLoading(false);
      return;
    }

    try {
      const res = await fetch(`${API_URL}/api/auth/device/approve`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        credentials: 'include',
        body: `user_code=${encodeURIComponent(code)}`,
      });

      if (res.ok) {
        setApproved(true);
      } else {
        const body = await res.json().catch(() => ({}));
        if (body.error === 'invalid_user_code') {
          setError('Invalid or expired code. Check the code on your device and try again.');
        } else {
          setError(body.message || 'Failed to approve. Please try again.');
        }
      }
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  if (checking) {
    return <p className="text-sm text-gray-500 dark:text-gray-400 text-center">Loading...</p>;
  }

  // Not authenticated — redirect to login with returnUrl back to this page
  if (!authenticated) {
    const returnUrl = userCode
      ? `/device?user_code=${encodeURIComponent(userCode)}`
      : '/device';

    return (
      <div className="text-center">
        <CardTitle className="mb-4">Sign in to continue</CardTitle>
        <p className="text-sm text-gray-500 dark:text-gray-400 mb-6">
          Sign in to approve access for your device.
        </p>
        <Button
          className="w-full"
          onClick={() => navigate(`/login?returnUrl=${encodeURIComponent(returnUrl)}`)}
        >
          Sign In
        </Button>
      </div>
    );
  }

  // Approved
  if (approved) {
    return (
      <div className="text-center">
        <CardTitle className="mb-4">Device approved</CardTitle>
        <div className="flex justify-center mb-4">
          <div className="w-16 h-16 rounded-full bg-green-100 dark:bg-green-900/40 flex items-center justify-center">
            <svg className="w-8 h-8 text-green-600 dark:text-green-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
            </svg>
          </div>
        </div>
        <p className="text-sm text-gray-500 dark:text-gray-400">
          You can close this window. Your device should be signed in momentarily.
        </p>
      </div>
    );
  }

  // Enter code form
  return (
    <div>
      <CardTitle className="mb-2 text-center">Authorize device</CardTitle>
      <p className="text-sm text-gray-500 dark:text-gray-400 text-center mb-6">
        Enter the code displayed on your device.
      </p>

      {error && <Alert variant="error" className="mb-4">{error}</Alert>}

      <form onSubmit={handleApprove}>
        <div className="mb-4">
          <Label htmlFor="user_code">Device code</Label>
          <Input
            id="user_code"
            type="text"
            value={userCode}
            onChange={(e) => setUserCode(e.target.value.toUpperCase())}
            placeholder="ABCD-1234"
            className="text-center text-2xl font-mono tracking-widest"
            maxLength={9}
            autoFocus
            autoComplete="off"
          />
        </div>
        <Button type="submit" className="w-full" loading={loading}>
          Approve
        </Button>
      </form>
    </div>
  );
}
