/**
 * The regular hours of NSE and BSE, cash and derivatives, on any IST day: the
 * console's one copy of the rule the API keeps in EquityMarketHours.cs and the
 * engine in core/market_hours.py.
 *
 * Until 6 Dec 2026 both segments close at 15:30. SEBI's circular of 15 Oct 2026
 * keeps the cash market (and so the indices, computed from it) to 15:30 and
 * moves the derivatives close to 15:45 from 7 Dec 2026, except that expiring
 * index contracts still stop at 15:30 on their expiry day. A day before the
 * switch reads as it always did; a later circular is one change here.
 */

/** IST is UTC+05:30 all year. */
const IST_OFFSET_MS = 330 * 60_000

/** The cash market's close on every day, "HH:MM" IST. */
export const CASH_CLOSE_IST = '15:30'

/** The first IST day ("yyyy-mm-dd") the derivatives close at LATER_DERIVATIVES_CLOSE_IST. */
export const LATER_DERIVATIVES_CLOSE_FROM = '2026-12-07'

/** The derivatives close from LATER_DERIVATIVES_CLOSE_FROM, "HH:MM" IST. */
export const LATER_DERIVATIVES_CLOSE_IST = '15:45'

/** The IST calendar day of an instant, "yyyy-mm-dd". */
export function istDayOf(ms: number): string {
  return new Date(ms + IST_OFFSET_MS).toISOString().slice(0, 10)
}

/** When NSE and BSE options and futures stop trading on the IST day `day` ("yyyy-mm-dd"), "HH:MM" IST. */
export function derivativesCloseIst(day: string): string {
  return day >= LATER_DERIVATIVES_CLOSE_FROM ? LATER_DERIVATIVES_CLOSE_IST : CASH_CLOSE_IST
}

/** Minutes from 00:00 IST of an "HH:MM". */
export function minutesOf(hm: string): number {
  return Number(hm.slice(0, 2)) * 60 + Number(hm.slice(3, 5))
}

/** A daytime IST "HH:MM" as the UTC "HH:MM" an ISO stamp carries: "15:30" is "10:00". */
export function utcHm(istHm: string): string {
  const m = minutesOf(istHm) - 330
  return `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`
}

/**
 * An option or a future in the FYERS grammar the desk stores
 * ("NSE:NIFTY2612224500CE", "NSE:BANKNIFTY26DECFUT"); a share ("-EQ") or an
 * index ("-INDEX") is not.
 */
export function isDerivativeSymbol(symbol: string | null | undefined): boolean {
  return /\d(CE|PE)$|FUT$/i.test((symbol ?? '').trim())
}

/** When an NSE or BSE symbol stops trading on `day`: a share or an index at the cash close, a derivative at the derivatives close. */
export function nseCloseIstOf(symbol: string | null | undefined, day: string): string {
  return isDerivativeSymbol(symbol) ? derivativesCloseIst(day) : CASH_CLOSE_IST
}
