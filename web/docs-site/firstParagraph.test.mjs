import { describe, expect, it } from 'vitest'
import { firstParagraph, firstSentence } from './build.mjs'

describe('firstParagraph (a docs page description)', () => {
  it('skips a strategy spec’s provenance line for the idea after it', () => {
    const md = [
      '# ChainFlowBuy',
      '',
      'Source: `strategies/directional/chain_flow_buy.py` (`ChainFlowBuyStrategy`).',
      'Every rule below is read from the code.',
      '',
      '## Idea',
      '',
      'Buy an index option only when price and the option chain agree on the',
      'direction.',
    ].join('\n')
    expect(firstParagraph(md)).toBe('Buy an index option only when price and the option chain agree on the direction.')
  })

  it('takes the first paragraph as it is when it is not a provenance line', () => {
    expect(firstParagraph('# AI\n\nThe desk’s models live on one page.\n\nMore.')).toBe('The desk’s models live on one page.')
  })

  it('never takes the inside of a code block', () => {
    expect(firstParagraph('# T\n\n```bash\nrm -rf build\n```\n\nThe real sentence.')).toBe('The real sentence.')
  })

  it('keeps an identifier’s underscores and a bracketed name, and drops emphasis marks', () => {
    expect(firstParagraph('# T\n\nSell a put `otm_offset_steps` strikes below, **once**, in *one* order (`<StrategyName>.md`).')).toBe(
      'Sell a put otm_offset_steps strikes below, once, in one order (<StrategyName>.md).',
    )
  })

  it('reads a list that follows its lead-in as one sentence, without the bullets', () => {
    expect(firstParagraph('# T\n\nWhat moves the market, recorded daily:\n- the news;\n- the filings.')).toBe('What moves the market, recorded daily: the news; the filings.')
  })

  it('drops a sentence that only introduces a list, and skips a table of contents', () => {
    expect(firstParagraph('# AI\n\nThe desk’s hosted language models live on one console page. The page shows:\n\n- agents;\n- calls.')).toBe(
      'The desk’s hosted language models live on one console page.',
    )
    expect(firstParagraph('# Report\n\n1. Summary\n2. Drivers\n\nThe report itself starts here.')).toBe('The report itself starts here.')
  })

  it('leads a strategy’s description with its name, inside the same length', () => {
    const d = firstParagraph(`# X\n\n${'word '.repeat(80).trim()}`, 'TrendRider: ')
    expect(d.startsWith('TrendRider: word')).toBe(true)
    expect(d.length).toBeLessThanOrEqual(158)
  })

  it('gives the first sentence whole for a list of pages', () => {
    expect(firstSentence('# X\n\nSell two straddles about 3.5 % away as wings. Then hold them.')).toBe('Sell two straddles about 3.5 % away as wings.')
  })

  it('keeps a description short enough for a search snippet', () => {
    const long = 'word '.repeat(80).trim()
    const d = firstParagraph(`# T\n\n${long}`)
    expect(d.length).toBeLessThanOrEqual(158)
    expect(d.endsWith('…')).toBe(true)
  })
})
