import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import path from 'node:path'

// V6.8.1 — first frontend test harness (added alongside the node-alerting UI).
// jsdom + Testing Library; CSS is ignored so component imports of `.css` are
// no-ops under test.
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  // Provided by vite.config.ts in app builds; stub it for tests.
  define: { __APP_VERSION__: JSON.stringify('9.9.9') },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
  },
})
