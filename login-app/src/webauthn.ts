// WebAuthn (passkey) encoding + (de)serialization shared by the passkey flows. The serialized assertion
// shape matches what the server's Fido2 layer expects (AuthenticatorAssertionRawResponse).

export function base64UrlToBuffer(base64url: string): ArrayBuffer {
  const base64 = base64url.replace(/-/g, '+').replace(/_/g, '/');
  const pad = base64.length % 4 === 0 ? '' : '='.repeat(4 - (base64.length % 4));
  const binary = atob(base64 + pad);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes.buffer as ArrayBuffer;
}

export function bufferToBase64Url(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export interface AssertionOptionsJson {
  challenge: string;
  rpId?: string;
  timeout?: number;
  userVerification?: UserVerificationRequirement;
  allowCredentials?: { id: string; type: string; transports?: string[] }[];
}

/** Convert the server's JSON assertion options into a browser PublicKeyCredentialRequestOptions. */
export function toRequestOptions(options: AssertionOptionsJson): PublicKeyCredentialRequestOptions {
  return {
    challenge: base64UrlToBuffer(options.challenge),
    rpId: options.rpId,
    timeout: options.timeout || 60000,
    userVerification: options.userVerification || 'preferred',
    allowCredentials: (options.allowCredentials || []).map((c) => ({
      id: base64UrlToBuffer(c.id),
      type: c.type as PublicKeyCredentialType,
      transports: c.transports as AuthenticatorTransport[] | undefined,
    })),
  };
}

/** Serialize a completed assertion for the server (base64url), matching AuthenticatorAssertionRawResponse. */
export function serializeAssertion(credential: PublicKeyCredential): string {
  const response = credential.response as AuthenticatorAssertionResponse;
  return JSON.stringify({
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
}
