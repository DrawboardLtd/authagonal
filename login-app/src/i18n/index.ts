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

i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources: {
      en: { translation: en },
      'zh-Hans': { translation: zhHans },
      de: { translation: de },
      fr: { translation: fr },
      es: { translation: es },
      vi: { translation: vi },
      pt: { translation: pt },
      ar: { translation: ar },
      tlh: { translation: tlh },
    },
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
