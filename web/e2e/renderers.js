import katex from 'katex'
import mermaid from 'mermaid'
import { parseMermaidToExcalidraw } from '@excalidraw/mermaid-to-excalidraw'
import { convertToExcalidrawElements, exportToSvg } from '@excalidraw/excalidraw'

export async function render() {
  const math = document.createElement('div')
  katex.render('E=mc^2', math, { trust: false })
  document.body.append(math)
  const untrusted = katex.renderToString('\\href{javascript:alert(1)}{unsafe}', { trust: false, throwOnError: false })
  mermaid.initialize({ startOnLoad: false, securityLevel: 'strict' })
  const diagram = await mermaid.render('math-smoke', 'flowchart LR\n A["$$x^2$$"] --> B["Order"]')
  const host = document.createElement('div')
  host.innerHTML = diagram.svg
  document.body.append(host)
  const parsed = await parseMermaidToExcalidraw('flowchart LR\n A[Signal] --> B[Order]')
  const elements = convertToExcalidrawElements(parsed.elements)
  const svg = await exportToSvg({ elements, files: parsed.files ?? {}, appState: { exportBackground: false } })
  document.body.append(svg)
  return { math: !!math.querySelector('.katex'), unsafeLink: /href="javascript:/.test(untrusted),
    diagram: host.querySelectorAll('svg').length, drawing: svg.tagName, elements: elements.length }
}
