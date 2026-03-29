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

export default i18n;
