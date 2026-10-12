import { describe, expect, it } from 'vitest'
import {
  ACTION_TOOLS,
  actionKindLabel,
  closeCountText,
  isPartlyDone,
  isPinShape,
  pinProblem,
  proposalsOf,
  readAnswer,
  readCloseItems,
  readPinStatus,
  readProposal,
  readRefusal,
  secondsLeft,
  waitText,
} from './aiActions'
import { ApiError } from './api'
import { toolLabel } from './ai'
import type { AiToolStep } from './ai'

/** A proposal tool's step as the stream's `tool` event carries it: the exact JSON the model was given. */
function step(name: string, data: Record<string, unknown>, ok = true): AiToolStep {
  return {
    round: 1,
    id: `call-${name}`,
    name,
    arguments: '{}',
    ok,
    error: ok ? null : 'refused',
    seconds: 0.1,
    rows: 1,
    asOfUtc: null,
    summary: '',
    resultChars: 0,
    result: JSON.stringify({ tool: name, fetchedAt: '2026-10-12 10:30:00 IST', rows: 1, data }),
  }
}

const proposal = (id: number, summary = 'Stop run 512 (Ghost Tangent Crossings on NIFTY, upendra’s account).') => ({
  proposed: true,
  actionId: id,
  kind: 'stop_run',
  summary,
  expiresAt: '10:32 IST',
  expiresUtc: '2026-10-12T05:02:00Z',
  confirm: 'The owner confirms it with his PIN in the card under this answer.',
})

describe('readProposal', () => {
  it('reads the stored action from a proposal tool’s result', () => {
    expect(readProposal(step('propose_stop_run', proposal(12)))).toEqual({
      id: 12,
      kind: 'stop_run',
      summary: 'Stop run 512 (Ghost Tangent Crossings on NIFTY, upendra’s account).',
      expiresUtc: '2026-10-12T05:02:00Z',
      items: null,
    })
  })

  it('reads an account close’s full list for the card', () => {
    const items = {
      account: 'admin',
      runCount: 2,
      positionCount: 1,
      openLegs: 3,
      runs: [
        { runId: 512, strategy: 'Ghost Tangent Crossings', underlying: 'NIFTY', openLegs: 2, net: -1240.4 },
        { runId: 540, strategy: 'Fulcrum', underlying: 'BANKNIFTY', openLegs: 1 },
        { strategy: 'no id: dropped' },
      ],
      positions: [{ positionId: 77, contract: 'NIFTY 25100 CE · 27 Oct', side: 'BUY', lots: 2, unrealized: 15 }],
      notListed: 0,
    }
    const read = readProposal(step('propose_close_account', { ...proposal(14, 'Close everything in admin’s account: …'), kind: 'close_account', items }))

    expect(read?.kind).toBe('close_account')
    expect(read?.items).toEqual({
      account: 'admin',
      runCount: 2,
      positionCount: 1,
      openLegs: 3,
      runs: [
        { runId: 512, strategy: 'Ghost Tangent Crossings', underlying: 'NIFTY', openLegs: 2, net: -1240.4 },
        { runId: 540, strategy: 'Fulcrum', underlying: 'BANKNIFTY', openLegs: 1, net: null },
      ],
      positions: [{ positionId: 77, contract: 'NIFTY 25100 CE · 27 Oct', side: 'BUY', lots: 2, unrealized: 15 }],
      notListed: 0,
    })
    expect(closeCountText(read!.items!)).toBe('2 runs (3 open legs) · 1 manual position')
    expect(closeCountText({ runCount: 0, positionCount: 3, openLegs: 0 })).toBe('3 manual positions')
    expect(readCloseItems({ runs: 'nope' })).toBeNull()
    expect(readCloseItems(undefined)).toBeNull()
  })

  it('ignores a refused proposal, a read tool and a result that is not the shape', () => {
    expect(readProposal(step('propose_stop_run', proposal(12), false))).toBeNull()
    expect(readProposal(step('get_runs', proposal(12)))).toBeNull()
    expect(readProposal(step('propose_stop_run', { ...proposal(12), proposed: false }))).toBeNull()
    expect(readProposal(step('propose_stop_run', { ...proposal(0) }))).toBeNull()
    expect(readProposal({ name: 'propose_stop_run', ok: true, result: '{"tool":"propose_stop_run","error":"cut' })).toBeNull()
  })

  it('knows all five proposal tools', () => {
    expect(ACTION_TOOLS).toEqual(['propose_stop_run', 'propose_start_run', 'propose_square_off', 'propose_agent_switch', 'propose_close_account'])
    for (const name of ACTION_TOOLS) expect(readProposal(step(name, proposal(3)))?.id).toBe(3)
  })
})

describe('proposalsOf', () => {
  it('lists each action once, in order, and marks all but the last as replaced', () => {
    const rounds = [
      { tools: [step('get_runs', {}), step('propose_stop_run', proposal(7))] },
      { tools: [step('propose_stop_run', proposal(7)), step('propose_start_run', { ...proposal(8), kind: 'start_run' })] },
    ]
    expect(proposalsOf(rounds).map((p) => [p.action.id, p.replaced])).toEqual([
      [7, true],
      [8, false],
    ])
    expect(proposalsOf([{ tools: [step('get_runs', {})] }])).toEqual([])
  })
})

describe('the PIN', () => {
  it('is 4 to 8 digits and nothing else', () => {
    for (const pin of ['1234', '12345678', '0000']) expect(isPinShape(pin)).toBe(true)
    for (const pin of ['123', '123456789', '12a4', ' 1234', '१२३४', '']) expect(isPinShape(pin)).toBe(false)
  })

  it('says why a new one cannot be saved yet', () => {
    expect(pinProblem('')).toBe('Type 4 to 8 digits.')
    expect(pinProblem('12')).toBe('A PIN is 4 to 8 digits, nothing else.')
    expect(pinProblem('4321', '4322')).toBe('The two PINs differ.')
    expect(pinProblem('4321', '4321')).toBeNull()
  })

  it('reads its status, never the PIN', () => {
    expect(readPinStatus({ enabled: true, set: true, lockedUntilUtc: null, confirmSeconds: 120 })).toEqual({
      enabled: true,
      set: true,
      lockedUntilUtc: null,
      confirmSeconds: 120,
    })
    expect(readPinStatus({})).toEqual({ enabled: true, set: false, lockedUntilUtc: null, confirmSeconds: 120 })
  })
})

describe('what the API said', () => {
  const view = (status: string) => ({ id: 12, kind: 'stop_run', status, summary: 'Stop run 512.', channel: 'console', createdUtc: '', expiresUtc: '' })

  it('reads a carried-out action, a refused one and a cancelled one', () => {
    expect(readAnswer({ message: 'Done: run 512 stopped at 11:42 IST.', action: view('executed') })).toMatchObject({ kind: 'ran', done: true })
    expect(readAnswer({ message: 'Not done: run 512 is not running.', action: view('failed') })).toMatchObject({ kind: 'ran', done: false })
    expect(readAnswer({ message: 'Cancelled action #12: nothing was done.', action: view('cancelled') })).toEqual({
      kind: 'cancelled',
      message: 'Cancelled action #12: nothing was done.',
    })
  })

  it('reads a wrong PIN, a lock and an action that no longer waits', () => {
    expect(readRefusal(new ApiError(403, 'Wrong PIN', { error: 'Wrong PIN: 2 tries left.', attemptsLeft: 2 }))).toEqual({
      kind: 'wrong',
      message: 'Wrong PIN: 2 tries left.',
      attemptsLeft: 2,
    })
    expect(readRefusal(new ApiError(423, 'Locked', { error: 'Actions are locked until 10:45 IST.', lockedUntilUtc: '2026-10-12T05:15:00Z' }))).toEqual({
      kind: 'locked',
      message: 'Actions are locked until 10:45 IST.',
      lockedUntilUtc: '2026-10-12T05:15:00Z',
    })
    expect(readRefusal(new ApiError(409, 'x', { error: 'Action #12 expired at 10:32 IST.', action: view('expired') }))).toEqual({
      kind: 'closed',
      message: 'Action #12 expired at 10:32 IST.',
      status: 'expired',
    })
    // A 403 with no body is the admin policy, not a wrong PIN.
    expect(readRefusal(new ApiError(403, 'You do not have permission to do that.'))).toMatchObject({ kind: 'closed' })
    expect(readRefusal(new ApiError(404, 'Not found'))).toMatchObject({ kind: 'closed' })
  })
})

describe('a proposal in the tool steps', () => {
  it('reads as what it proposes, with its target', () => {
    expect(toolLabel('propose_stop_run', '{"run_id":512}')).toEqual({ label: 'Stop run #512', detail: '' })
    expect(toolLabel('propose_agent_switch', '{"agent":"ai-trader","on":false}')).toEqual({ label: 'Switch ai-trader', detail: 'on: false' })
    expect(toolLabel('propose_square_off', '{"all":true}')).toEqual({ label: 'Square off', detail: 'all: true' })
    expect(toolLabel('propose_close_account', '{"account":"coderforchange"}')).toEqual({ label: 'Close account coderforchange', detail: '' })
  })
})

describe('the card’s words', () => {
  it('names each action and counts down its wait', () => {
    expect(['stop_run', 'start_run', 'square_off', 'agent_switch', 'close_account', 'x'].map(actionKindLabel)).toEqual([
      'Stop a run',
      'Start a run',
      'Square off',
      'Switch an agent',
      'Close an account',
      'Action',
    ])
    expect(isPartlyDone('Partly done: admin’s account at 12:41 IST: 6 runs stopped; 1 failed: run 530 (no answer).')).toBe(true)
    expect(isPartlyDone('Not done: nothing in admin’s account was closed at 12:41 IST; 1 failed: run 530 (no answer).')).toBe(false)
    const now = Date.parse('2026-10-12T05:00:18Z')
    expect(secondsLeft('2026-10-12T05:02:00Z', now)).toBe(102)
    expect(waitText(102)).toBe('1:42 left')
    expect(waitText(secondsLeft('2026-10-12T05:00:00Z', now))).toBe('expired')
    expect(waitText(null)).toBe('waiting for your PIN')
  })
})
