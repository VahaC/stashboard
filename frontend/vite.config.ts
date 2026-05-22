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
  build: { outDir: 'dist' },
})
