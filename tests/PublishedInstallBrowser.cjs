// Test-only login against the actual Compose service; no camera/network actions.
const assert = require('node:assert/strict');
const { chromium } = require(process.argv[2]);
(async () => {
  const browser = await chromium.launch({ channel: process.env.ONVIF_BROWSER_CHANNEL || 'msedge', headless: true });
  try {
    const page = await browser.newPage();
    await page.goto(process.argv[3]);
    await page.locator('[name="username"]').fill('synthetic-admin');
    await page.locator('[name="password"]').fill('  synthetic-admin-password  ');
    await page.locator('#login button').click();
    await page.locator('#workspace').waitFor({ state: 'visible' });
    await page.getByRole('button', { name: 'Cameras', exact: true }).click();
    await page.getByText('Synthetic persistence check', { exact: true }).waitFor();
    assert.equal(await page.locator('#login').isVisible(), false);
    console.log('PASS: published-image browser login and persisted synthetic camera.');
  } finally {
    await browser.close();
  }
})().catch(() => { console.error('Published-image browser smoke failed.'); process.exitCode = 1; });
