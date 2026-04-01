import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
      '/saml': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
      '/oidc': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
      '/connect': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
})
