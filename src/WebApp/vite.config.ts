import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '');
  const gateway = { target: env.GATEWAY_URL || 'http://localhost:5080', changeOrigin: true, timeout: 15_000, proxyTimeout: 15_000 };
  return { plugins: [react(), tailwindcss()], server: { proxy: { '/locations': gateway, '/dev/token': gateway } } };
});
