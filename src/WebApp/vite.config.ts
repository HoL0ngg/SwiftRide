import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '');
  const gateway = {
    target: env.GATEWAY_URL || 'http://localhost:5080',
    changeOrigin: true,
    timeout: 15_000,
    proxyTimeout: 15_000,
  };
  return {
    plugins: [react(), tailwindcss()],
    // Multi-page app: `/` (role chooser), `/rider/` and `/driver/` are separate entries.
    appType: 'mpa' as const,
    build: {
      rollupOptions: {
        input: {
          landing: 'index.html',
          rider: 'rider/index.html',
          driver: 'driver/index.html',
        },
      },
    },
    server: {
      proxy: {
        '/locations': gateway,
        '/dev/token': gateway,
        '/trips': gateway,
        '/drivers': gateway,
      },
    },
  };
});
