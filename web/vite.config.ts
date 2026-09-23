import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import type { Plugin } from 'vite'

/**
 * Not `new URL(...).pathname`. On Windows that returns "/C:/Users/..." with the
 * leading slash still attached, which node then resolves against the drive and
 * tries to mkdir "C:\C:\Users\...". The docs build died there and took the
 * whole `npm run build` exit code with it, which is what scripts/deploy.ps1
 * checks before it copies anything into wwwroot - so a Windows deploy stopped
 * before it published. fileURLToPath is the conversion that knows about
 * drive letters, and it percent-decodes a path with spaces in it as a bonus.
 */
const here = (rel: string) => fileURLToPath(new URL(rel, import.meta.url))

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
      const pages = await buildDocs({ outDir: here('./dist') })
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
      const root = here('./src')
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
