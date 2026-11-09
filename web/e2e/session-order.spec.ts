import { expect, test } from '@playwright/test'

async function login(page: import('@playwright/test').Page, name = 'browser-admin') {
  await page.goto('/trade/ticket')
  await page.getByLabel('Username or email').fill(name)
  await page.getByLabel('Password', { exact: true }).fill('browser-test-password')
  const ready = page.waitForResponse(r => r.url().endsWith('/api/UserAuth/me') && r.status() === 200)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await ready
  await page.waitForURL(url => !url.pathname.startsWith('/login'))
  await page.goto('/trade/ticket')
}

test('lost response and reload recover one protected manual order', async ({ page, request }) => {
  await login(page)
  await expect(page.getByRole('heading', { name: 'Manual order', exact: true })).toBeVisible()
  await page.locator('#manual-symbol').fill('NSE:TEST-EQ')
  await page.locator('#manual-qty').fill('2')
  await page.locator('#manual-sl').fill('90')
  await page.locator('#manual-target').fill('120')
  page.on('dialog', dialog => dialog.accept())
  let key: string | undefined
  await page.route('**/api/ManualOrders/keyed', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    key = route.request().postDataJSON().clientOrderId
    const response = await route.fetch()
    expect(response.status()).toBe(200)
    await route.abort('failed') // Server committed; browser never received its outcome.
    await page.unroute('**/api/ManualOrders/keyed')
  })
  await page.getByRole('button', { name: /^Buy/ }).click()
  await expect(page.getByRole('button', { name: 'Recover order outcome' })).toBeEnabled()
  await page.reload()
  await expect(page.getByRole('button', { name: 'Recover order outcome' })).toBeVisible()
  const response = page.waitForResponse(r => r.url().endsWith('/api/ManualOrders/keyed') && r.request().method() === 'POST')
  await page.getByRole('button', { name: 'Recover order outcome' }).click()
  const recovered = await response
  expect(recovered.status()).toBe(200)
  expect(recovered.request().postDataJSON().clientOrderId).toBe(key)
  await expect(page.getByRole('button', { name: 'Recover order outcome' })).toHaveCount(0)
  expect(await (await request.get('http://127.0.0.1:5025/test/orders')).json()).toEqual({
    orders: 1, receipts: 1, positions: [{ stopLossPrice: 90, targetPrice: 120 }],
  })
})

test('a fresh account cannot see the previous admin ticket or private receipt', async ({ page }) => {
  await login(page, 'browser-viewer')
  await expect(page.getByRole('heading', { name: 'Manual order', exact: true })).toHaveCount(0)
  await expect(page.getByText('Recover order outcome', { exact: true })).toHaveCount(0)
  // Server policies remain authoritative when a user bypasses client-side routing.
  const status = await page.evaluate(async () => {
    const { api } = await import('/src/lib/api.ts')
    try { await api.get('/api/ManualOrders/book'); return 200 }
    catch (error) { return (error as { status: number }).status }
  })
  expect(status).toBe(403)
})

test('sign out revokes the session and updates another open tab', async ({ page, context, request }) => {
  await login(page)
  const oldToken = await page.evaluate(async () => (await import('/src/lib/api.ts')).tokenStore.access)
  const sibling = await context.newPage()
  await sibling.goto('/trade/ticket')
  await expect(sibling.getByRole('heading', { name: 'Manual order', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Account: browser-admin', exact: true }).click()
  const revoked = page.waitForResponse(r => r.url().endsWith('/api/UserAuth/logout') && r.status() === 200)
  await page.getByRole('button', { name: 'Sign out', exact: true }).click()
  await revoked
  await expect(page.getByLabel('Username or email')).toBeVisible()
  await expect(sibling.getByLabel('Username or email')).toBeVisible()
  const result = await request.get('http://127.0.0.1:5025/api/ManualOrders/book', { headers: { Authorization: `Bearer ${oldToken}` } })
  expect(result.status()).toBe(401)
})
