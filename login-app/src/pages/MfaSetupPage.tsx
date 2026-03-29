import { useState, useEffect } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { mfaStatus, mfaTotpSetup, mfaTotpConfirm, mfaWebAuthnSetup, mfaWebAuthnConfirm, mfaRecoveryGenerate, mfaDeleteCredential, ApiRequestError } from '../api';
import type { MfaMethod } from '../types';

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

export default function MfaSetupPage() {
  const { t } = useTranslation();
  const [searchParams] = useSearchParams();
  const mfaSetupToken = searchParams.get('setupToken') || undefined;
  const returnUrl = searchParams.get('returnUrl') || '';
  const [enabled, setEnabled] = useState(false);
  const [methods, setMethods] = useState<MfaMethod[]>([]);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);

  // TOTP setup state
  const [totpSetup, setTotpSetup] = useState<{ setupToken: string; qrCodeDataUri: string } | null>(null);
  const [totpCode, setTotpCode] = useState('');
  const [totpLoading, setTotpLoading] = useState(false);

  // WebAuthn state
  const [webAuthnLoading, setWebAuthnLoading] = useState(false);

  // Recovery codes state
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);
  const [recoveryLoading, setRecoveryLoading] = useState(false);

  useEffect(() => {
    loadStatus();
  }, []);

  async function loadStatus() {
    try {
      const status = await mfaStatus(mfaSetupToken);
      setEnabled(status.enabled);
      setMethods(status.methods);
    } catch {
      setError(t('errorUnexpected'));
    } finally {
      setLoading(false);
    }
  }

  // When using a setup token, redirect after MFA is successfully set up
  function handleSetupComplete() {
    if (mfaSetupToken) {
      // Server signed the cookie — redirect to the original destination
      if (returnUrl && isSafeReturnUrl(returnUrl)) {
        window.location.href = returnUrl;
      } else {
        window.location.href = '/';
      }
    }
  }

  async function handleTotpSetup() {
    setError('');
    setTotpLoading(true);
    try {
      const result = await mfaTotpSetup(mfaSetupToken);
      setTotpSetup(result);
    } catch (err) {
      if (err instanceof ApiRequestError && err.error === 'totp_already_enrolled') {
        setError(t('mfaTotpAlreadyEnrolled'));
      } else {
        setError(t('errorUnexpected'));
      }
    } finally {
      setTotpLoading(false);
    }
  }

  async function handleTotpConfirm() {
    if (!totpSetup || !totpCode) return;
    setError('');
    setTotpLoading(true);
    try {
      await mfaTotpConfirm(totpSetup.setupToken, totpCode, mfaSetupToken);
      setTotpSetup(null);
      setTotpCode('');
      handleSetupComplete();
      await loadStatus();
    } catch (err) {
      if (err instanceof ApiRequestError && err.error === 'invalid_code') {
        setError(t('mfaInvalidCode'));
      } else {
        setError(t('errorUnexpected'));
      }
    } finally {
      setTotpLoading(false);
    }
  }

  async function handleWebAuthnSetup() {
    setError('');
    setWebAuthnLoading(true);
    try {
      const { setupToken, options } = await mfaWebAuthnSetup(mfaSetupToken);

      // Convert options for the browser API
      const publicKeyOptions: PublicKeyCredentialCreationOptions = {
        challenge: base64UrlToBuffer(options.challenge),
        rp: options.rp,
        user: {
          ...options.user,
          id: base64UrlToBuffer(options.user.id),
        },
        pubKeyCredParams: options.pubKeyCredParams,
        timeout: options.timeout || 60000,
        attestation: options.attestation || 'none',
        authenticatorSelection: options.authenticatorSelection,
        excludeCredentials: (options.excludeCredentials || []).map((c: { id: string; type: string; transports?: string[] }) => ({
          id: base64UrlToBuffer(c.id),
          type: c.type,
          transports: c.transports,
        })),
      };

      const credential = await navigator.credentials.create({ publicKey: publicKeyOptions }) as PublicKeyCredential;
      if (!credential) {
        setError(t('errorUnexpected'));
        return;
      }

      const response = credential.response as AuthenticatorAttestationResponse;
      const attestationJson = JSON.stringify({
        id: credential.id,
        rawId: bufferToBase64Url(credential.rawId),
        type: credential.type,
        response: {
          attestationObject: bufferToBase64Url(response.attestationObject),
          clientDataJSON: bufferToBase64Url(response.clientDataJSON),
        },
      });

      await mfaWebAuthnConfirm(setupToken, attestationJson, mfaSetupToken);
      handleSetupComplete();
      await loadStatus();
    } catch (err) {
      if (err instanceof DOMException && err.name === 'NotAllowedError') {
        setError(t('mfaWebAuthnCancelled'));
      } else if (err instanceof ApiRequestError) {
        setError(err.message || t('errorUnexpected'));
      } else {
        setError(t('errorUnexpected'));
      }
    } finally {
      setWebAuthnLoading(false);
    }
  }

  async function handleRecoveryGenerate() {
    setError('');
    setRecoveryLoading(true);
    try {
      const result = await mfaRecoveryGenerate(mfaSetupToken);
      setRecoveryCodes(result.codes);
      await loadStatus();
    } catch (err) {
      if (err instanceof ApiRequestError && err.error === 'primary_method_required') {
        setError(t('mfaRecoveryRequiresPrimary'));
      } else {
        setError(t('errorUnexpected'));
      }
    } finally {
      setRecoveryLoading(false);
    }
  }

  async function handleDeleteCredential(credentialId: string) {
    setError('');
    try {
      await mfaDeleteCredential(credentialId, mfaSetupToken);
      await loadStatus();
    } catch {
      setError(t('errorUnexpected'));
    }
  }

  if (loading) {
    return <div style={{ textAlign: 'center', padding: '40px' }}>{t('mfaLoading')}</div>;
  }

  const hasTotp = methods.some(m => m.type === 'totp');
  const hasWebAuthn = methods.some(m => m.type === 'webauthn');
  const hasRecovery = methods.some(m => m.type === 'recoverycode');
  const hasPrimaryMethod = hasTotp || hasWebAuthn;
  const supportsWebAuthn = typeof window !== 'undefined' && !!window.PublicKeyCredential;

  return (
    <div>
      <h2 className="auth-title">{t('mfaSetupTitle')}</h2>

      {error && <div className="alert-error">{error}</div>}

      <div style={{ marginBottom: '24px' }}>
        <p style={{ color: '#6b7280', textAlign: 'center' }}>
          {enabled ? t('mfaStatusEnabled') : t('mfaStatusDisabled')}
        </p>
      </div>

      {/* Enrolled methods */}
      {methods.filter(m => m.type !== 'recoverycode').length > 0 && (
        <div style={{ marginBottom: '24px' }}>
          <h3 style={{ fontSize: '16px', marginBottom: '8px' }}>{t('mfaEnrolledMethods')}</h3>
          {methods.filter(m => m.type !== 'recoverycode').map(m => (
            <div key={m.id} style={{
              display: 'flex', justifyContent: 'space-between', alignItems: 'center',
              padding: '12px', border: '1px solid #e5e7eb', borderRadius: '8px', marginBottom: '8px'
            }}>
              <div>
                <strong>{m.name}</strong>
                <br />
                <small style={{ color: '#9ca3af' }}>{t('mfaAddedOn', { date: new Date(m.createdAt).toLocaleDateString() })}</small>
              </div>
              <button
                type="button"
                className="btn-secondary"
                style={{ fontSize: '13px', padding: '6px 12px' }}
                onClick={() => handleDeleteCredential(m.id)}
              >
                {t('mfaRemove')}
              </button>
            </div>
          ))}
        </div>
      )}

      {/* TOTP setup */}
      {!hasTotp && !totpSetup && (
        <button
          type="button"
          className="btn-primary"
          style={{ marginBottom: '16px', width: '100%' }}
          onClick={handleTotpSetup}
          disabled={totpLoading}
        >
          {t('mfaSetupTotp')}
        </button>
      )}

      {totpSetup && (
        <div style={{ marginBottom: '24px' }}>
          <p style={{ textAlign: 'center', marginBottom: '16px' }}>{t('mfaScanQrCode')}</p>
          <div style={{ textAlign: 'center', marginBottom: '16px' }}>
            <img src={totpSetup.qrCodeDataUri} alt="QR Code" style={{ width: '200px', height: '200px', imageRendering: 'pixelated' }} />
          </div>
          {totpSetup.manualKey && (
            <div style={{ textAlign: 'center', marginBottom: '16px' }}>
              <p style={{ fontSize: '13px', color: '#6b7280', marginBottom: '4px' }}>{t('mfaManualKeyLabel')}</p>
              <code
                style={{
                  display: 'inline-block', padding: '8px 12px', background: '#f3f4f6',
                  borderRadius: '6px', fontSize: '14px', letterSpacing: '2px', userSelect: 'all',
                  wordBreak: 'break-all', cursor: 'pointer',
                }}
                title={t('mfaCopyKey')}
                onClick={() => navigator.clipboard?.writeText(totpSetup.manualKey)}
              >
                {totpSetup.manualKey}
              </code>
            </div>
          )}
          <div className="form-group">
            <label htmlFor="totp-confirm">{t('mfaEnterCode')}</label>
            <input
              id="totp-confirm"
              type="text"
              value={totpCode}
              onChange={(e) => setTotpCode(e.target.value)}
              placeholder="000000"
              autoComplete="one-time-code"
              maxLength={6}
              inputMode="numeric"
              pattern="[0-9]{6}"
            />
          </div>
          <button
            type="button"
            className="btn-primary"
            style={{ width: '100%' }}
            onClick={handleTotpConfirm}
            disabled={totpLoading || totpCode.length !== 6}
          >
            {t('mfaConfirm')}
          </button>
        </div>
      )}

      {/* WebAuthn / Passkey setup */}
      {supportsWebAuthn && !hasWebAuthn && (
        <button
          type="button"
          className="btn-primary"
          style={{ marginBottom: '16px', width: '100%' }}
          onClick={handleWebAuthnSetup}
          disabled={webAuthnLoading}
        >
          {webAuthnLoading ? t('mfaLoading') : t('mfaSetupPasskey')}
        </button>
      )}

      {/* Recovery codes */}
      {hasPrimaryMethod && !hasRecovery && !recoveryCodes && (
        <button
          type="button"
          className="btn-secondary"
          style={{ marginBottom: '16px', width: '100%' }}
          onClick={handleRecoveryGenerate}
          disabled={recoveryLoading}
        >
          {t('mfaGenerateRecoveryCodes')}
        </button>
      )}

      {recoveryCodes && (
        <div style={{ marginBottom: '24px' }}>
          <h3 style={{ fontSize: '16px', marginBottom: '8px' }}>{t('mfaRecoveryCodesTitle')}</h3>
          <p style={{ color: '#6b7280', marginBottom: '12px', fontSize: '14px' }}>
            {t('mfaRecoveryCodesWarning')}
          </p>
          <div style={{
            background: '#f9fafb', padding: '16px', borderRadius: '8px',
            fontFamily: 'monospace', fontSize: '14px', columns: 2, columnGap: '24px'
          }}>
            {recoveryCodes.map((code, i) => (
              <div key={i} style={{ marginBottom: '4px' }}>{code}</div>
            ))}
          </div>
          <button
            type="button"
            className="btn-secondary"
            style={{ marginTop: '12px', width: '100%' }}
            onClick={() => setRecoveryCodes(null)}
          >
            {t('mfaDone')}
          </button>
        </div>
      )}

      {hasRecovery && !recoveryCodes && (
        <button
          type="button"
          className="btn-secondary"
          style={{ marginBottom: '16px', width: '100%' }}
          onClick={handleRecoveryGenerate}
          disabled={recoveryLoading}
        >
          {t('mfaRegenerateRecoveryCodes')}
        </button>
      )}

      {/* Skip button — only when not in forced setup mode (user has cookie session) */}
      {!mfaSetupToken && (
        <div className="form-footer" style={{ marginTop: '16px' }}>
          <button
            type="button"
            className="link"
            style={{ background: 'none', border: 'none', cursor: 'pointer', fontSize: '13px', color: '#6b7280' }}
            onClick={() => {
              const dest = returnUrl && isSafeReturnUrl(returnUrl) ? returnUrl : '/';
              window.location.href = dest;
            }}
          >
            {t('mfaSkipSetup')}
          </button>
        </div>
      )}
    </div>
  );
}
