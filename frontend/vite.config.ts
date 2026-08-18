import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Прокси на сервис Map — фронт ходит на /api, CORS не нужен.
// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5200',
        // vite/http-proxy НЕ рвёт upstream при client-abort → вытесненный тяжёлый анализ досчитывался бы на
        // бэкенде впустую (в проде фронт ходит в Map напрямую и abort проходит). Рвём upstream вручную, чтобы
        // dev совпал с продом: клиент отвалился до конца ответа → destroy → Map/Analysis отменяют Roslyn (P1.5-H).
        configure: (proxy) => {
          proxy.on('proxyReq', (proxyReq, _req, res) => {
            res.once('close', () => { if (!res.writableEnded) proxyReq.destroy(); });
          });
        },
      },
    },
  },
})
