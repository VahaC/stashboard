import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'node:path'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5254', changeOrigin: true, secure: false },
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
