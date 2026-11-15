import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Прокси на сервис Map — фронт ходит на /api, CORS не нужен.
// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  build: {
    // Единственный чанк >500 кБ — elkjs (~1.4 МБ, неделимая библиотека), он грузится лениво при первой раскладке.
    // Порог поднят осознанно: основной чанк ~140 кБ, всё остальное меньше.
    chunkSizeWarningLimit: 1500,
    rolldownOptions: {
      output: {
        // Вендоры — отдельными чанками (#57): кэшируются независимо от кода приложения.
        codeSplitting: {
          groups: [
            { name: 'vendor-react-flow', test: /node_modules[\\/](@xyflow|d3-[^\\/]+|classcat|zustand)[\\/]/, priority: 20 },
            { name: 'vendor-react', test: /node_modules[\\/](react|react-dom|scheduler)[\\/]/, priority: 10 },
          ],
        },
      },
    },
  },
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
