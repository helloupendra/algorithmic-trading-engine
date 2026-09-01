/**
 * Shared run selector used by the Positions and Orders screens: both are
 * views *into a run*, so they share the same way of choosing one.
 */

import { useEffect } from 'react'
import { useSimulationRuns } from '../../lib/queries'
import { shortSymbol } from '../../lib/format'

export function RunPicker({
  value,
  onChange,
}: {
  value: number | null
  onChange: (id: number) => void
}) {
  const runs = useSimulationRuns()

  return (
    <select
      className="field__input field__input--sm"
      value={value ?? ''}
      onChange={(e) => onChange(Number(e.target.value))}
      aria-label="Simulation run"
    >
      <option value="" disabled>
        {runs.isPending ? 'Loading runs…' : 'Select a run'}
      </option>
      {(runs.data ?? []).map((run) => (
        <option key={run.id} value={run.id}>
          #{run.id} · {run.strategyName} · {shortSymbol(run.symbol)} · {run.status}
        </option>
      ))}
    </select>
  )
}

/** Picks the most recent run once loaded, so pages never start empty. */
export function useDefaultRunId(current: number | null, setter: (id: number) => void) {
  const runs = useSimulationRuns()
  const first = runs.data?.[0]?.id
  useEffect(() => {
    if (current == null && first != null) setter(first)
  }, [current, first, setter])
}
