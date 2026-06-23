import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { getProfile, updateProfile, ApiRequestError } from '../api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle } from '@/components/ui/card';

// The UI languages we ship — mirrors AuthLayout's switcher. The chosen value is the user's
// preferred UI/communication language; emails localise to it (falling back to English for any
// language we don't template, e.g. the tlh easter egg).
const LANGUAGES: { code: string; label: string }[] = [
  { code: 'en', label: 'English' },
  { code: 'zh-Hans', label: '中文' },
  { code: 'de', label: 'Deutsch' },
  { code: 'fr', label: 'Français' },
  { code: 'es', label: 'Español' },
  { code: 'vi', label: 'Tiếng Việt' },
  { code: 'pt', label: 'Português' },
  { code: 'tlh', label: 'tlhIngan' },
];

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
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [unauthenticated, setUnauthenticated] = useState(false);
  const [saved, setSaved] = useState(false);
  const [email, setEmail] = useState('');
  const [form, setForm] = useState({ firstName: '', lastName: '', companyName: '', phone: '', locale: '' });

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

  return (
    <>
      <CardTitle>{t('account.title')}</CardTitle>
      <p className="text-sm text-gray-500 dark:text-gray-400 mb-4">{t('account.subtitle')}</p>

      {error && <Alert variant="error">{error}</Alert>}
      {saved && <Alert variant="success">{t('account.saved')}</Alert>}

      {loading ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">{t('account.loading')}</p>
      ) : (
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
              {LANGUAGES.map((l) => <option key={l.code} value={l.code}>{l.label}</option>)}
            </select>
          </div>
          <Button type="submit" className="w-full" loading={saving}>
            {saving ? t('account.saving') : t('account.save')}
          </Button>
        </form>
      )}
    </>
  );
}
