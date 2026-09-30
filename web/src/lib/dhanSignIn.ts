/**
 * What Dhan's automatic PIN + TOTP sign-in is doing, in one place.
 *
 * Like Angel, the values it needs (the PIN and the TOTP secret) live in the
 * server's .env, not in the console, so the panel has to say which are missing
 * rather than offer a form. What it adds over the Session panel is the
 * difference between "a person pressed Connect" and "the desk signed itself
 * in", and when the desk will try next.
 */

export interface DhanAutoSignInStatus {
  configured: boolean
  enabled: boolean
  missing: string[]
  morningFromIst: string
  morningUntilIst: string
  stoppedForToday: boolean
  lastAttemptUtc: string | null
  lastOk: boolean | null
  lastTrigger: string | null
  lastMessage: string | null
  lastExpiresUtc: string | null
}

export interface DhanSignInNowResult {
  ok: boolean
  message: string
  expiresUtc?: string | null
}

export type DhanAutoSignInTone = 'pos' | 'warn' | 'neg' | 'neutral'

export interface DhanAutoSignInView {
  tone: DhanAutoSignInTone
  label: string
  detail: string
}

/** The one line the panel leads with, and its colour. */
export function dhanAutoSignInView(status: DhanAutoSignInStatus | undefined): DhanAutoSignInView {
  if (!status) return { tone: 'neutral', label: 'Checking…', detail: '' }

  if (!status.configured) {
    return {
      tone: 'neutral',
      label: 'Not set up',
      detail: `Add ${status.missing.join(' and ')} to the server's .env to let the desk sign in by itself. Until then, press Connect each morning.`,
    }
  }

  if (!status.enabled) {
    return {
      tone: 'neutral',
      label: 'Switched off',
      detail: 'The PIN and TOTP secret are set, but Dhan:AutoSignIn:Enabled is false.',
    }
  }

  if (status.stoppedForToday) {
    // A refused PIN, three refused codes, a missing value or three unreachable
    // tries stop the day, and retrying a wrong PIN can lock the account: the
    // machine waits for a person.
    return {
      tone: 'neg',
      label: 'Stopped for today',
      detail: `${status.lastMessage ?? 'The last try failed.'} It tries again tomorrow; Sign in now tries at once.`,
    }
  }

  // Nothing signs in before the window opens, nor while the last token is live:
  // on 30 Sep Dhan refused a sign-in nine seconds after the last token ended.
  const window = `on weekdays from ${status.morningFromIst} IST, two minutes after the last token has run out (Dhan has refused a new one sooner)`
  if (status.lastOk === false) {
    return { tone: 'warn', label: 'Last try failed', detail: `${status.lastMessage ?? ''} It will try again by itself.`.trim() }
  }

  return {
    tone: 'pos',
    label: 'On',
    detail: status.lastOk ? `${status.lastMessage} Next: ${window}.` : `Signs in ${window}.`,
  }
}
