/**
 * Strategy specifications: docs/strategies/<Name>.md, served raw by
 * GET /api/Strategy/{id}/spec and rendered by StrategySpecPanel.
 *
 * Kept out of queries.ts on purpose: that module is imported by every page,
 * and the only consumer of a spec is the Library page's lazily loaded panel.
 * Nothing here polls — a spec changes when someone edits a file, and the page
 * re-fetches when a different strategy is selected.
 */

import { useQuery } from '@tanstack/react-query'
import { api } from './api'

/** The facts block of a spec as the API parsed it: flat `key: value` pairs. */
export type SpecFacts = Record<string, string>

export interface StrategySpec {
  /** Registry name; the spec file is named after it. */
  name: string
  /** False while docs/strategies/<Name>.md has not been written. */
  hasSpec: boolean
  /** The file as written; null when hasSpec is false. */
  markdown: string | null
  /** Repo-relative path, e.g. "docs/strategies/ShortStraddle.md", whether or not it exists. */
  path: string
  /** ISO timestamp of the file's last write; null when it does not exist. */
  updatedUtc: string | null
  /** Parsed yaml facts block; null without a spec or without a block. */
  facts: SpecFacts | null
}

/**
 * The fact keys the template requires (docs/strategies/README.md), in the
 * order the panel shows them. `name` is omitted: it is the panel's title.
 */
export const FACT_CHIPS: ReadonlyArray<{ key: string; label: string }> = [
  { key: 'evaluates_on', label: 'evaluates on' },
  { key: 'resolution', label: 'resolution' },
  { key: 'data', label: 'data' },
  { key: 'instruments', label: 'instruments' },
  { key: 'default_lots', label: 'default lots' },
  { key: 'built_in_exit', label: 'built-in exit' },
]

/** The H2 headings a spec must carry, in order — the same list the Python test enforces. */
export const SPEC_HEADINGS: ReadonlyArray<string> = [
  'Idea',
  'Data it needs',
  'Timeframe',
  'Entry',
  'Position management',
  'Exit',
  'Parameters',
  'Worked example',
  'Limitations',
  'Facts (machine-readable)',
]

function normalizeSpec(raw: Partial<StrategySpec> & { name: string }): StrategySpec {
  return {
    name: raw.name,
    hasSpec: raw.hasSpec === true,
    markdown: typeof raw.markdown === 'string' ? raw.markdown : null,
    path: raw.path ?? `docs/strategies/${raw.name}.md`,
    updatedUtc: raw.updatedUtc ?? null,
    facts: raw.facts && typeof raw.facts === 'object' ? raw.facts : null,
  }
}

/** The spec of one strategy by catalog id; disabled until an id is chosen. */
export function useStrategySpec(id: number | null) {
  return useQuery({
    queryKey: ['strategies', 'spec', id],
    queryFn: async () => normalizeSpec(await api.get<Partial<StrategySpec> & { name: string }>(`/api/Strategy/${id}/spec`)),
    enabled: id != null,
    // A file on disk: once read it is right until the author saves again,
    // which is minutes apart, not seconds.
    staleTime: 60_000,
  })
}
