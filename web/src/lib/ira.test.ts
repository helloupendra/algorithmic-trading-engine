import { describe, expect, it } from 'vitest'
import {
  installHint,
  iraActivity,
  iraActivityText,
  iraAskUrl,
  iraGate,
  isIos,
  isStandalone,
  loginState,
  phoneLabelProblem,
  readInstallDismissed,
  readIraSpeak,
  readPhoneActions,
  readPhoneMade,
  readPhones,
  shortcutSteps,
  writeInstallDismissed,
  writeIraSpeak,
} from './ira'
import type { InstallEnv } from './ira'
import { returnTarget } from './modules'
import { IRA_SHELL_SWAPS, iraShell } from './iraShell'
import { THEME_COLOR } from './theme'

/** A storage that keeps what it is given, like localStorage. */
function memory(): Storage {
  const m = new Map<string, string>()
  return {
    get length() {
      return m.size
    },
    clear: () => m.clear(),
    getItem: (k) => m.get(k) ?? null,
    key: (i) => [...m.keys()][i] ?? null,
    removeItem: (k) => void m.delete(k),
    setItem: (k, v) => void m.set(k, String(v)),
  }
}

const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1'
const IPAD_AS_MAC = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Safari/605.1.15'
const ANDROID = 'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36'

describe('the /ira route gate', () => {
  it('waits for the session, sends a visitor to sign in, turns a trader away and opens for an admin', () => {
    expect(iraGate({ isLoading: true, isAuthenticated: false, isAdmin: false })).toBe('wait')
    expect(iraGate({ isLoading: false, isAuthenticated: false, isAdmin: false })).toBe('login')
    expect(iraGate({ isLoading: false, isAuthenticated: true, isAdmin: false })).toBe('forbidden')
    expect(iraGate({ isLoading: false, isAuthenticated: true, isAdmin: true })).toBe('open')
  })

  it('hands the sign-in page /ira, and the sign-in page comes back to it', () => {
    const state = loginState({ pathname: '/ira', search: '', hash: '' })
    expect(returnTarget(state)).toBe('/ira')
    expect(returnTarget(loginState({ pathname: '/ira', search: '?agent=ai-trader', hash: '#end' }))).toBe('/ira?agent=ai-trader#end')
    // RequireAuth's own state (a router Location) reads the same way.
    expect(returnTarget({ from: { pathname: '/markets/chart', search: '?symbol=NIFTY', hash: '', state: null, key: 'x' } })).toBe('/markets/chart?symbol=NIFTY')
  })

  it('goes home when nothing was remembered, and never off the site', () => {
    expect(returnTarget(null)).toBeNull()
    expect(returnTarget({})).toBeNull()
    expect(returnTarget({ from: { pathname: 42 } })).toBeNull()
    expect(returnTarget({ from: { pathname: '//evil.example/x' } })).toBeNull()
    expect(returnTarget({ from: { pathname: 'https://evil.example' } })).toBeNull()
  })
})

describe('the install hint', () => {
  const env = (over: Partial<InstallEnv>): InstallEnv => ({
    standalone: false,
    promptReady: false,
    userAgent: ANDROID,
    maxTouchPoints: 5,
    dismissed: false,
    ...over,
  })

  it("offers Android's install button once the browser hands over its prompt", () => {
    expect(installHint(env({ promptReady: true }))).toBe('prompt')
    expect(installHint(env({}))).toBe('none')
  })

  it('tells an iPhone or iPad how: Share, then Add to Home Screen', () => {
    expect(installHint(env({ userAgent: IPHONE }))).toBe('ios')
    expect(installHint(env({ userAgent: IPAD_AS_MAC, maxTouchPoints: 5 }))).toBe('ios')
    // A Mac is a Mac.
    expect(installHint(env({ userAgent: IPAD_AS_MAC, maxTouchPoints: 0 }))).toBe('none')
    expect(isIos(IPHONE, 0)).toBe(true)
  })

  it('says nothing once installed, or once the viewer closed it', () => {
    expect(installHint(env({ standalone: true, promptReady: true }))).toBe('none')
    expect(installHint(env({ standalone: true, userAgent: IPHONE }))).toBe('none')
    expect(installHint(env({ dismissed: true, promptReady: true }))).toBe('none')
  })

  it('knows the installed app by its display mode, or by iOS’s navigator.standalone', () => {
    const win = (standaloneMedia: boolean, iosStandalone?: boolean) =>
      ({
        matchMedia: (q: string) => ({ matches: standaloneMedia && q === '(display-mode: standalone)' }),
        navigator: { standalone: iosStandalone },
      }) as unknown as Pick<Window, 'matchMedia' | 'navigator'>
    expect(isStandalone(win(true))).toBe(true)
    expect(isStandalone(win(false, true))).toBe(true)
    expect(isStandalone(win(false))).toBe(false)
    expect(isStandalone(undefined)).toBe(false)
  })

  it('remembers a closed hint where storage allows', () => {
    const store = memory()
    expect(readInstallDismissed(store)).toBe(false)
    writeInstallDismissed(store)
    expect(readInstallDismissed(store)).toBe(true)
    expect(readInstallDismissed(null)).toBe(false)
  })
})

describe('spoken replies in the Ira app', () => {
  it('are on until the viewer turns them off', () => {
    const store = memory()
    expect(readIraSpeak(store)).toBe(true)
    writeIraSpeak(false, store)
    expect(readIraSpeak(store)).toBe(false)
    writeIraSpeak(true, store)
    expect(readIraSpeak(store)).toBe(true)
    expect(store.length).toBe(0)
    expect(readIraSpeak(null)).toBe(true)
  })
})

describe('what Ira is doing', () => {
  const idle = {
    online: true,
    recording: false,
    hearing: false,
    playback: 'idle' as const,
    streaming: false,
    answerStarted: false,
    handsFreeListening: false,
  }

  it('says the most pressing thing first', () => {
    expect(iraActivity(idle)).toBe('ready')
    expect(iraActivity({ ...idle, handsFreeListening: true })).toBe('listening')
    expect(iraActivity({ ...idle, streaming: true, handsFreeListening: true })).toBe('thinking')
    expect(iraActivity({ ...idle, streaming: true, answerStarted: true })).toBe('answering')
    expect(iraActivity({ ...idle, playback: 'playing', streaming: true })).toBe('speaking')
    expect(iraActivity({ ...idle, hearing: true, playback: 'playing' })).toBe('hearing')
    expect(iraActivity({ ...idle, recording: true, hearing: true })).toBe('recording')
    expect(iraActivity({ ...idle, online: false, recording: true })).toBe('offline')
    expect(iraActivity({ ...idle, playback: 'blocked' })).toBe('ready')
  })

  it('in words, with her name', () => {
    expect(iraActivityText('listening', 'Ira')).toBe('Listening for “Ira”')
    expect(iraActivityText('thinking', 'Ira')).toBe('Reading the desk…')
  })
})

describe('phone tokens', () => {
  it('reads the Phones box, never expecting the token itself', () => {
    const p = readPhones({
      enabled: true,
      maxTokens: 5,
      tokens: [{ id: 3, label: "Upendra's iPhone", createdUtc: '2026-10-12T05:00:00Z', lastUsedUtc: null }, { nope: true }],
    })
    expect(p).toEqual({ enabled: true, maxTokens: 5, tokens: [{ id: 3, label: "Upendra's iPhone", createdUtc: '2026-10-12T05:00:00Z', lastUsedUtc: null }] })
    expect(readPhones(null)).toEqual({ enabled: true, maxTokens: 5, tokens: [] })
  })

  it('reads a new token once, and refuses an answer without one', () => {
    const made = readPhoneMade({ id: 4, label: 'iPad', createdUtc: '2026-10-12T05:00:00Z', lastUsedUtc: null, token: 'ira_abc' })
    expect(made.token).toBe('ira_abc')
    expect(() => readPhoneMade({ id: 4, label: 'iPad' })).toThrow('did not return the token')
  })

  it('names a phone in 60 characters or fewer', () => {
    expect(phoneLabelProblem('  ')).toContain('Name the phone')
    expect(phoneLabelProblem('x'.repeat(61))).toContain('60 characters')
    expect(phoneLabelProblem("Upendra's iPhone")).toBeNull()
  })

  it('lists the Shortcut exactly: dictate, post with the token, read spoken, say it', () => {
    const url = iraAskUrl('https://www.openfno.com/')
    expect(url).toBe('https://www.openfno.com/api/Ira/ask')
    const steps = shortcutSteps(url)
    expect(steps.map((s) => s.action)).toEqual(['New Shortcut', 'Dictate Text', 'Get Contents of URL', 'Get Dictionary Value', 'Speak Text'])
    expect(steps[0].detail).toContain('"Ira"')
    expect(steps[1].detail).toContain('English (India)')
    expect(steps[2].detail).toContain(url)
    expect(steps[2].detail).toContain('Method POST')
    expect(steps[2].detail).toContain('Authorization = Bearer <your token>')
    expect(steps[2].detail).toContain('text = Dictated Text')
    expect(steps[3].detail).toContain('spoken')
  })

  it('reads the actions a phone question proposed in the confirm card’s shape', () => {
    expect(
      readPhoneActions([
        { id: 12, kind: 'stop_run', summary: 'Stop run 512.', createdUtc: 'x', expiresUtc: '2026-10-12T05:02:00Z', callId: 9 },
        { id: 'no' },
      ]),
    ).toEqual([{ id: 12, kind: 'stop_run', summary: 'Stop run 512.', expiresUtc: '2026-10-12T05:02:00Z', items: null }])
    expect(readPhoneActions({})).toEqual([])
  })
})

describe('the installable app', () => {
  const read = (glob: Record<string, unknown>) => Object.values(glob)[0] as string
  const manifestText = read(import.meta.glob('../../public/ira.webmanifest', { query: '?raw', import: 'default', eager: true }))
  const indexHtml = read(import.meta.glob('../../index.html', { query: '?raw', import: 'default', eager: true }))
  const brand = Object.keys(import.meta.glob('../../public/brand/ira-*', { query: '?url', import: 'default' })).map((k) => k.replace('../../public', ''))

  it('has a manifest named Ira, scoped to /ira, standalone, in the console’s colours', () => {
    const m = JSON.parse(manifestText) as Record<string, unknown> & { icons: { src: string; sizes: string; purpose: string }[] }
    expect([m.name, m.short_name, m.start_url, m.scope, m.display]).toEqual(['Ira', 'Ira', '/ira', '/ira', 'standalone'])
    expect(m.theme_color).toBe(THEME_COLOR.dark)
    expect(m.background_color).toBe(THEME_COLOR.dark)
    expect(m.icons.map((i) => `${i.sizes} ${i.purpose}`)).toEqual(['192x192 any', '512x512 any', '512x512 maskable'])
    // Every icon it names is in the repo, with the iPhone's and the sources.
    for (const icon of m.icons) expect(brand).toContain(icon.src)
    expect(brand).toEqual(expect.arrayContaining(['/brand/ira-apple-touch-icon.png', '/brand/ira-mark.svg', '/brand/ira-maskable.svg']))
  })

  it('gives /ira a shell whose head installs Ira, not the console', () => {
    const shell = iraShell(indexHtml)
    expect(shell).toContain('<link rel="manifest" href="/ira.webmanifest" />')
    expect(shell).toContain('<link rel="apple-touch-icon" href="/brand/ira-apple-touch-icon.png" />')
    expect(shell).toContain('<meta name="apple-mobile-web-app-title" content="Ira" />')
    expect(shell).toContain('<meta name="apple-mobile-web-app-capable" content="yes" />')
    expect(shell).toContain('<meta name="mobile-web-app-capable" content="yes" />')
    expect(shell).toContain('<meta name="apple-mobile-web-app-status-bar-style" content="default" />')
    expect(shell).toContain('<title>Ira · OpenFNO</title>')
    expect(shell).toContain('<meta name="robots" content="noindex,nofollow" />')
    expect(shell).not.toContain('manifest.webmanifest')
    expect(shell).not.toContain('Loading the console')
    // The same app: the bundle's script is untouched.
    expect(shell).toContain('<script type="module" src="/src/main.tsx"></script>')
  })

  it('fails the build rather than ship a shell that would install the console', () => {
    expect(() => iraShell('<html><head><title>x</title></head></html>')).toThrow('ira.html: index.html has no the manifest')
    expect(IRA_SHELL_SWAPS.length).toBeGreaterThan(5)
  })
})
