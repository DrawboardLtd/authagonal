import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { getProfile, updateProfile, mfaStatus, getApps, exportMyData, requestErasure, listSessions, revokeSession, revokeOtherSessions, ApiRequestError } from '../api';
import type { AppLinkResponse, ActiveSession } from '../types';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle } from '@/components/ui/card';
// The shipped-locale registry — one list drives i18next registration AND every picker, so this
// select can't drift from the languages we actually ship (see i18n/index.ts). The chosen value is
// the user's preferred UI/communication language; emails localise to it (falling back to English
// for any language we don't template). The OPTIONS honour branding.languages with the shipped
// default as fallback; LANGUAGES stays the validity set for normalization.
import { LANGUAGES, DEFAULT_LANGUAGES } from '../i18n';
import { useBranding } from '../branding';

// Resolve any tag (stored locale, browser language, region variant) to one of our option codes —
// the controlled <select> must always hold a value that exists in LANGUAGES. zh* → zh-Hans,
// "de-DE" → "de", unknown → "en".
function toLangOption(code?: string): string {
  if (!code) return 'en';
  if (LANGUAGES.some((l) => l.code === code)) return code;
  if (code.toLowerCase().startsWith('zh')) return 'zh-Hans';
  const base = code.split('-')[0];
  return LANGUAGES.some((l) => l.code === base) ? base : 'en';
}

export default function AccountPage() {
  const { t, i18n } = useTranslation();
  const branding = useBranding();
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [unauthenticated, setUnauthenticated] = useState(false);
  const [saved, setSaved] = useState(false);
  const [mfaOffered, setMfaOffered] = useState(false);
  const [apps, setApps] = useState<AppLinkResponse[]>([]);
  const [email, setEmail] = useState('');
  const [form, setForm] = useState({ firstName: '', lastName: '', companyName: '', phone: '', locale: '' });
  // "Your data" (GDPR) section state.
  const [exporting, setExporting] = useState(false);
  const [showDelete, setShowDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState('');
  const [deleteQueued, setDeleteQueued] = useState(false);
  const [sessions, setSessions] = useState<ActiveSession[]>([]);
  const [sessionsBusy, setSessionsBusy] = useState(false);

  async function handleExport() {
    setExporting(true);
    try {
      const blob = await exportMyData();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'authagonal-data-export.json';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch {
      setError(t('account.exportError', 'Could not export your data. Please try again.'));
    } finally {
      setExporting(false);
    }
  }

  async function handleDelete() {
    setDeleting(true);
    setDeleteError('');
    try {
      await requestErasure();
      setDeleteQueued(true);
      setShowDelete(false);
    } catch (e) {
      if (e instanceof ApiRequestError && e.error === 'last_owner_cannot_erase') {
        setDeleteError(t('account.deleteOwnerError', 'You own this account and cannot delete your own record. Transfer ownership first.'));
      } else {
        setDeleteError(t('account.deleteError', 'Could not schedule deletion. Please try again.'));
      }
    } finally {
      setDeleting(false);
    }
  }

  useEffect(() => {
    getProfile()
      .then((p) => {
        setEmail(p.email ?? '');
        setForm({
          firstName: p.firstName ?? '',
          lastName: p.lastName ?? '',
          companyName: p.companyName ?? '',
          phone: p.phone ?? '',
          // Resolve to a real option: their stored locale, else the current UI language.
          locale: toLangOption(p.locale ?? i18n.language),
        });
      })
      .catch((e) => {
        if (e instanceof ApiRequestError && (e.error === 'unauthorized' || e.error === 'not_authenticated')) {
          setUnauthenticated(true);
        } else {
          setError(t('account.loadError'));
        }
      })
      .finally(() => setLoading(false));
  }, [t]);

  // MFA setup is only advertised when the tenant offers it (some client's policy is not Disabled) —
  // don't show a "set up 2FA" prompt on a tenant that has turned MFA off.
  useEffect(() => {
    mfaStatus().then((s) => setMfaOffered(s.offered !== false)).catch(() => {});
  }, []);

  // Server-side sessions: only surfaced when the tenant tracks them (an empty list = feature off, so the
  // whole section stays hidden). Newest activity first, current device flagged.
  useEffect(() => {
    listSessions().then((r) => setSessions(r.sessions)).catch(() => {});
  }, []);

  async function handleRevokeSession(id: string) {
    setSessionsBusy(true);
    try {
      await revokeSession(id);
      setSessions((prev) => prev.filter((s) => s.sessionId !== id));
    } catch { /* transient failure: leave the list as-is */ }
    finally { setSessionsBusy(false); }
  }

  async function handleRevokeOthers() {
    setSessionsBusy(true);
    try {
      await revokeOtherSessions();
      setSessions((prev) => prev.filter((s) => s.current));
    } catch { /* transient failure: leave the list as-is */ }
    finally { setSessionsBusy(false); }
  }

  // "Back to app" targets: clients the operator gave a home URI. Absent/failed → no button,
  // the account page stays the destination (the pre-existing behavior).
  useEffect(() => {
    getApps().then(setApps).catch(() => {});
  }, []);

  // Switch the live UI immediately as a preview; the choice only persists on Save.
  function changeLocale(code: string) {
    setForm((f) => ({ ...f, locale: code }));
    setSaved(false);
    if (code) i18n.changeLanguage(code);
  }

  async function handleSave(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true); setError(''); setSaved(false);
    try {
      const updated = await updateProfile({
        firstName: form.firstName,
        lastName: form.lastName,
        companyName: form.companyName,
        phone: form.phone,
        locale: form.locale,
      });
      if (updated.locale) i18n.changeLanguage(updated.locale);
      setSaved(true);
    } catch {
      setError(t('account.saveError'));
    } finally {
      setSaving(false);
    }
  }

  if (unauthenticated) {
    return (
      <>
        <CardTitle>{t('account.title')}</CardTitle>
        <p className="text-sm text-gray-500 dark:text-gray-400 mb-4">{t('account.signInPrompt')}</p>
        <Link to="/"><Button className="w-full">{t('signIn')}</Button></Link>
      </>
    );
  }

  const defaultApp = apps.find((a) => a.isDefault) ?? null;
  const otherApps = apps.filter((a) => a !== defaultApp);

  return (
    <>
      {apps.length > 0 && (
        <div className="mb-4">
          {defaultApp && (
            <a href={defaultApp.homeUri} data-testid="back-to-app">
              <Button variant="secondary" className="w-full">
                {defaultApp.logoUri && <img src={defaultApp.logoUri} alt="" className="h-4 w-4 me-2 rounded-sm" />}
                {t('account.backToApp', { app: defaultApp.clientName })}
              </Button>
            </a>
          )}
          {otherApps.length > 0 && (
            <div className="mt-2 flex flex-wrap gap-x-3 gap-y-1 justify-center">
              {otherApps.map((a) => (
                <a key={a.clientId} href={a.homeUri}
                   className="text-xs text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200 underline underline-offset-2">
                  {a.clientName}
                </a>
              ))}
            </div>
          )}
        </div>
      )}

      <CardTitle>{t('account.title')}</CardTitle>
      <p className="text-sm text-gray-500 dark:text-gray-400 mb-4">{t('account.subtitle')}</p>

      {error && <Alert variant="error">{error}</Alert>}
      {saved && <Alert variant="success">{t('account.saved')}</Alert>}

      {loading ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">{t('account.loading')}</p>
      ) : (
        <>
        <form onSubmit={handleSave} className="space-y-4">
          <div>
            <Label htmlFor="acc-email">{t('email')}</Label>
            <Input id="acc-email" type="email" value={email} disabled readOnly />
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <Label htmlFor="acc-first">{t('firstName')}</Label>
              <Input id="acc-first" value={form.firstName} onChange={(e) => { setForm((f) => ({ ...f, firstName: e.target.value })); setSaved(false); }} />
            </div>
            <div>
              <Label htmlFor="acc-last">{t('lastName')}</Label>
              <Input id="acc-last" value={form.lastName} onChange={(e) => { setForm((f) => ({ ...f, lastName: e.target.value })); setSaved(false); }} />
            </div>
          </div>
          <div>
            <Label htmlFor="acc-company">{t('account.companyName')}</Label>
            <Input id="acc-company" value={form.companyName} onChange={(e) => { setForm((f) => ({ ...f, companyName: e.target.value })); setSaved(false); }} />
          </div>
          <div>
            <Label htmlFor="acc-phone">{t('account.phone')}</Label>
            <Input id="acc-phone" value={form.phone} onChange={(e) => { setForm((f) => ({ ...f, phone: e.target.value })); setSaved(false); }} />
          </div>
          <div>
            <Label htmlFor="acc-language">{t('account.language')}</Label>
            <select
              id="acc-language"
              value={form.locale}
              onChange={(e) => changeLocale(e.target.value)}
              className="flex h-11 w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-base text-gray-900 transition-colors focus-visible:outline-none focus-visible:border-primary focus-visible:ring-[3px] focus-visible:ring-primary/15 dark:border-gray-700 dark:bg-gray-900 dark:text-gray-100"
            >
              {(() => {
                const opts = branding.languages ?? DEFAULT_LANGUAGES;
                // A locale chosen while it was offered (e.g. one a tenant later dropped from
                // branding.languages) must stay renderable — a controlled <select> whose value is
                // missing from its options displays blank. Append the known entry for the active value.
                const withActive = form.locale && !opts.some((l) => l.code === form.locale)
                  ? [...opts, ...LANGUAGES.filter((l) => l.code === form.locale).map(({ code, label }) => ({ code, label }))]
                  : opts;
                return withActive.map((l) => <option key={l.code} value={l.code}>{l.label}</option>);
              })()}
            </select>
          </div>
          <Button type="submit" className="w-full" loading={saving}>
            {saving ? t('account.saving') : t('account.save')}
          </Button>
        </form>

        {mfaOffered && (
        <div className="mt-6 border-t border-gray-200 dark:border-gray-800 pt-4">
          <h2 className="text-sm font-medium text-gray-900 dark:text-white mb-1">{t('account.security', 'Security')}</h2>
          <p className="text-sm text-gray-500 dark:text-gray-400 mb-3">{t('account.securitySubtitle', 'Add two-factor authentication to keep your account secure.')}</p>
          <Link to="/mfa-setup">
            <Button type="button" variant="secondary" className="w-full">{t('account.setupMfa', 'Set up two-factor authentication')}</Button>
          </Link>
        </div>
        )}

        {/* Active sessions — server-side SSO sessions, self-service revocation. Hidden when the tenant
            doesn't track sessions (empty list). */}
        {sessions.length > 0 && (
        <div className="mt-6 border-t border-gray-200 dark:border-gray-800 pt-4">
          <div className="flex items-center justify-between mb-1">
            <h2 className="text-sm font-medium text-gray-900 dark:text-white">{t('account.sessionsTitle', 'Active sessions')}</h2>
            {sessions.some((s) => !s.current) && (
              <button type="button" onClick={handleRevokeOthers} disabled={sessionsBusy}
                className="text-xs text-red-600 hover:text-red-700 dark:text-red-400 underline underline-offset-2 disabled:opacity-50">
                {t('account.sessionsLogoutOthers', 'Log out other devices')}
              </button>
            )}
          </div>
          <p className="text-sm text-gray-500 dark:text-gray-400 mb-3">{t('account.sessionsSubtitle', 'Devices where you are currently signed in.')}</p>
          <ul className="divide-y divide-gray-100 dark:divide-gray-800 rounded-md border border-gray-200 dark:border-gray-800">
            {sessions.map((s) => (
              <li key={s.sessionId} className="flex items-center gap-3 py-2 px-3">
                <div className="flex-1 min-w-0">
                  <div className="text-sm text-gray-900 dark:text-white truncate">
                    {s.ip || t('account.sessionsUnknownIp', 'Unknown location')}
                    {s.current && <span className="ms-2 text-xs text-green-700 dark:text-green-400">{t('account.sessionsThisDevice', 'This device')}</span>}
                  </div>
                  <div className="text-xs text-gray-500 dark:text-gray-400 truncate">
                    {t('account.sessionsLastSeen', { defaultValue: 'Last active {{date}}', date: new Date(s.lastSeenAt).toLocaleString() })}
                    {s.userAgent ? ` · ${s.userAgent}` : ''}
                  </div>
                </div>
                {!s.current && (
                  <button type="button" onClick={() => handleRevokeSession(s.sessionId)} disabled={sessionsBusy}
                    className="text-xs text-red-600 hover:text-red-700 dark:text-red-400 underline underline-offset-2 disabled:opacity-50 shrink-0">
                    {t('account.sessionsRevoke', 'Log out')}
                  </button>
                )}
              </li>
            ))}
          </ul>
        </div>
        )}

        {/* Your data — GDPR export (Art. 15/20) + delete (Art. 17). */}
        <div className="mt-6 border-t border-gray-200 dark:border-gray-800 pt-4">
          <h2 className="text-sm font-medium text-gray-900 dark:text-white mb-1">{t('account.dataTitle', 'Your data')}</h2>
          <p className="text-sm text-gray-500 dark:text-gray-400 mb-3">{t('account.dataSubtitle', 'Download a copy of your personal data, or delete your account.')}</p>

          {deleteQueued ? (
            <Alert variant="success">{t('account.deleteQueued', "Your account is scheduled for deletion. We've emailed you a cancellation link — use it any time in the next 30 days to keep your account.")}</Alert>
          ) : (
            <div className="space-y-2">
              <Button type="button" variant="secondary" className="w-full" loading={exporting} onClick={handleExport}>
                {t('account.exportData', 'Download my data')}
              </Button>

              {!showDelete ? (
                <Button type="button" variant="ghost" className="w-full text-red-600 hover:text-red-700 dark:text-red-400" onClick={() => { setShowDelete(true); setDeleteError(''); }}>
                  {t('account.deleteAccount', 'Delete my account')}
                </Button>
              ) : (
                <div className="rounded-md border border-red-200 dark:border-red-900/50 p-3 space-y-2">
                  <p className="text-sm text-gray-700 dark:text-gray-300">{t('account.deleteConfirmBody', 'This schedules your account for permanent deletion in 30 days or less. You will be signed out and emailed a cancellation link. Download your data first if you want a copy.')}</p>
                  {deleteError && <Alert variant="error">{deleteError}</Alert>}
                  <div className="flex gap-2">
                    <Button type="button" variant="secondary" className="flex-1" disabled={deleting} onClick={() => setShowDelete(false)}>{t('cancel', 'Cancel')}</Button>
                    <Button type="button" className="flex-1 bg-red-600 text-white hover:bg-red-700" loading={deleting} onClick={handleDelete}>{t('account.deleteConfirm', 'Delete my account')}</Button>
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
        </>
      )}
    </>
  );
}
