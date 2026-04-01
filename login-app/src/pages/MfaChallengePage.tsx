import { useState, useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { mfaVerify, ApiRequestError } from '../api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle, CardDescription } from '@/components/ui/card';

function isSafeReturnUrl(url: string): boolean {
  if (!url) return false;
  try {
    const parsed = new URL(url, window.location.origin);
    return parsed.origin === window.location.origin && url.startsWith('/');
  } catch {
    return false;
  }
}

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

  const hasWebAuthn = availableMethods.includes('webauthn');
  const defaultMethod = hasWebAuthn ? 'webauthn'
    : availableMethods.includes('totp') ? 'totp'
    : availableMethods[0] || 'totp';

  const [method, setMethod] = useState(defaultMethod);
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const handleSuccess = useCallback(() => {
    if (returnUrl && isSafeReturnUrl(returnUrl)) {
      window.location.href = returnUrl;
    } else {
      window.location.href = '/';
    }
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
              className="flex-1"
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
              className="flex-1"
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
              className="flex-1"
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
              placeholder={method === 'totp' ? '000000' : 'XXXX-XXXX'}
              autoComplete="one-time-code"
              autoFocus
              maxLength={method === 'totp' ? 6 : 9}
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
    </div>
  );
}
