import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  base: './',
  server: {
    proxy: {
      '/ws': {
        target: 'http://localhost:12346',
        ws: true,
        changeOrigin: true
      }
    }
  }
})
