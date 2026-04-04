import { useState, useEffect } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { mfaStatus, mfaTotpSetup, mfaTotpConfirm, mfaWebAuthnSetup, mfaWebAuthnConfirm, mfaRecoveryGenerate, mfaDeleteCredential, ApiRequestError } from '../api';
import type { MfaMethod } from '../types';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle, CardDescription, CardFooter } from '@/components/ui/card';

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
  const backUrl = searchParams.get('backUrl') || '';
  const [enabled, setEnabled] = useState(false);
  const [methods, setMethods] = useState<MfaMethod[]>([]);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);

  // TOTP setup state
  const [totpSetup, setTotpSetup] = useState<{ setupToken: string; qrCodeDataUri: string; manualKey: string } | null>(null);
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
    return <div className="text-center py-10 text-gray-500">{t('mfaLoading')}</div>;
  }

  const hasTotp = methods.some(m => m.type === 'totp');
  const hasWebAuthn = methods.some(m => m.type === 'webauthn');
  const hasRecovery = methods.some(m => m.type === 'recoverycode');
  const hasPrimaryMethod = hasTotp || hasWebAuthn;
  const supportsWebAuthn = typeof window !== 'undefined' && !!window.PublicKeyCredential;

  return (
    <div>
      <CardTitle>{t('mfaSetupTitle')}</CardTitle>

      {error && <Alert variant="error">{error}</Alert>}

      <CardDescription className="mb-6">
        {enabled ? t('mfaStatusEnabled') : t('mfaStatusDisabled')}
      </CardDescription>

      {/* Enrolled methods */}
      {methods.filter(m => m.type !== 'recoverycode').length > 0 && (
        <div className="mb-6">
          <h3 className="text-base font-medium mb-2">{t('mfaEnrolledMethods')}</h3>
          {methods.filter(m => m.type !== 'recoverycode').map(m => (
            <div key={m.id} className="flex justify-between items-center p-3 border border-gray-200 rounded-lg mb-2">
              <div>
                <strong className="text-sm">{m.name}</strong>
                <br />
                <small className="text-gray-400 text-xs">{t('mfaAddedOn', { date: new Date(m.createdAt).toLocaleDateString() })}</small>
              </div>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                className="w-auto"
                onClick={() => handleDeleteCredential(m.id)}
              >
                {t('mfaRemove')}
              </Button>
            </div>
          ))}
        </div>
      )}

      {/* TOTP setup */}
      {!hasTotp && !totpSetup && (
        <Button className="mb-4" onClick={handleTotpSetup} disabled={totpLoading}>
          {t('mfaSetupTotp')}
        </Button>
      )}

      {totpSetup && (
        <div className="mb-6">
          <p className="text-center mb-4 text-sm">{t('mfaScanQrCode')}</p>
          <div className="text-center mb-4">
            <img src={totpSetup.qrCodeDataUri} alt="QR Code" className="w-[200px] h-[200px] mx-auto" style={{ imageRendering: 'pixelated' }} />
          </div>
          {totpSetup.manualKey && (
            <div className="text-center mb-4">
              <p className="text-[13px] text-gray-500 mb-1">{t('mfaManualKeyLabel')}</p>
              <code
                className="inline-block px-3 py-2 bg-gray-100 rounded-md text-sm tracking-widest select-all break-all cursor-pointer"
                title={t('mfaCopyKey')}
                onClick={() => navigator.clipboard?.writeText(totpSetup.manualKey)}
              >
                {totpSetup.manualKey}
              </code>
            </div>
          )}
          <div className="mb-4">
            <Label htmlFor="totp-confirm">{t('mfaEnterCode')}</Label>
            <Input
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
          <Button onClick={handleTotpConfirm} disabled={totpLoading || totpCode.length !== 6}>
            {t('mfaConfirm')}
          </Button>
        </div>
      )}

      {/* WebAuthn / Passkey setup */}
      {supportsWebAuthn && !hasWebAuthn && (
        <Button className="mb-4" onClick={handleWebAuthnSetup} disabled={webAuthnLoading}>
          {webAuthnLoading ? t('mfaLoading') : t('mfaSetupPasskey')}
        </Button>
      )}

      {/* Recovery codes */}
      {hasPrimaryMethod && !hasRecovery && !recoveryCodes && (
        <Button variant="secondary" className="mb-4" onClick={handleRecoveryGenerate} disabled={recoveryLoading}>
          {t('mfaGenerateRecoveryCodes')}
        </Button>
      )}

      {recoveryCodes && (
        <div className="mb-6">
          <h3 className="text-base font-medium mb-2">{t('mfaRecoveryCodesTitle')}</h3>
          <p className="text-gray-500 mb-3 text-sm">{t('mfaRecoveryCodesWarning')}</p>
          <div className="bg-gray-50 p-4 rounded-lg font-mono text-sm columns-2 gap-6">
            {recoveryCodes.map((code, i) => (
              <div key={i} className="mb-1">{code}</div>
            ))}
          </div>
          <Button variant="secondary" className="mt-3" onClick={() => setRecoveryCodes(null)}>
            {t('mfaDone')}
          </Button>
        </div>
      )}

      {hasRecovery && !recoveryCodes && (
        <Button variant="secondary" className="mb-4" onClick={handleRecoveryGenerate} disabled={recoveryLoading}>
          {t('mfaRegenerateRecoveryCodes')}
        </Button>
      )}

      {/* Skip button — only when not in forced setup mode (user has cookie session) */}
      {!mfaSetupToken && (
        <CardFooter className="mt-4">
          <button
            type="button"
            className="bg-transparent border-none cursor-pointer text-[13px] text-gray-500 hover:text-gray-700"
            onClick={() => {
              const dest = returnUrl && isSafeReturnUrl(returnUrl) ? returnUrl : '/';
              window.location.href = dest;
            }}
          >
            {t('mfaSkipSetup')}
          </button>
        </CardFooter>
      )}

      {/* Back to app link — shown when navigating from an external app */}
      {backUrl && (
        <div className="mt-6 text-center pt-4 border-t border-gray-200">
          <a href={backUrl} className="text-sm text-primary no-underline hover:underline">
            &larr; {t('mfaBackToApp')}
          </a>
        </div>
      )}
    </div>
  );
}
