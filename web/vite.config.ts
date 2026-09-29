/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build output is embedded into RepoManager.App (wwwroot/**) and served by the daemon on 127.0.0.1:4000.
export default defineConfig({
  base: '/',
  plugins: [react()],
  build: {
    outDir: '../src/RepoManager.App/wwwroot',
    emptyOutDir: true,
    target: 'es2022',
    sourcemap: false,
  },
  server: {
    port: 5199,
    strictPort: false,
    proxy: {
      // Dev: open http://localhost:5199/#token=<token>; /api is forwarded to the running daemon.
      '/api': { target: 'http://127.0.0.1:4000', changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    // The first render of a file can take several seconds on a cold CI runner; 5 s (the default) was too tight.
    testTimeout: 20_000,
  },
});
