import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import MfaChallengePage from './MfaChallengePage';

// -------------------------------------------------------------------------------------------------
// The recovery-code field could not hold a recovery code.
//
// RecoveryCodeService generates 10 alphanumerics and presents them as `$"{code[..5]}-{code[5..]}"` —
// `XXXXX-XXXXX`, eleven characters. This input carried `maxLength={9}` and a placeholder advertising
// `XXXX-XXXX`, a 4-4 shape the server has never produced. The browser silently truncated every code
// to nine characters, the server rejected the remainder, and the page said "Invalid code. Please try
// again." — so a correct code, pasted correctly, failed as though the user had mistyped it.
//
// Recovery codes are the way back in after a lost authenticator, so this made them unusable in the
// hosted UI while pointing the blame at the user. Found by the deployed e2e (recovery-codes.spec.ts),
// which failed the same way twice.
//
// These assert the ATTRIBUTE rather than simulating typing. The truncation is browser behaviour that
// jsdom does not reproduce — `fireEvent.change` writes the value straight past `maxLength` — so a
// "type it and check it survived" test would pass against the broken code and prove nothing. The
// property worth holding is that the field never caps below what the server can present.
// -------------------------------------------------------------------------------------------------

vi.mock('../api', () => ({
  mfaVerify: vi.fn().mockResolvedValue({ redirectUrl: '/' }),
  ApiRequestError: class extends Error {},
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => key, i18n: { language: 'en' } }),
}));

/** Renders the challenge with both methods offered, as the server does after a TOTP enrolment. */
function renderChallenge() {
  render(
    <MemoryRouter initialEntries={['/login/mfa-challenge?challengeId=abc&methods=recoverycode,totp']}>
      <Routes>
        <Route path="/login/mfa-challenge" element={<MfaChallengePage />} />
      </Routes>
    </MemoryRouter>,
  );
  return document.getElementById('mfa-code') as HTMLInputElement;
}

/** The real presented shape: 10 alphanumerics split 5-5 by a dash. */
const REAL_CODE = 'P9FT3-WQ9XY';

describe('MFA challenge: the recovery-code field', () => {
  it('does not cap below the length of a real recovery code', () => {
    renderChallenge();
    fireEvent.click(screen.getByText('mfaMethodRecovery'));
    const input = document.getElementById('mfa-code') as HTMLInputElement;

    // -1 is jsdom's "unset". Anything else must be at least a full presented code; the old value was
    // 9 against an 11-character code.
    const max = input.maxLength;
    expect(
      max === -1 || max >= REAL_CODE.length,
      `recovery maxLength=${max}, shorter than a presented code (${REAL_CODE.length} chars)`,
    ).toBe(true);
  });

  it('advertises the shape the server actually presents', () => {
    renderChallenge();
    fireEvent.click(screen.getByText('mfaMethodRecovery'));
    const input = document.getElementById('mfa-code') as HTMLInputElement;

    // The placeholder is the only format guidance a user gets, and it said XXXX-XXXX. Matched against
    // the real 5-5 split rather than a literal, so reformatting the code changes this test honestly.
    expect(input.placeholder).toBe('XXXXX-XXXXX');
    expect(input.placeholder).toHaveLength(REAL_CODE.length);
  });

  it('still caps TOTP at six, which is what drives its auto-submit', () => {
    const input = renderChallenge();

    // Default method is the first offered; click TOTP explicitly so this does not depend on order.
    fireEvent.click(screen.getByText('mfaMethodTotp'));
    expect((document.getElementById('mfa-code') as HTMLInputElement).maxLength).toBe(6);
    expect(input).toBeTruthy();
  });
});
