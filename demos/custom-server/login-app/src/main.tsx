import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { loadBranding, BrandingContext, type BrandingConfig } from '@authagonal/login';
import '@authagonal/login/styles.css';
import App from './App';

loadBranding().then((config: BrandingConfig) => {
  document.title = `Sign In — ${config.appName}`;

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <BrandingContext.Provider value={config}>
        <App />
      </BrandingContext.Provider>
    </StrictMode>,
  );
});
