/**
 * What a deploy record says happened. Two scripts write the record: the
 * Windows desk's auto-deploy.ps1 calls a deploy that went live "applied", the
 * Linux desk's desk.sh calls it "ok". Reading only the first as success made
 * the Desk report every Linux deploy as "Deploy failed".
 */

export type DeployState = 'live' | 'skipped' | 'failed'

/** The record's outcome as a state; null for a word neither script writes. */
export function deployState(outcome: string | null | undefined): DeployState | null {
  switch (outcome) {
    case 'applied':
    case 'ok':
      return 'live'
    case 'skipped':
      return 'skipped'
    case 'failed':
      return 'failed'
    default:
      return null
  }
}

/**
 * desk.sh joins its notes with a bare ";" (the first character of its IFS);
 * read them as a list. A note's own "; " (with its space) is prose, and stays.
 */
export function deploySummary(summary: string | null | undefined): string {
  return (summary ?? '')
    .split(/;(?!\s)/)
    .map((part) => part.trim())
    .filter(Boolean)
    .join(' · ')
}
