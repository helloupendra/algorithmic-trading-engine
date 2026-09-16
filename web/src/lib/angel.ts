/**
 * What the Angel One connector's status means, in one place.
 *
 * Angel is the only connector whose credentials the console cannot hold yet:
 * a SmartAPI session needs an API key, a client code, a trading PIN and a TOTP
 * secret, and the two encrypted fields every connector shares have room for
 * two of them. Until that changes the values come from .env, and the page has
 * to say so instead of offering a form that would not work.
 */

export interface AngelStatus {
  provider: string
  displayName: string
  rootUrl: string
  clientCode: string
  staticIp: string
  configured: boolean
  missing: string[]
  hasSession: boolean
  sessionStartedUtc: string | null
  note: string
}

export interface AngelTestResult {
  ok: boolean
  step: string
  message: string
  elapsedMs?: number
}

export type AngelReadinessTone = 'pos' | 'warn' | 'neg'

export interface AngelReadiness {
  tone: AngelReadinessTone
  label: string
  detail: string
}

/** The one line the panel leads with, and its colour. */
export function angelReadiness(status: AngelStatus | undefined): AngelReadiness {
  if (!status) return { tone: 'warn', label: 'Checking…', detail: '' }
  if (!status.configured) {
    return {
      tone: 'neg',
      label: `Not configured — ${status.missing.length} value(s) missing`,
      detail: `Add ${status.missing.join(', ')} to .env and restart the API.`,
    }
  }
  if (status.hasSession) {
    return { tone: 'pos', label: 'Signed in', detail: 'A SmartAPI session is held; calls will not sign in again.' }
  }
  return {
    tone: 'warn',
    label: 'Configured, not signed in',
    detail: 'Credentials are set. Test connection signs in and prices one instrument.',
  }
}

/** Refusals worth explaining before the operator goes looking. */
export function angelFailureHint(message: string): string | null {
  const text = (message || '').toLowerCase()
  if (text.includes('static ip'))
    return 'The SmartAPI app answers only from the IP it was registered with — try it on the server.'
  if (text.includes('totp') || text.includes('clock'))
    return 'Check ANGEL_TOTP_SECRET, and that this machine’s clock is right.'
  if (text.includes('not configured'))
    return 'Fill the missing values in .env, then restart the API.'
  return null
}
