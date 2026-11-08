/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react';
import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), 'VITE_');
  const target = env.VITE_API_PROXY_TARGET || 'http://127.0.0.1:53375';
  return {
    plugins: [react()],
    server: {
      port: 5173,
      // The browser sees one origin in development, so the SameSite=Strict refresh cookie works without CORS.
      proxy: { '/api': { target, changeOrigin: false }, '/health': { target, changeOrigin: false } }
    },
    test: { environment: 'jsdom', globals: true, setupFiles: './src/test-setup.ts', include: ['src/**/*.test.{ts,tsx}'] }
  };
});
