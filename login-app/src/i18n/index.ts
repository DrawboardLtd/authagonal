import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import LanguageDetector from 'i18next-browser-languagedetector';

import en from './en.json';
import zhHans from './zh-Hans.json';
import de from './de.json';
import fr from './fr.json';
import es from './es.json';
import vi from './vi.json';
import pt from './pt.json';
import ar from './ar.json';
import tlh from './tlh.json';
import af from './af.json';
import hi from './hi.json';

/// The single source of truth for shipped UI languages: this list drives BOTH i18next resource
/// registration and every language picker (AuthLayout switcher, AccountPage select). Adding a
/// locale here is the whole job — a picker can no longer drift from the registered locales
/// (which is how hi/af/ar went missing from dropdowns while the tlh easter egg survived).
/// Labels are each language's native name, no flags.
export const LANGUAGES: { code: string; label: string; resource: object }[] = [
  { code: 'en', label: 'English', resource: en },
  { code: 'zh-Hans', label: '中文', resource: zhHans },
  { code: 'de', label: 'Deutsch', resource: de },
  { code: 'fr', label: 'Français', resource: fr },
  { code: 'es', label: 'Español', resource: es },
  { code: 'vi', label: 'Tiếng Việt', resource: vi },
  { code: 'pt', label: 'Português', resource: pt },
  { code: 'ar', label: 'العربية', resource: ar },
  { code: 'af', label: 'Afrikaans', resource: af },
  { code: 'hi', label: 'हिन्दी', resource: hi },
  { code: 'tlh', label: 'tlhIngan', resource: tlh },
];

i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources: Object.fromEntries(LANGUAGES.map((l) => [l.code, { translation: l.resource }])),
    fallbackLng: 'en',
    interpolation: {
      escapeValue: false,
    },
    detection: {
      order: ['localStorage', 'querystring', 'navigator'],
      lookupQuerystring: 'lng',
      caches: ['localStorage'],
    },
  });

// Mirror the active language onto <html lang>/<html dir> so RTL languages (ar, …) flip the auth card.
// The language picker switches language in place (no reload), so the languageChanged listener is what
// keeps direction in sync on every switch.
const applyDocumentDir = (lng: string) => {
  document.documentElement.lang = lng;
  document.documentElement.dir = i18n.dir(lng);
};
applyDocumentDir(i18n.language);
i18n.on('languageChanged', applyDocumentDir);

export default i18n;
