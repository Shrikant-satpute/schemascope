import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// The built app is dropped straight into the API's wwwroot so the packaged
// desktop exe serves everything from one origin.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    outDir: '../src/SchemaScope.Api/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 2000,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:5199',
        // changeOrigin rewrites Host to the target, which the API requires.
        changeOrigin: true,
        // The packaged app gets its token from the launch URL. In development
        // the proxy supplies the fixed one that `--server` mode expects.
        headers: { 'X-SchemaScope-Token': 'schemascope-dev' },
      },
    },
  },
})
