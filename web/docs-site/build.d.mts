/**
 * Type surface of build.mjs for vite.config.ts, which imports it under
 * tsconfig.node.json (no allowJs). Keep in step with the exports there.
 */
export declare const SITE: {
  url: string
  name: string
  tagline: string
  github: string
  description: string
}
export declare function buildDocs(options: { outDir: string }): Promise<number>
