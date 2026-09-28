/**
 * Edge cues for sideways-scrolling rows and tables. A `.tablewrap`, an
 * `.oc-tabs` strip or anything marked `.scroll-x` gets a `data-overflow`
 * attribute saying where the hidden part is (right, left, both), and
 * styles.css fades that edge, so a row that is cut off says so instead of
 * looking complete. Installed once for the signed-in console; a
 * MutationObserver picks up rows as pages mount them, and a ResizeObserver
 * on each row and its content keeps the state right as columns come and go.
 */

const SELECTOR = '.tablewrap, .oc-tabs, .scroll-x'

export type OverflowState = '' | 'right' | 'left' | 'both'

/** Which edge hides content, from a scroll container's three widths. */
export function overflowState(scrollWidth: number, clientWidth: number, scrollLeft: number): OverflowState {
  const hidden = scrollWidth - clientWidth
  if (hidden <= 1) return ''
  if (scrollLeft <= 1) return 'right'
  if (scrollLeft >= hidden - 1) return 'left'
  return 'both'
}

export function installScrollCues(root: ParentNode & Node = document): () => void {
  if (typeof ResizeObserver === 'undefined' || typeof MutationObserver === 'undefined') return () => {}
  const watched = new Map<Element, () => void>()

  const update = (el: Element) => {
    const state = overflowState(el.scrollWidth, el.clientWidth, el.scrollLeft)
    if (!state) el.removeAttribute('data-overflow')
    else if (el.getAttribute('data-overflow') !== state) el.setAttribute('data-overflow', state)
  }
  const resizes = new ResizeObserver((entries) => {
    for (const e of entries) {
      const el = e.target
      // The content's size changed: the row it scrolls in is what needs re-reading.
      if (watched.has(el)) update(el)
      else if (el.parentElement && watched.has(el.parentElement)) update(el.parentElement)
    }
  })

  const watch = (el: Element) => {
    if (watched.has(el)) return
    const onScroll = () => update(el)
    el.addEventListener('scroll', onScroll, { passive: true })
    resizes.observe(el)
    if (el.firstElementChild) resizes.observe(el.firstElementChild)
    watched.set(el, () => {
      el.removeEventListener('scroll', onScroll)
      resizes.unobserve(el)
      if (el.firstElementChild) resizes.unobserve(el.firstElementChild)
      el.removeAttribute('data-overflow')
    })
    update(el)
  }
  const forget = (el: Element) => {
    watched.get(el)?.()
    watched.delete(el)
  }
  const scan = (node: Node) => {
    if (!(node instanceof Element)) return
    if (node.matches(SELECTOR)) watch(node)
    node.querySelectorAll(SELECTOR).forEach(watch)
  }

  const mutations = new MutationObserver((records) => {
    for (const r of records) {
      r.addedNodes.forEach(scan)
      r.removedNodes.forEach((n) => {
        if (!(n instanceof Element)) return
        if (n.matches(SELECTOR)) forget(n)
        n.querySelectorAll(SELECTOR).forEach(forget)
      })
    }
  })
  mutations.observe(root, { childList: true, subtree: true })
  scan(root instanceof Element ? root : (root as Document).body)

  return () => {
    mutations.disconnect()
    resizes.disconnect()
    watched.forEach((off) => off())
    watched.clear()
  }
}
