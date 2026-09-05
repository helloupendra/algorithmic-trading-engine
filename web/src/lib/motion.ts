/** Shared motion preference check — lives outside the scene module so that file
 *  only exports components (fast refresh) and the landing page can reuse it. */
export function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined' && !!window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches
}
