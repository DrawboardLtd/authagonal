import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { loadBranding, BrandingContext } from '@drawboard/authagonal-login';
import '@drawboard/authagonal-login/styles.css';
import App from './App';

loadBranding().then((config) => {
  document.title = `Sign In — ${config.appName}`;

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <BrandingContext.Provider value={config}>
        <App />
      </BrandingContext.Provider>
    </StrictMode>,
  );
});
