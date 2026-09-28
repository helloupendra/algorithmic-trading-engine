/**
 * One mapping from the console's tokens to lightweight-charts options, read
 * at the moment a chart is created (the wrappers rebuild on a theme change).
 * The library's own defaults are dark-tuned: its crosshair labels sit on a
 * near-black pill whatever the theme, which is what this replaces.
 */

import { ColorType } from 'lightweight-charts'

/** A token's value as the stylesheet resolves it now ("" when it is missing). */
export function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

/**
 * The chart's chrome: transparent ground (the panel behind shows), secondary
 * text for the axes, the soft hairline for the grid, the line for the scale
 * borders, and a crosshair whose label pill is a surface the text can read
 * on (the library picks black or white ink against it).
 */
export function chartOptions(container: HTMLElement) {
  const line = cssVar('--line')
  const strong = cssVar('--line-strong')
  return {
    width: container.clientWidth,
    height: container.clientHeight,
    layout: {
      background: { type: ColorType.Solid, color: 'transparent' },
      textColor: cssVar('--text-2'),
      fontSize: 11,
      attributionLogo: false,
    },
    grid: {
      vertLines: { color: cssVar('--line-soft') },
      horzLines: { color: cssVar('--line-soft') },
    },
    rightPriceScale: { borderColor: line },
    timeScale: { borderColor: line, timeVisible: true, secondsVisible: false },
    crosshair: {
      mode: 0,
      vertLine: { color: strong, labelBackgroundColor: cssVar('--surface3') },
      horzLine: { color: strong, labelBackgroundColor: cssVar('--surface3') },
    },
  }
}
