/** One unresolved ticket per account and tab. Persist before sending so reloads can recover it. */
export interface ManualOrderPayload {
  symbol: string
  side: 'BUY' | 'SELL'
  quantity: number
  limitPrice?: number | null
  stopLossPrice?: number | null
  targetPrice?: number | null
  carryForward?: boolean
}
export type ManualOrderIntent = ManualOrderPayload & { clientOrderId: string }
const storageKey = (userId: number) => `openfno.manual-order.${userId}`
export function pendingOrder(storage: Storage, userId: number): ManualOrderIntent | null {
  const raw = storage.getItem(storageKey(userId))
  if (!raw) return null
  const parsed = JSON.parse(raw) as ManualOrderIntent
  if (!parsed.clientOrderId || !parsed.symbol || !['BUY', 'SELL'].includes(parsed.side) || !Number.isInteger(parsed.quantity))
    throw new Error('The saved order could not be read. Check your manual book before clearing browser storage.')
  return parsed
}
export function beginOrder(storage: Storage, userId: number, payload: ManualOrderPayload): ManualOrderIntent {
  const pending = pendingOrder(storage, userId)
  if (pending) return pending
  // randomUUID requires HTTPS; getRandomValues also supports a local HTTP desk.
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6] & 15) | 64
  bytes[8] = (bytes[8] & 63) | 128
  const hex = [...bytes].map(n => n.toString(16).padStart(2, '0')).join('')
  const clientOrderId = `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
  const intent = { ...payload, clientOrderId }
  storage.setItem(storageKey(userId), JSON.stringify(intent))
  return intent
}
export function finishOrder(storage: Storage, userId: number, clientOrderId: string): void {
  if (pendingOrder(storage, userId)?.clientOrderId === clientOrderId) storage.removeItem(storageKey(userId))
}
