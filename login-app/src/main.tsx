import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './styles.css';
import App from './App';
import { loadBranding, BrandingContext } from './branding';

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
