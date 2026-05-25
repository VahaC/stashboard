import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'node:path'
import { createRequire } from 'node:module'

const _require = createRequire(import.meta.url)
const { version: APP_VERSION } = _require('./package.json') as { version: string }

export default defineConfig({
  plugins: [react(), tailwindcss()],
  define: {
    __APP_VERSION__: JSON.stringify(APP_VERSION),
  },
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },

  server: {
    port: 5173,
    proxy: {
      // `ws: true` is required for the V5.3 host terminal — its WebSocket
      // (`/api/docker/connections/{id}/host-shell/ws`) won't upgrade through the
      // dev proxy without it, leaving the terminal stuck on "connecting…".
      '/api': { target: 'http://localhost:5254', changeOrigin: true, secure: false, ws: true },
      '/uploads': { target: 'http://localhost:5254', changeOrigin: true, secure: false },
    },
  },
  build: {
    outDir: 'dist',
    // Single self-consistent bundle: no separately-hashed lazy chunks that can
    // 404 or go stale after a release. One index-<hash>.js + one index-<hash>.css.
    cssCodeSplit: false,
    rollupOptions: {
      output: { inlineDynamicImports: true },
    },
  },
})
