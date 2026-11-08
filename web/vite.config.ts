import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { resolve } from 'node:path'
import type { Plugin } from 'vite'
import { iraShell } from './src/lib/iraShell.ts'

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
 * The Ira phone app's shell, dist/ira.html: the built index.html with Ira's
 * manifest, icons and name in its head (src/lib/iraShell.ts), which the API
 * serves for /ira so a phone installs "Ira" before any script runs.
 */
function iraShellPage(): Plugin {
  let outDir = here('./dist')
  return {
    name: 'openfno-ira-shell',
    apply: 'build',
    configResolved(config) {
      // --outDir (a screenshot build, a deploy's staging folder) wins over the default dist.
      outDir = resolve(config.root, config.build.outDir)
    },
    async closeBundle() {
      const { readFileSync, writeFileSync } = await import('node:fs')
      const { join } = await import('node:path')
      writeFileSync(join(outDir, 'ira.html'), iraShell(readFileSync(join(outDir, 'index.html'), 'utf8')))
      console.log(`\n  ira-shell: ${join(outDir, 'ira.html')}\n`)
    },
  }
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), iraShellPage(), docsSite()],
  build: {
    rollupOptions: {
      // Two documents: the console (index.html, the React app) and the public
      // homepage (home.html, plain HTML the API serves at "/"; see ConsoleSite).
      input: { main: here('./index.html'), home: here('./home.html') },
    },
  },
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
