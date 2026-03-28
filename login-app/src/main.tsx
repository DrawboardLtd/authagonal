import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './styles.css';
import App from './App';
import { loadBranding, BrandingContext } from './branding';
import './i18n';
import i18n from './i18n';

loadBranding().then((config) => {
  document.title = i18n.t('signInTitle', { appName: config.appName });

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <BrandingContext.Provider value={config}>
        <App />
      </BrandingContext.Provider>
    </StrictMode>,
  );
});
