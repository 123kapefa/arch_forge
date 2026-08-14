import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Прокси на сервис Map — фронт ходит на /api, CORS не нужен.
// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': 'http://localhost:5200',
    },
  },
})
