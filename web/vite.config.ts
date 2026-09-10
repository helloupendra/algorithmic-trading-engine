import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import type { Plugin } from 'vite'

/**
 * The documentation site is generated into dist/docs after the console
 * bundle, so the one `vite build` the desk runs publishes both, plus the
 * site's robots.txt and sitemap.xml. See docs-site/build.mjs.
 */
function docsSite(): Plugin {
  return {
    name: 'openfno-docs-site',
    apply: 'build',
    async closeBundle() {
      const { buildDocs } = await import('./docs-site/build.mjs')
      const pages = await buildDocs({ outDir: new URL('./dist', import.meta.url).pathname })
      console.log(`\n  docs-site: ${pages} pages → dist/docs\n`)
    },
  }
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), docsSite()],
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5025',
        changeOrigin: true,
        secure: false,
      }
    }
  }
})
