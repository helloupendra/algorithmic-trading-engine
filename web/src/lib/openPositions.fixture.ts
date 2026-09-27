/**
 * One open leg as GET /api/Positions/open answers it, for the tests that
 * read that answer (the Desk's legs, the Positions page's groups). A bought
 * NIFTY 23300 PE of admin's Ghost run on Monday 28 Sep 2026, marked live.
 */

import type { OpenPosition } from './types'

export function openPosition(over: Partial<OpenPosition> = {}): OpenPosition {
  return {
    positionId: 9001,
    runId: 612,
    strategyName: 'GhostTangentCrossings',
    isManualBook: false,
    userId: 1,
    userName: 'admin',
    groupId: 'g1',
    symbol: 'NSE:NIFTY2692923300PE',
    underlying: 'NIFTY',
    expiryDate: '2026-09-29',
    strike: 23300,
    optionType: 'PE',
    label: 'NIFTY 23300 PE · 29 Sep',
    direction: 'LONG',
    lots: 2,
    lotSize: 65,
    quantity: 130,
    entryPrice: 64.8,
    markPrice: 69.57,
    markUtc: '2026-09-28T06:12:04Z',
    markAgeSeconds: 4,
    unrealizedPnl: 620,
    carryForward: false,
    carriedFromRunId: null,
    carriedFromStrategy: null,
    stopLossPrice: null,
    targetPrice: null,
    openedUtc: '2026-09-28T05:42:00Z',
    greeks: null,
    ...over,
  }
}
