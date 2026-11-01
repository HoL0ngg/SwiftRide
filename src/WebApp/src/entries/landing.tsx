import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './shared';
import LandingPage, { legacyRoleRedirect } from '../apps/landing/LandingPage';

// Old links used `/?role=driver`; send them to the dedicated app.
const redirect = legacyRoleRedirect(window.location.search);
if (redirect) {
  window.location.replace(redirect);
} else {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <LandingPage />
    </StrictMode>
  );
}
