import { describe, expect, it } from 'vitest'
import { firstParagraph } from './build.mjs'

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

  it('keeps a description short enough for a search snippet', () => {
    const long = 'word '.repeat(80).trim()
    const d = firstParagraph(`# T\n\n${long}`)
    expect(d.length).toBeLessThanOrEqual(158)
    expect(d.endsWith('…')).toBe(true)
  })
})
