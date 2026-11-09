import { expect, test } from '@playwright/test'
test('math, Mermaid and Excalidraw render with the patched KaTeX dependency', async ({ page }) => {
  await page.goto('/e2e/renderers.html')
  const result = page.getByTestId('renderer-result')
  await expect(result).toBeVisible()
  const output = JSON.parse(await result.innerText())
  expect(output.error).toBeUndefined()
  expect(output.math).toBe(true)
  expect(output.unsafeLink).toBe(false)
  expect(output.diagram).toBe(1)
  expect(output.drawing).toBe('svg')
  expect(output.elements).toBeGreaterThanOrEqual(3)
})
