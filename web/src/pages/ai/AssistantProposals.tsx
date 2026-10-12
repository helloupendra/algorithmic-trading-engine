/**
 * The confirm cards under an answer that proposed one of Ira's actions, for
 * both faces of the assistant (AI → Assistant and the phone app): each
 * proposal the answer's tools stored, with the console's own card
 * (AssistantActions ActionCard), once the answer has finished streaming.
 * Outcomes are the page's (useActionOutcomes), so a card switched away from
 * and back is found as it was.
 */

import type { ChatTurn } from '../../lib/ai'
import { proposalsOf } from '../../lib/aiActions'
import type { ActionOutcome } from '../../lib/aiActions'
import { ActionCard } from './AssistantActions'

export function TurnActions({
  turn,
  newest,
  outcomes,
  onOutcome,
}: {
  turn: ChatTurn
  /** The newest answer's last card takes the keyboard, so the PIN can be typed at once. */
  newest: boolean
  outcomes: Readonly<Record<number, ActionOutcome>>
  onOutcome: (id: number, outcome: ActionOutcome) => void
}) {
  if (turn.status === 'streaming') return null
  const proposals = proposalsOf(turn.rounds)
  if (proposals.length === 0) return null
  return (
    <div className="ai-actions">
      {proposals.map(({ action, replaced }, i) => (
        <ActionCard
          key={action.id}
          action={action}
          replaced={replaced}
          outcome={outcomes[action.id] ?? null}
          onOutcome={(o) => onOutcome(action.id, o)}
          focus={newest && i === proposals.length - 1}
        />
      ))}
    </div>
  )
}
