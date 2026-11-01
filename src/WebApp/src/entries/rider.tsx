import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import 'leaflet/dist/leaflet.css';
import './shared';
import RiderApp from '../apps/rider/RiderApp';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <RiderApp />
  </StrictMode>
);
