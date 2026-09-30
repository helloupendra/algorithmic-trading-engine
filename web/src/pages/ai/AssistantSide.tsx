/**
 * Beside the Assistant's conversation: the Desk Assistant on Telegram, and a
 * search of the desk's own docs, the same search its search_docs tool makes.
 *
 * Telegram: the bot answers only accounts linked here, in private chats; a
 * stranger's /start gets "This bot is private." and nothing else. Linking is
 * a one-time code: "Link Telegram" asks the API for one, the owner sends
 * /pair CODE to the bot within 10 minutes, and this card, polling every 5 s
 * while the code shows, sees the new account appear. The token and the key
 * stay on the server; the API says only whether the bot is running.
 *
 * Docs search: docs/ (module docs and strategy specs) cut into passages and
 * embedded by the embed tier's model; a query returns the closest passages,
 * each linked to its page on openfno.com/docs when the file is published.
 */

import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import {
  botLink,
  callTime,
  clockTime,
  countdownText,
  docsLink,
  modelName,
  telegramState,
  useAiTelegram,
  useDocsSearch,
  usePairTelegram,
  useUnlinkTelegram,
} from '../../lib/ai'
import type { AiTelegramPairing } from '../../lib/ai'
import { ApiError } from '../../lib/api'
import { Badge, Panel } from '../../components/ui'
import { IconSearch } from '../../components/icons'
import { errorText, useNow } from './common'

/** The Desk Assistant on Telegram: whether it runs, who is linked, and linking one more. */
export function TelegramCard() {
  const [pairing, setPairing] = useState<AiTelegramPairing | null>(null)
  const [linked, setLinked] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<string | null>(null)
  const before = useRef<Set<string> | null>(null)
  const now = useNow(1_000, pairing != null)
  const expired = pairing != null && Date.parse(pairing.expiresUtc) <= now
  const status = useAiTelegram(pairing != null && !expired)
  const pair = usePairTelegram()
  const unlink = useUnlinkTelegram()
  const t = status.data

  // An account that was not there when the code was asked for is the one that just sent /pair.
  const fresh = pairing && before.current && t ? t.owners.find((o) => !before.current!.has(o.telegramUserId)) : undefined
  useEffect(() => {
    if (!fresh) return
    setLinked(fresh.telegramName || fresh.telegramUserId)
    setPairing(null)
    before.current = null
  }, [fresh])

  const start = () => {
    setLinked(null)
    before.current = new Set((t?.owners ?? []).map((o) => o.telegramUserId))
    pair.mutate(undefined, { onSuccess: (p) => setPairing(p) })
  }

  const state = t ? telegramState(t) : null
  const bot = botLink(pairing?.botUsername ?? t?.botUsername)
  const botName = (pairing?.botUsername ?? t?.botUsername ?? '').replace(/^@/, '')

  return (
    <Panel title={<>Telegram</>} className="ai-side">
      {status.isPending ? (
        <p className="faint ai-flush">Asking the API about the bot…</p>
      ) : !t ? (
        <p className="small-note warn ai-flush">
          {status.error instanceof ApiError && status.error.status === 404
            ? 'This API build has no Telegram bot yet.'
            : `The bot's state could not be read: ${errorText(status.error)}`}
        </p>
      ) : (
        <>
          <p className="ai-side__line">
            <Badge tone={state!.tone === 'pos' ? 'pos' : state!.tone === 'warn' ? 'warn' : 'neutral'}>{state!.label}</Badge>
            {bot ? (
              <a className="ai-subject" href={bot} target="_blank" rel="noopener noreferrer">
                @{botName} ↗
              </a>
            ) : (
              <span className="faint">no bot name known</span>
            )}
          </p>
          <p className="small-note ai-flush">{state!.note} Strangers get "This bot is private."; group chats are ignored.</p>

          <div className="ai-side__sub">Linked accounts</div>
          {t.owners.length === 0 ? (
            <p className="faint ai-flush">None yet.</p>
          ) : (
            <ul className="ai-owners">
              {t.owners.map((o) => (
                <li key={o.telegramUserId} className="ai-owner">
                  <span className="ai-owner__who">
                    <b>{o.telegramName || o.telegramUserId}</b>
                    <span className="faint">
                      {' '}
                      · {o.consoleUser}
                      {o.linkedUtc ? ` · linked ${callTime(o.linkedUtc, now)}` : ''}
                    </span>
                  </span>
                  {confirm === o.telegramUserId ? (
                    <span className="ai-owner__confirm">
                      <span className="small">The bot stops answering them.</span>
                      <button
                        type="button"
                        className="btn btn--danger btn--sm"
                        disabled={unlink.isPending}
                        onClick={() => unlink.mutate(o.telegramUserId, { onSuccess: () => setConfirm(null) })}
                      >
                        {unlink.isPending ? 'Unlinking…' : 'Unlink'}
                      </button>
                      <button type="button" className="btn btn--ghost btn--sm" onClick={() => setConfirm(null)}>
                        Cancel
                      </button>
                    </span>
                  ) : (
                    <button type="button" className="btn btn--ghost btn--sm" onClick={() => setConfirm(o.telegramUserId)}>
                      Unlink
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
          {unlink.isError && (
            <div className="alert alert--error ai-flush" role="alert">
              {errorText(unlink.error)}
            </div>
          )}
          {linked && (
            <div className="alert alert--success ai-flush" role="status">
              Linked {linked}. The bot answers them now.
            </div>
          )}

          {pairing ? (
            <div className="ai-pair">
              <div className={`ai-pair__code mono ${expired ? 'faint' : ''}`} aria-label="Pairing code">
                {pairing.code}
              </div>
              <p className="ai-flush">
                {expired ? (
                  <span className="warn">This code has expired.</span>
                ) : (
                  <>
                    Send <span className="mono">/pair {pairing.code}</span> to{' '}
                    {bot ? (
                      <a className="ai-subject" href={bot} target="_blank" rel="noopener noreferrer">
                        @{botName} ↗
                      </a>
                    ) : (
                      'the bot'
                    )}{' '}
                    in a private chat. <span className="faint">{countdownText(pairing.expiresUtc, now)}</span>
                  </>
                )}
              </p>
              <p className="small-note ai-flush">The code works once. This card looks for the new account every 5 s.</p>
              <div className="ai-pair__tools">
                {expired && (
                  <button type="button" className="btn btn--sm" disabled={pair.isPending} onClick={start}>
                    New code
                  </button>
                )}
                <button type="button" className="btn btn--ghost btn--sm" onClick={() => setPairing(null)}>
                  {expired ? 'Close' : 'Cancel'}
                </button>
              </div>
            </div>
          ) : (
            <div className="ai-pair__tools">
              <button type="button" className="btn btn--sm" disabled={pair.isPending} onClick={start}>
                {pair.isPending ? 'Asking for a code…' : 'Link Telegram'}
              </button>
              <span className="faint small">/new starts a new conversation; /unlink removes the link.</span>
            </div>
          )}
          {pair.isError && (
            <div className="alert alert--error ai-flush" role="alert">
              {errorText(pair.error)}
            </div>
          )}
        </>
      )}
    </Panel>
  )
}

/** A search of the desk's docs, as the Desk Assistant's search_docs tool sees them. */
export function DocsSearchCard() {
  const [draft, setDraft] = useState('')
  const [query, setQuery] = useState('')
  const search = useDocsSearch(query)
  const now = useNow(60_000)
  const d = search.data
  const onSubmit = (e: FormEvent) => {
    e.preventDefault()
    setQuery(draft.trim())
  }
  const failed = search.isError
    ? search.error instanceof ApiError && search.error.status === 503
      ? 'No NVIDIA key on the server, so nothing can be searched.'
      : search.error instanceof ApiError && search.error.status === 502
        ? `The query could not be turned into a vector: ${errorText(search.error)}`
        : search.error instanceof ApiError && search.error.status === 404
          ? 'This API build has no docs search yet.'
          : `The search failed: ${errorText(search.error)}`
    : null

  return (
    <Panel title={<>Docs search</>} className="ai-side">
      {d ? (
        <p className="small-note ai-flush">
          {d.index.files} docs · {d.index.passages.toLocaleString('en-IN')} passages ·{' '}
          {d.index.indexedUtc ? `indexed ${clockTime(d.index.indexedUtc, now)} IST` : 'not indexed yet'} ·{' '}
          <span title={d.index.model}>{modelName(d.index.model)}</span>
        </p>
      ) : search.isPending ? (
        <p className="faint ai-flush">Asking the API about the index…</p>
      ) : null}
      <p className="small-note ai-flush">What the Desk Assistant finds when it searches the desk's docs and strategy specs.</p>
      <form className="ai-docs__form" onSubmit={onSubmit} role="search">
        <input
          className="field__input field__input--sm ai-docs__input"
          type="search"
          placeholder="Search the docs…"
          aria-label="Search the docs"
          value={draft}
          maxLength={500}
          onChange={(e) => setDraft(e.target.value)}
        />
        <button type="submit" className="btn btn--sm" disabled={!draft.trim() || search.isFetching}>
          <IconSearch /> Search
        </button>
      </form>
      {failed && <p className="small-note warn ai-flush">{failed}</p>}
      {d && d.query && (
        <>
          {d.hits.length === 0 ? (
            <p className="faint ai-flush">Nothing in the docs matches “{d.query}”.</p>
          ) : (
            <ol className="ai-hits" aria-label={`Passages for ${d.query}`}>
              {d.hits.map((h, i) => {
                const link = docsLink(h.file, h.section)
                return (
                  <li key={`${h.file}-${i}`} className="ai-hit">
                    <div className="ai-hit__head">
                      {link ? (
                        <a className="ai-subject mono ai-hit__file" href={link} target="_blank" rel="noopener noreferrer" title="Read it on openfno.com/docs">
                          {h.file} ↗
                        </a>
                      ) : (
                        <span className="mono ai-hit__file" title="Not published on the docs site">
                          {h.file}
                        </span>
                      )}
                      {h.score != null && <span className="faint ai-hit__score">{h.score.toFixed(2)}</span>}
                    </div>
                    {h.section && <div className="ai-hit__section">{h.section}</div>}
                    <p className="ai-hit__text">{h.text}</p>
                  </li>
                )
              })}
            </ol>
          )}
        </>
      )}
    </Panel>
  )
}
