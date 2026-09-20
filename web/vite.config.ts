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

/**
 * The fallback stills under src/shots/stills are rendered from the live scene
 * by private/homepage-v3/stills.mjs. A still older than the scene it shows is
 * a lie on the reduced-motion and phone pages, so the build says so loudly.
 * It warns rather than fails: the server that deploys this has no Chrome to
 * re-render them, and the stills themselves need a build to render from. The
 * rule is that whoever changes the scene re-renders the stills before pushing.
 */
function freshStills(): Plugin {
  return {
    name: 'openfno-fresh-stills',
    apply: 'build',
    async buildStart() {
      const { readdirSync, statSync, existsSync } = await import('node:fs')
      const { join } = await import('node:path')
      const root = new URL('./src', import.meta.url).pathname
      const stillsDir = join(root, 'shots', 'stills')
      if (!existsSync(stillsDir)) {
        this.warn('no fallback stills under src/shots/stills — run private/homepage-v3/stills.mjs')
        return
      }
      const stills = readdirSync(stillsDir).filter((f) => f.endsWith('.webp'))
      if (!stills.length) {
        this.warn('no fallback stills under src/shots/stills — run private/homepage-v3/stills.mjs')
        return
      }
      const oldest = Math.min(...stills.map((f) => statSync(join(stillsDir, f)).mtimeMs))
      const sources = [
        ...readdirSync(join(root, 'scene')).filter((f) => f.endsWith('.ts') && !f.endsWith('.test.ts')).map((f) => join(root, 'scene', f)),
        ...readdirSync(join(root, 'shots')).filter((f) => f.endsWith('.webp')).map((f) => join(root, 'shots', f)),
      ]
      const newest = sources.reduce((m, f) => Math.max(m, statSync(f).mtimeMs), 0)
      if (newest > oldest) {
        this.warn('the fallback stills under src/shots/stills are older than the scene — re-render them with private/homepage-v3/stills.mjs before pushing')
      }
    },
  }
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), docsSite(), freshStills()],
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
