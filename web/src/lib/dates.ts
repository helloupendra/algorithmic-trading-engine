/**
 * Day-month-year text for the console's date fields.
 *
 * The API and every piece of state speak ISO (yyyy-MM-dd). What the user sees
 * and types is dd/mm/yyyy — the Indian order — regardless of the browser's
 * locale, which is what a native <input type="date"> would impose (a phone or
 * a Chrome set to en-US shows mm/dd/yyyy and there is no attribute that
 * overrides it). These helpers are the whole conversion; DateField.tsx is the
 * control built on them.
 */

const ISO = /^(\d{4})-(\d{2})-(\d{2})$/

/** yyyy-MM-dd → dd/mm/yyyy; anything that is not an ISO date → ''. */
export function isoToDmy(iso: string | null | undefined): string {
  if (!iso) return ''
  const m = ISO.exec(iso)
  if (!m) return ''
  return `${m[3]}/${m[2]}/${m[1]}`
}

/**
 * Typed text → yyyy-MM-dd, or null when it is not a complete, real calendar
 * date. Accepts d/m/yyyy with '/', '-' or '.' between the parts, eight bare
 * digits (ddmmyyyy), and a pasted ISO date.
 */
export function dmyToIso(text: string): string | null {
  const t = text.trim()
  if (ISO.test(t)) return isRealDate(t) ? t : null
  let d: string, m: string, y: string
  const sep = /^(\d{1,2})[/.-](\d{1,2})[/.-](\d{4})$/.exec(t)
  if (sep) [, d, m, y] = sep
  else if (/^\d{8}$/.test(t)) [d, m, y] = [t.slice(0, 2), t.slice(2, 4), t.slice(4)]
  else return null
  const iso = `${y}-${m.padStart(2, '0')}-${d.padStart(2, '0')}`
  return isRealDate(iso) ? iso : null
}

/**
 * What the field shows while the user types: digits only, a '/' placed after
 * the day and after the month, never longer than dd/mm/yyyy. Typing '7', '/'
 * and '9' therefore reads 07/09 without the user having to pad anything.
 */
export function maskDmy(raw: string): string {
  // A pasted ISO date is turned around rather than shredded into digits.
  if (ISO.test(raw.trim())) return isoToDmy(raw.trim())
  const parts = raw.split(/[/.-]/)
  let digits = ''
  // A one-digit day or month followed by a separator means "that part is
  // done": pad it so the mask's fixed slots line up with what was meant.
  for (let i = 0; i < parts.length; i++) {
    const p = parts[i].replace(/\D/g, '')
    const closed = i < parts.length - 1 && i < 2
    digits += closed && p.length === 1 ? `0${p}` : p
  }
  digits = digits.slice(0, 8)
  // A separator the user just typed after a complete part stays on screen,
  // otherwise the caret would sit before a slash that appears on the next key.
  const closing = /[/.-]$/.test(raw) && (digits.length === 2 || digits.length === 4) ? '/' : ''
  if (digits.length <= 2) return digits + closing
  if (digits.length <= 4) return `${digits.slice(0, 2)}/${digits.slice(2)}${closing}`
  return `${digits.slice(0, 2)}/${digits.slice(2, 4)}/${digits.slice(4)}`
}

function isRealDate(iso: string): boolean {
  const m = ISO.exec(iso)
  if (!m) return false
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])]
  if (y < 1900 || mo < 1 || mo > 12 || d < 1) return false
  return d <= new Date(Date.UTC(y, mo, 0)).getUTCDate()
}
