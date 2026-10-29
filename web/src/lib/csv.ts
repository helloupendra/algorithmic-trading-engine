/**
 * Tables to CSV, in the browser. What a table holds goes out (every row the
 * page has, not the slice on screen), under headers a spreadsheet reads, with
 * numbers as plain numbers, times as ISO in IST, and a file name that says
 * what is in it. The text is RFC 4180: comma-separated, CRLF line ends, a
 * field quoted when it holds a comma, a quote or a line break.
 */

export type CsvValue = string | number | boolean | null | undefined

/** One column: its header and how a row fills it. */
export interface CsvColumn<T> {
  header: string
  value: (row: T) => CsvValue
}

/**
 * A text a spreadsheet would run as a formula: Excel and Sheets evaluate a
 * cell that starts with =, +, -, @ (or a tab or carriage return before one),
 * so a user name or a strategy note of "=HYPERLINK(…)" would be live in the
 * file of whoever opened it. Such a text is written with a leading
 * apostrophe, which the sheet shows as the text itself. Numbers are numbers
 * and are never touched: −45100 stays a negative number.
 */
const FORMULA_START = /^[=+\-@\t\r]/

/**
 * One field as CSV writes it: a number in plain digits (no grouping, so a
 * sheet reads it as a number; not finite is empty), empty for nothing, and a
 * text quoted, its quotes doubled, when it holds a comma, a quote or a line
 * break.
 */
export function csvField(value: CsvValue): string {
  if (value == null) return ''
  if (typeof value === 'number') return Number.isFinite(value) ? String(Object.is(value, -0) ? 0 : value) : ''
  if (typeof value === 'boolean') return value ? 'true' : 'false'
  const text = FORMULA_START.test(value) ? `'${value}` : value
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
}

/** The rows under a header line, every line ended by CRLF. */
export function toCsv<T>(rows: readonly T[], columns: readonly CsvColumn<T>[]): string {
  const lines = [columns.map((c) => csvField(c.header)), ...rows.map((row) => columns.map((c) => csvField(c.value(row))))]
  return lines.map((fields) => `${fields.join(',')}\r\n`).join('')
}

const IST_OFFSET_MS = 330 * 60_000

/**
 * "2026-10-29T11:00:00+05:30": an instant as an ISO time in IST, the zone
 * every time on the console is read in, with its offset so a sheet in any
 * zone reads the same instant. Empty for nothing or a time that is not one.
 */
export function istIsoTime(iso: string | null | undefined): string {
  if (!iso) return ''
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return ''
  return `${new Date(ms + IST_OFFSET_MS).toISOString().slice(0, 19)}+05:30`
}

/** "2026-10-29-1100": an instant's IST day and minute, for a file name. Empty for nothing. */
export function fileStamp(iso: string | null | undefined): string {
  const ist = istIsoTime(iso)
  return ist ? `${ist.slice(0, 10)}-${ist.slice(11, 13)}${ist.slice(14, 16)}` : ''
}

/**
 * "NIFTY-chain-2026-11-04-2026-10-29-1100.csv": the parts that say what is in
 * the file, joined by dashes, each made safe for a file name on any system
 * (anything but letters, digits, dots and underscores becomes a dash), the
 * empty ones left out.
 */
export function csvFileName(...parts: ReadonlyArray<string | null | undefined>): string {
  const safe = parts
    .map((p) => (p ?? '').replace(/[^A-Za-z0-9._]+/g, '-').replace(/^-+|-+$/g, ''))
    .filter(Boolean)
  return `${safe.length > 0 ? safe.join('-') : 'export'}.csv`
}

/**
 * Hands the browser the file to save. Written with a byte-order mark: Excel
 * reads a CSV without one in the machine's code page, which turns "₹" and
 * "−" into mojibake.
 */
export function downloadCsv(fileName: string, csv: string): void {
  const url = URL.createObjectURL(new Blob(['﻿', csv], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  // After the click has handed the file over: revoking at once can cancel it in some browsers.
  window.setTimeout(() => URL.revokeObjectURL(url), 0)
}
