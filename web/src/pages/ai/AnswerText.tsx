/**
 * A model's answer as readable text: paragraphs, headings, lists, bold,
 * italic, inline code, fenced code and simple tables, from lib/ai's
 * parseAnswer. Every piece is a React text node; nothing a model writes is
 * ever handed to the DOM as HTML, so an answer that contains markup shows
 * the markup as text. Links stay text too: a URL a model wrote is not one
 * the desk vouches for.
 */

import { memo, useMemo } from 'react'
import type { ReactNode } from 'react'
import { parseAnswer } from '../../lib/ai'
import type { Block, Inline } from '../../lib/ai'

function inlines(parts: readonly Inline[]): ReactNode[] {
  return parts.map((part, i) => {
    switch (part.kind) {
      case 'text':
        return part.text
      case 'bold':
        return <strong key={i}>{inlines(part.children)}</strong>
      case 'em':
        return <em key={i}>{inlines(part.children)}</em>
      case 'code':
        return <code key={i}>{part.text}</code>
      case 'break':
        return <br key={i} />
    }
  })
}

function block(b: Block, i: number): ReactNode {
  switch (b.kind) {
    case 'para':
      return <p key={i}>{inlines(b.inlines)}</p>
    case 'heading':
      // Headings inside an answer stay small: the answer is one card, not a document.
      return (
        <p key={i} className={`ai-md__h ai-md__h--${Math.min(b.level, 3)}`}>
          {inlines(b.inlines)}
        </p>
      )
    case 'list':
      return (
        <div key={i} className="ai-md__list" role="list">
          {b.items.map((item, j) => (
            <div key={j} className={`ai-md__li ai-md__li--d${item.depth}`} role="listitem">
              <span className="ai-md__marker" aria-hidden="true">
                {item.marker}
              </span>
              <span className="ai-md__item">{inlines(item.inlines)}</span>
            </div>
          ))}
        </div>
      )
    case 'code':
      return (
        <pre key={i} className="ai-md__code" data-lang={b.lang || undefined}>
          <code>{b.text}</code>
        </pre>
      )
    case 'table':
      return (
        <div key={i} className="tablewrap ai-md__table">
          <table className="table">
            <thead>
              <tr>
                {b.head.map((cell, j) => (
                  <th key={j}>{inlines(cell)}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {b.rows.map((row, r) => (
                <tr key={r}>
                  {row.map((cell, j) => (
                    <td key={j}>{inlines(cell)}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )
    case 'quote':
      return (
        <blockquote key={i} className="ai-md__quote">
          {inlines(b.inlines)}
        </blockquote>
      )
    case 'rule':
      return <hr key={i} className="ai-md__rule" />
  }
}

/** Memoised on the text: a finished answer is not re-parsed while the next one streams. */
export const AnswerText = memo(function AnswerText({ text }: { text: string }) {
  const blocks = useMemo(() => parseAnswer(text), [text])
  return <div className="ai-md">{blocks.map(block)}</div>
})
