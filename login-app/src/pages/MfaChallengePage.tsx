import { useState, useCallback } from 'react';
import { useSearchParams, Link } from 'react-router';
import { useTranslation } from 'react-i18next';
import { mfaVerify, ApiRequestError } from '../api';
import { resolveRedirect } from '@/lib/returnUrl';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle, CardDescription } from '@/components/ui/card';

// Helper: Base64URL decode to Uint8Array
function base64UrlToBuffer(base64url: string): ArrayBuffer {
  const base64 = base64url.replace(/-/g, '+').replace(/_/g, '/');
  const pad = base64.length % 4 === 0 ? '' : '='.repeat(4 - (base64.length % 4));
  const binary = atob(base64 + pad);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes.buffer as ArrayBuffer;
}

// Helper: ArrayBuffer to Base64URL
function bufferToBase64Url(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export default function MfaChallengePage() {
  const { t } = useTranslation();
  const [searchParams] = useSearchParams();
  const challengeId = searchParams.get('challengeId') || '';
  const returnUrl = searchParams.get('returnUrl') || '';
  const methodsParam = searchParams.get('methods') || '';
  const availableMethods = methodsParam ? methodsParam.split(',') : [];

  // Escape hatch: the page state is driven entirely by the URL, so a stale/expired challengeId would
  // otherwise trap the user on this form (every submit re-POSTs the dead challenge). Always offer a
  // way back to the login form, preserving the OIDC returnUrl so the flow resumes. The App router's
  // basename is /login, so the target is app-relative "/" (LoginPage) — NOT "/login" (which would
  // resolve to /login/login, miss every route, and hit the catch-all redirect that drops the query).
  const loginLink = returnUrl
    ? `/?returnUrl=${encodeURIComponent(returnUrl)}`
    : '/';

  const hasWebAuthn = availableMethods.includes('webauthn');
  // Default to a device-independent factor (TOTP) when the user has one, so a login on a device that
  // doesn't have the passkey is never pushed toward it — passkey stays a one-tap choice, not the forced
  // default. (We also never auto-invoke the passkey; it only fires on an explicit tap.)
  const defaultMethod = availableMethods.includes('totp') ? 'totp'
    : hasWebAuthn ? 'webauthn'
    : availableMethods[0] || 'totp';

  const [method, setMethod] = useState(defaultMethod);
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const handleSuccess = useCallback(() => {
    void resolveRedirect(returnUrl, () => '/').then((target) => {
      window.location.href = target;
    });
  }, [returnUrl]);

  const handleError = useCallback((err: unknown) => {
    if (err instanceof ApiRequestError) {
      switch (err.error) {
        case 'invalid_code':
        case 'assertion_failed':
          setError(t('mfaInvalidCode'));
          break;
        case 'invalid_challenge':
          setError(t('mfaChallengeExpired'));
          break;
        case 'too_many_attempts':
          setError(t('mfaTooManyAttempts'));
          break;
        default:
          setError(err.message || t('errorUnexpected'));
      }
    } else {
      setError(t('errorUnexpected'));
    }
  }, [t]);

  async function handleWebAuthn() {
    setError('');
    setLoading(true);

    try {
      // Get webAuthn options from the search params (stored as JSON in the URL)
      const webAuthnOptionsParam = searchParams.get('webAuthn');
      if (!webAuthnOptionsParam) {
        setError(t('errorUnexpected'));
        return;
      }

      const options = JSON.parse(webAuthnOptionsParam);

      // Convert challenge and allowCredentials from Base64URL to ArrayBuffer
      const publicKeyOptions: PublicKeyCredentialRequestOptions = {
        challenge: base64UrlToBuffer(options.challenge),
        rpId: options.rpId,
        timeout: options.timeout || 60000,
        userVerification: options.userVerification || 'preferred',
        allowCredentials: (options.allowCredentials || []).map((c: { id: string; type: string; transports?: string[] }) => ({
          id: base64UrlToBuffer(c.id),
          type: c.type,
          transports: c.transports,
        })),
      };

      const credential = await navigator.credentials.get({ publicKey: publicKeyOptions }) as PublicKeyCredential;
      if (!credential) {
        setError(t('errorUnexpected'));
        return;
      }

      const response = credential.response as AuthenticatorAssertionResponse;
      const assertionJson = JSON.stringify({
        id: credential.id,
        rawId: bufferToBase64Url(credential.rawId),
        type: credential.type,
        response: {
          authenticatorData: bufferToBase64Url(response.authenticatorData),
          clientDataJSON: bufferToBase64Url(response.clientDataJSON),
          signature: bufferToBase64Url(response.signature),
          userHandle: response.userHandle ? bufferToBase64Url(response.userHandle) : null,
        },
      });

      await mfaVerify(challengeId, 'webauthn', undefined, assertionJson);
      handleSuccess();
    } catch (err) {
      if (err instanceof DOMException && err.name === 'NotAllowedError') {
        setError(t('mfaWebAuthnCancelled'));
      } else {
        handleError(err);
      }
    } finally {
      setLoading(false);
    }
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!code.trim()) return;
    setError('');
    setLoading(true);

    try {
      await mfaVerify(challengeId, method, code);
      handleSuccess();
    } catch (err) {
      handleError(err);
    } finally {
      setLoading(false);
    }
  }

  function handleCodeChange(value: string) {
    setCode(value);
    // Auto-submit on 6 digits for TOTP
    if (method === 'totp' && value.replace(/\s/g, '').length === 6) {
      setTimeout(() => {
        const form = document.getElementById('mfa-form') as HTMLFormElement;
        form?.requestSubmit();
      }, 100);
    }
  }

  return (
    <div>
      <CardTitle>{t('mfaTitle')}</CardTitle>
      <CardDescription className="mb-6">{t('mfaSubtitle')}</CardDescription>

      {availableMethods.length > 1 && (
        <div className="flex gap-2 mb-4 justify-center flex-wrap">
          {hasWebAuthn && (
            <Button
              type="button"
              variant={method === 'webauthn' ? 'default' : 'secondary'}
              size="sm"
              className="flex-1 whitespace-nowrap"
              onClick={() => { setMethod('webauthn'); setCode(''); setError(''); }}
            >
              {t('mfaMethodWebAuthn')}
            </Button>
          )}
          {availableMethods.includes('totp') && (
            <Button
              type="button"
              variant={method === 'totp' ? 'default' : 'secondary'}
              size="sm"
              className="flex-1 whitespace-nowrap"
              onClick={() => { setMethod('totp'); setCode(''); setError(''); }}
            >
              {t('mfaMethodTotp')}
            </Button>
          )}
          {availableMethods.includes('recoverycode') && (
            <Button
              type="button"
              variant={method === 'recovery' ? 'default' : 'secondary'}
              size="sm"
              className="flex-1 whitespace-nowrap"
              onClick={() => { setMethod('recovery'); setCode(''); setError(''); }}
            >
              {t('mfaMethodRecovery')}
            </Button>
          )}
        </div>
      )}

      {error && <Alert variant="error">{error}</Alert>}

      {method === 'webauthn' ? (
        <Button type="button" loading={loading} onClick={handleWebAuthn}>
          {loading ? t('mfaVerifying') : t('mfaUsePasskey')}
        </Button>
      ) : (
        <form id="mfa-form" onSubmit={handleSubmit}>
          <div className="mb-4">
            <Label htmlFor="mfa-code">
              {method === 'totp' ? t('mfaTotpLabel') : t('mfaRecoveryLabel')}
            </Label>
            <Input
              id="mfa-code"
              type="text"
              value={code}
              onChange={(e) => handleCodeChange(e.target.value)}
              placeholder={method === 'totp' ? '000000' : 'XXXXX-XXXXX'}
              autoComplete="one-time-code"
              autoFocus
              // No maxLength for a recovery code. It was 9, against a code the server presents as
              // `XXXXX-XXXXX`: RecoveryCodeService generates 10 alphanumerics and renders them
              // `$"{code[..5]}-{code[5..]}"`, so 11 characters. The browser truncated every code to 9,
              // and the verify then failed as "Invalid code. Please try again." — which reads as a
              // mistyped code rather than a field that refused to hold the right one. Recovery codes
              // are the way back in after a lost authenticator, so they were unusable here while
              // looking like user error. The placeholder advertised the same wrong 4-4 shape.
              //
              // Left unbounded rather than raised to 11: the server normalises by stripping dashes and
              // spaces before verifying, so a paste carrying either still has to work, and a length cap
              // on this input buys nothing it has not already cost. TOTP keeps 6 — that drives its
              // auto-submit.
              maxLength={method === 'totp' ? 6 : undefined}
              inputMode={method === 'totp' ? 'numeric' : 'text'}
              pattern={method === 'totp' ? '[0-9]{6}' : undefined}
              required
            />
          </div>

          <Button type="submit" loading={loading}>
            {loading ? t('mfaVerifying') : t('mfaVerify')}
          </Button>
        </form>
      )}

      <div className="mt-6 text-center">
        <Link to={loginLink} className="text-sm font-medium text-primary hover:underline no-underline">
          {t('backToSignIn')}
        </Link>
      </div>
    </div>
  );
}
