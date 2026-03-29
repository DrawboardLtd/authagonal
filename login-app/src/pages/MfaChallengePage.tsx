import { useState, useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { mfaVerify, ApiRequestError } from '../api';

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
      <h2 className="auth-title">{t('mfaTitle')}</h2>
      <p style={{ textAlign: 'center', color: '#6b7280', marginBottom: '24px' }}>
        {t('mfaSubtitle')}
      </p>

      {availableMethods.length > 1 && (
        <div style={{ display: 'flex', gap: '8px', marginBottom: '16px', justifyContent: 'center', flexWrap: 'wrap' }}>
          {hasWebAuthn && (
            <button
              type="button"
              className={method === 'webauthn' ? 'btn-primary' : 'btn-secondary'}
              style={{ flex: 1, fontSize: '14px', padding: '8px' }}
              onClick={() => { setMethod('webauthn'); setCode(''); setError(''); }}
            >
              {t('mfaMethodWebAuthn')}
            </button>
          )}
          {availableMethods.includes('totp') && (
            <button
              type="button"
              className={method === 'totp' ? 'btn-primary' : 'btn-secondary'}
              style={{ flex: 1, fontSize: '14px', padding: '8px' }}
              onClick={() => { setMethod('totp'); setCode(''); setError(''); }}
            >
              {t('mfaMethodTotp')}
            </button>
          )}
          {availableMethods.includes('recoverycode') && (
            <button
              type="button"
              className={method === 'recovery' ? 'btn-primary' : 'btn-secondary'}
              style={{ flex: 1, fontSize: '14px', padding: '8px' }}
              onClick={() => { setMethod('recovery'); setCode(''); setError(''); }}
            >
              {t('mfaMethodRecovery')}
            </button>
          )}
        </div>
      )}

      {error && <div className="alert-error">{error}</div>}

      {method === 'webauthn' ? (
        <div>
          <button
            type="button"
            className="btn-primary"
            disabled={loading}
            onClick={handleWebAuthn}
            style={{ width: '100%' }}
          >
            {loading ? (
              <span className="btn-loading">
                <span className="spinner" />
                {t('mfaVerifying')}
              </span>
            ) : (
              t('mfaUsePasskey')
            )}
          </button>
        </div>
      ) : (
        <form id="mfa-form" onSubmit={handleSubmit}>
          <div className="form-group">
            <label htmlFor="mfa-code">
              {method === 'totp' ? t('mfaTotpLabel') : t('mfaRecoveryLabel')}
            </label>
            <input
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

          <button type="submit" className="btn-primary" disabled={loading}>
            {loading ? (
              <span className="btn-loading">
                <span className="spinner" />
                {t('mfaVerifying')}
              </span>
            ) : (
              t('mfaVerify')
            )}
          </button>
        </form>
      )}
    </div>
  );
}
