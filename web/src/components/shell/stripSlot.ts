import { useLocation, useOutletContext } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import { accessFor, nameRoute, navFor } from '../../lib/modules'

/** What the shell hands the page under it. */
export interface ShellOutlet {
  /** The strip under the top bar, where a page may put controls of its own; null until it mounts. */
  stripSlot: HTMLElement | null
}

/**
 * Where a page renders its own controls into the shell's strip (with a
 * portal), so the page's switches sit on the one bar under the top bar
 * instead of adding a second. Null outside the shell and on the first render.
 */
export function useStripSlot(): HTMLElement | null {
  return useOutletContext<ShellOutlet | undefined>()?.stripSlot ?? null
}

/** Whether the page is drawn inside the console shell, whose strip then names it. */
export function useInShell(): boolean {
  return useOutletContext<ShellOutlet | undefined>() != null
}

/**
 * The page's name as the registry has it (lib/modules.ts): the word on its
 * tab, in the browser's title and in the strip. A page that needs the word
 * in its own heading takes it from here rather than spelling a second one.
 */
export function usePageTitle(): string {
  const { user } = useAuth()
  const { pathname } = useLocation()
  return nameRoute(pathname, navFor(accessFor(user))).page
}
