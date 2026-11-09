import { describe, expect, it } from 'vitest'
import { beginOrder, finishOrder, pendingOrder } from './manualOrderIntent'

function storage(): Storage {
  const values = new Map<string, string>()
  return { get length() { return values.size }, clear: () => values.clear(), getItem: k => values.get(k) ?? null,
    key: i => [...values.keys()][i] ?? null, removeItem: k => { values.delete(k) }, setItem: (k, v) => { values.set(k, v) } }
}
const ticket = { symbol: 'NSE:TEST-EQ', side: 'BUY' as const, quantity: 2, stopLossPrice: 90, targetPrice: 120 }
describe('manual order recovery', () => {
  it('persists the original payload and key through a reload and a second click', () => {
    const s = storage()
    const first = beginOrder(s, 7, ticket)
    expect(first.clientOrderId).toMatch(/^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/)
    expect(pendingOrder(s, 7)).toEqual(first)
    expect(beginOrder(s, 7, { ...ticket, quantity: 3 })).toEqual(first)
    expect(pendingOrder(s, 8)).toBeNull()
    expect(beginOrder(s, 8, ticket).clientOrderId).not.toEqual(first.clientOrderId)
  })
  it('does not let a late response clear a different unresolved order', () => {
    const s = storage()
    const first = beginOrder(s, 7, ticket)
    finishOrder(s, 7, first.clientOrderId)
    const second = beginOrder(s, 7, ticket)
    finishOrder(s, 7, first.clientOrderId)
    expect(pendingOrder(s, 7)).toEqual(second)
  })
  it('fails closed when storage cannot preserve a recovery key', () => {
    const s = storage()
    s.setItem = () => { throw new Error('storage unavailable') }
    expect(() => beginOrder(s, 7, ticket)).toThrow('storage unavailable')
  })
  it('does not silently discard an unreadable pending ticket', () => {
    const s = storage()
    s.setItem('openfno.manual-order.7', '{')
    expect(() => beginOrder(s, 7, ticket)).toThrow()
    expect(s.length).toBe(1)
  })
})
