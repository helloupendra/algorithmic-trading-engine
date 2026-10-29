/**
 * "Export CSV": the small action beside a table that hands the browser a file
 * of what the table holds (lib/csv.ts). The text is built when clicked, not
 * on every render of a table that polls every few seconds.
 */

import { downloadCsv } from '../lib/csv'
import { IconDownload } from './icons'

export function ExportCsvButton({
  fileName,
  csv,
  disabled = false,
  title = 'Download what this table holds as a CSV file',
}: {
  fileName: string
  /** The file's text, asked for on the click. */
  csv: () => string
  disabled?: boolean
  title?: string
}) {
  return (
    <button type="button" className="btn btn--ghost btn--sm" disabled={disabled} title={title} onClick={() => downloadCsv(fileName, csv())}>
      <IconDownload style={{ width: 13, height: 13 }} /> Export CSV
    </button>
  )
}
