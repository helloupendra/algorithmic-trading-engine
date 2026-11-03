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
  /** Dhan refused TOTP codes, and the desk is waiting to send one more at this time; null otherwise. */
  codesPausedUntilIst: string | null
  codesToday: number
  maxCodesPerDay: number
  /** Someone pressed Disconnect; the desk does not sign in until someone connects. */
  heldByDisconnect?: boolean
  /** Dhan refused the token before its 24 hours were up. */
  tokenRefused?: boolean
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

  if (status.heldByDisconnect) {
    return {
      tone: 'neutral',
      label: 'Held after Disconnect',
      detail: 'Someone pressed Disconnect, so the desk does not sign in by itself. Connect or Sign in now starts it again.',
    }
  }

  if (status.tokenRefused) {
    return {
      tone: 'neg',
      label: 'Dhan refused the token',
      detail: 'Dhan ended the token before its 24 hours were up, and nothing is signed in. Press Connect or Sign in now: by itself the desk waits for that token\'s due end, because Dhan may still count it as live.',
    }
  }

  if (status.stoppedForToday) {
    // A refused PIN, a day's limit of refused codes, a missing value or three
    // unreachable tries stop the day, and retrying a wrong PIN can lock the
    // account: the machine waits for a person.
    return {
      tone: 'neg',
      label: 'Stopped for today',
      detail: `${status.lastMessage ?? 'The last try failed.'} It tries again tomorrow; Sign in now tries at once.`,
    }
  }

  if (status.codesPausedUntilIst) {
    // Neither stopped nor working: on 3 Nov Dhan refused three codes, then took the
    // next one sent an hour later, so the desk keeps sending one at a time.
    return {
      tone: 'warn',
      label: 'Paused',
      detail: `Dhan refused ${status.codesToday} TOTP ${status.codesToday === 1 ? 'code' : 'codes'} today. The next goes at ${status.codesPausedUntilIst} IST, up to ${status.maxCodesPerDay} a day; Sign in now tries at once.`,
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
