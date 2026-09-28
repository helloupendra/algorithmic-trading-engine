/**
 * The console's theme: dark (the default, the product's identity) or light,
 * or "system" to follow the device. The choice lives in localStorage under
 * `openfno.theme` and lands on <html data-theme="…">, which styles.css reads
 * to swap the token block. index.html carries a copy of `resolveTheme` in an
 * inline script so the first paint is already the right colour; this module
 * is the same rule for everything after that.
 *
 * Public pages (the homepage, sign-in, invites) keep their own black identity
 * whatever the preference: `useForcedTheme('dark')` holds them there while
 * they are mounted and hands the preference back on the way out.
 *
 * Charts drawn by JavaScript read their colours from the tokens once when
 * they are created, so they subscribe to `useTheme().theme` and rebuild when
 * it changes; `THEME_EVENT` is the same signal for code outside React.
 */

import { useCallback, useEffect, useSyncExternalStore } from 'react'

export type Theme = 'dark' | 'light'
export type ThemePref = Theme | 'system'

export const THEME_KEY = 'openfno.theme'
export const THEME_EVENT = 'openfno:theme'
export const THEME_PREFS: readonly ThemePref[] = ['dark', 'light', 'system']

/** The colour the browser paints around the page (the iOS status bar, Android's task switcher). */
export const THEME_COLOR: Record<Theme, string> = { dark: '#030406', light: '#f4f6f9' }

/** A stored value read back: anything but the three words is the default. */
export function parseThemePref(raw: string | null | undefined): ThemePref {
  return raw === 'light' || raw === 'system' ? raw : 'dark'
}

/** Which of the two token blocks a preference means, given what the device asks for. */
export function resolveTheme(pref: ThemePref, systemDark: boolean): Theme {
  if (pref === 'system') return systemDark ? 'dark' : 'light'
  return pref
}

/** The storage, or null where touching it throws (private windows, blocked site data). */
function storage(): Storage | null {
  try {
    return window.localStorage
  } catch {
    return null
  }
}

export function readThemePref(store: Pick<Storage, 'getItem'> | null = storage()): ThemePref {
  try {
    return parseThemePref(store?.getItem(THEME_KEY))
  } catch {
    return 'dark'
  }
}

export function writeThemePref(pref: ThemePref, store: Pick<Storage, 'setItem' | 'removeItem'> | null = storage()): void {
  try {
    // The default is not worth a key: a fresh browser and a reset one read the same.
    if (pref === 'dark') store?.removeItem(THEME_KEY)
    else store?.setItem(THEME_KEY, pref)
  } catch {
    // Nothing to do: the theme still applies for this page's lifetime.
  }
}

function systemDark(): boolean {
  return typeof window === 'undefined' || typeof window.matchMedia !== 'function' ? true : !window.matchMedia('(prefers-color-scheme: light)').matches
}

/** The theme currently on <html>; dark until something has been applied. */
export function currentTheme(doc: Pick<Document, 'documentElement'> = document): Theme {
  return doc.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark'
}

/**
 * Put a theme on the document: the data attribute the stylesheet keys on and
 * the browser's own chrome colour. Fires THEME_EVENT only on a real change,
 * so listeners can rebuild charts without debouncing.
 */
export function applyTheme(theme: Theme, doc: Document = document): void {
  const root = doc.documentElement
  const before = currentTheme(doc)
  root.setAttribute('data-theme', theme)
  root.style.colorScheme = theme
  doc.querySelector('meta[name="theme-color"]')?.setAttribute('content', THEME_COLOR[theme])
  if (before !== theme) doc.dispatchEvent(new CustomEvent(THEME_EVENT, { detail: theme }))
}

// ------------------------------------------------------------- the store

type Listener = () => void
const listeners = new Set<Listener>()
let prefCache: ThemePref | null = null
/** Set while a public page holds the document dark; the preference is untouched. */
let forced: Theme | null = null

function pref(): ThemePref {
  if (prefCache == null) prefCache = readThemePref()
  return prefCache
}

function wanted(): Theme {
  return forced ?? resolveTheme(pref(), systemDark())
}

function notify() {
  applyTheme(wanted())
  for (const l of listeners) l()
}

function subscribe(listener: Listener): () => void {
  listeners.add(listener)
  let media: MediaQueryList | null = null
  if (listeners.size === 1 && typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
    media = window.matchMedia('(prefers-color-scheme: light)')
    media.addEventListener('change', notify)
  }
  const onStorage = (e: StorageEvent) => {
    // Another tab changed it: follow, so two consoles side by side agree.
    if (e.key === THEME_KEY) {
      prefCache = parseThemePref(e.newValue)
      notify()
    }
  }
  window.addEventListener('storage', onStorage)
  return () => {
    listeners.delete(listener)
    media?.removeEventListener('change', notify)
    window.removeEventListener('storage', onStorage)
  }
}

/** Change the preference: stored, applied, and every subscriber told. */
export function setThemePref(next: ThemePref): void {
  prefCache = next
  writeThemePref(next)
  notify()
}

/** The preference and the theme it currently means, live. */
export function useTheme(): { pref: ThemePref; theme: Theme; setPref: (next: ThemePref) => void } {
  const current = useSyncExternalStore(subscribe, pref, () => 'dark' as ThemePref)
  const theme = useSyncExternalStore(subscribe, wanted, () => 'dark' as Theme)
  // The first subscriber applies the stored preference: index.html painted it
  // already, and this is what keeps it applied after client-side navigation.
  useEffect(() => applyTheme(wanted()), [])
  return { pref: current, theme, setPref: useCallback((next: ThemePref) => setThemePref(next), []) }
}

/** Hold the document on one theme while the caller is mounted (public pages stay dark). */
export function useForcedTheme(theme: Theme): void {
  useEffect(() => {
    forced = theme
    notify()
    return () => {
      forced = null
      notify()
    }
  }, [theme])
}

/** The label a control shows for each preference. */
export const THEME_LABELS: Record<ThemePref, string> = { dark: 'Dark', light: 'Light', system: 'System' }
