/**
 * Strategies module — the spec of one strategy (docs/strategies/<Name>.md),
 * rendered as the author wrote it: GFM tables, $...$ maths, code.
 *
 * Loaded with React.lazy from the Library page: react-markdown, the remark
 * and rehype plugins and KaTeX (with its CSS and fonts) are several hundred
 * kilobytes that no other screen needs, so they ship in their own chunk and
 * are fetched the first time somebody opens a spec. Everything the panel
 * needs, including its stylesheet, is imported here and nowhere else — that
 * is what keeps the chunk separate.
 */

import Markdown from 'react-markdown'
import type { Components } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import remarkMath from 'remark-math'
import rehypeKatex from 'rehype-katex'
import 'katex/dist/katex.min.css'
import './spec.css'
import { FACT_CHIPS, SPEC_HEADINGS, useStrategySpec } from '../../lib/specs'
import type { StrategySpec } from '../../lib/specs'
import { InlineError, Loading } from '../../components/ui'

const README_URL = 'https://github.com/helloupendra/algorithmic-trading-engine/blob/main/docs/strategies/README.md'

const remarkPlugins = [remarkGfm, remarkMath]
const rehypePlugins = [rehypeKatex]

/**
 * A spec's tables are wide (the data table has five columns, the parameters
 * table five); wrapped so each scrolls on its own and the page never does.
 * `node` is the hast node react-markdown adds to every element's props — it
 * is dropped so it does not reach the DOM as an attribute.
 */
/** "Position management" → "position-management": the anchor the page's table of contents jumps to. */
export function headingSlug(text: string): string {
  return text.toLowerCase().replace(/\(.*?\)/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')
}

function textOf(children: unknown): string {
  if (typeof children === 'string') return children
  if (Array.isArray(children)) return children.map(textOf).join('')
  if (children && typeof children === 'object' && 'props' in children) return textOf((children as { props: { children?: unknown } }).props.children)
  return ''
}

const components: Components = {
  // Every H2 gets the id the table of contents links to.
  h2: ({ node, children, ...props }) => {
    void node
    return (
      <h2 id={headingSlug(textOf(children))} {...props}>
        {children}
      </h2>
    )
  },
  table: ({ node, ...props }) => {
    void node
    return (
      <div className="spec__tablewrap">
        <table {...props} />
      </div>
    )
  },
  a: ({ node, href, ...props }) => {
    void node
    // Specs link out to broker docs and to GitHub; the console stays open.
    // A same-document anchor ("#entry") keeps the default behaviour.
    const external = typeof href === 'string' && /^https?:\/\//.test(href)
    return <a {...props} href={href} target={external ? '_blank' : undefined} rel={external ? 'noreferrer' : undefined} />
  },
}

function formatUpdated(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return d.toLocaleDateString('en-IN', {
    timeZone: 'Asia/Kolkata',
    day: '2-digit',
    month: 'short',
    year: 'numeric',
  })
}

function FactChips({ facts }: { facts: StrategySpec['facts'] }) {
  if (!facts) return null
  const chips = FACT_CHIPS.filter((c) => (facts[c.key] ?? '').trim().length > 0)
  if (chips.length === 0) return null
  return (
    <div className="spec__facts" aria-label="Facts">
      {chips.map((c) => (
        <span key={c.key} className="spec__fact">
          <span className="spec__fact-key">{c.label}</span>
          <span className="spec__fact-value mono">{facts[c.key]}</span>
        </span>
      ))}
    </div>
  )
}

/**
 * What the author still owes. The list of headings is the README's, so the
 * empty state and the test that enforces the file agree on the contract.
 */
function SpecPending({ spec }: { spec: StrategySpec }) {
  return (
    <div className="spec__pending">
      <p className="spec__pending-title">Spec pending</p>
      <p>
        <b>{spec.name}</b> can be launched but has no specification yet. Until it does, the
        engine's test suite fails for it and this page cannot say what a run will do.
      </p>
      <p>
        The author writes <code className="mono">{spec.path}</code> with these sections, in this order:
      </p>
      <ol className="spec__pending-list">
        {SPEC_HEADINGS.map((h) => (
          <li key={h}>{h}</li>
        ))}
      </ol>
      <p>
        Each section's contents, the maths convention and the worked-example rule (every number
        traceable to a database row) are in the{' '}
        <a href={README_URL} target="_blank" rel="noreferrer">
          authoring guide
        </a>
        . The strategy's name is then removed from <code className="mono">PENDING_SPECS</code> in{' '}
        <code className="mono">tests/test_strategy_specs.py</code>.
      </p>
    </div>
  )
}

export function StrategySpecPanel({ strategyId, hideFacts = false }: { strategyId: number; hideFacts?: boolean }) {
  const spec = useStrategySpec(strategyId)

  if (spec.isPending) return <Loading label="Loading spec…" />
  if (spec.isError) return <InlineError error={spec.error} />

  const data = spec.data
  if (!data.hasSpec || data.markdown == null) return <SpecPending spec={data} />

  // The facts block is what the chips above show; the page hides the
  // section itself so the document ends at Limitations.
  const body = hideFacts ? data.markdown.replace(/\n## Facts[\s\S]*$/, '') : data.markdown

  return (
    <div className="spec">
      <div className="spec__meta">
        <span className="mono">{data.path}</span>
        <span className="spec__updated">Updated {formatUpdated(data.updatedUtc)}</span>
      </div>
      <FactChips facts={data.facts} />
      <article className="spec__body">
        <Markdown remarkPlugins={remarkPlugins} rehypePlugins={rehypePlugins} components={components}>
          {body}
        </Markdown>
      </article>
    </div>
  )
}

// The Library page mounts this component with React.lazy, which wants a
// default export; the named one above is what a test would import.
export default StrategySpecPanel
