import { useState, useEffect } from 'react';
import { useSearchParams, useNavigate } from 'react-router';
import { getSession } from '../api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle } from '@/components/ui/card';

const API_URL = import.meta.env.VITE_API_URL || '';

type DeviceInfo = {
  clientId: string;
  clientName: string;
  clientUri?: string | null;
  logoUri?: string | null;
  scopes: string[];
};

export default function DevicePage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [userCode, setUserCode] = useState(searchParams.get('user_code') || '');
  const [loading, setLoading] = useState(false);
  const [checking, setChecking] = useState(true);
  const [authenticated, setAuthenticated] = useState(false);
  const [approved, setApproved] = useState(false);
  const [error, setError] = useState('');
  // What the user is actually approving. Until this is loaded and displayed, Approve stays disabled:
  // RFC 8628 §5.4's remote-phishing warning is about approving an OPAQUE prompt, and
  // verification_uri_complete pre-fills the code so approval was otherwise one click on nothing.
  const [info, setInfo] = useState<DeviceInfo | null>(null);
  const [loadingInfo, setLoadingInfo] = useState(false);

  // Check if user is already authenticated
  useEffect(() => {
    getSession()
      .then((session) => {
        setAuthenticated(!!session?.userId);
      })
      .catch(() => setAuthenticated(false))
      .finally(() => setChecking(false));
  }, []);

  // Step 1: resolve what the code represents. Deliberately a separate step from approving — the user
  // must see the requesting application and the scopes before they can grant anything.
  async function handleLookup(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const code = userCode.trim().toUpperCase();
    if (!code) {
      setError('Please enter the code shown on your device.');
      return;
    }

    setLoadingInfo(true);
    try {
      const res = await fetch(
        `${API_URL}/api/auth/device/info?user_code=${encodeURIComponent(code)}`,
        { credentials: 'include' },
      );
      if (res.ok) {
        setInfo(await res.json());
      } else {
        const body = await res.json().catch(() => ({}));
        setError(
          body.error === 'invalid_user_code'
            ? 'Invalid or expired code. Check the code on your device and try again.'
            : body.message || 'Could not look up that code. Please try again.',
        );
      }
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoadingInfo(false);
    }
  }

  // Step 2: the actual grant, only reachable once the details above are on screen.
  async function handleApprove() {
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
          onClick={() => navigate(`/?returnUrl=${encodeURIComponent(returnUrl)}`)}
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

      {!info ? (
        <form onSubmit={handleLookup}>
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
          <Button type="submit" className="w-full" loading={loadingInfo}>
            Continue
          </Button>
        </form>
      ) : (
        <div>
          <div className="mb-4 rounded-md border border-gray-200 dark:border-gray-700 p-4">
            <div className="flex items-center gap-3 mb-3">
              {info.logoUri && (
                <img src={info.logoUri} alt="" className="h-8 w-8 rounded" />
              )}
              <div>
                <p className="font-medium">{info.clientName}</p>
                {info.clientUri && (
                  <p className="text-xs text-gray-500 dark:text-gray-400 break-all">{info.clientUri}</p>
                )}
              </div>
            </div>
            <p className="text-sm text-gray-500 dark:text-gray-400 mb-1">
              This application is requesting access to:
            </p>
            {info.scopes.length > 0 ? (
              <ul className="text-sm list-disc list-inside">
                {info.scopes.map((s) => (
                  <li key={s} className="font-mono">{s}</li>
                ))}
              </ul>
            ) : (
              <p className="text-sm italic text-gray-500 dark:text-gray-400">No permissions</p>
            )}
          </div>

          <p className="text-sm text-gray-500 dark:text-gray-400 mb-4">
            Only approve this if you started signing in on that device yourself.
          </p>

          <Button onClick={handleApprove} className="w-full mb-2" loading={loading}>
            Approve {info.clientName}
          </Button>
          <Button variant="secondary" className="w-full" onClick={() => { setInfo(null); setError(''); }}>
            Cancel
          </Button>
        </div>
      )}
    </div>
  );
}
