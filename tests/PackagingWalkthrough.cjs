// Packaging-only walkthrough against the real production image and actual simulator events.
const {chromium}=require(process.env.ONVIF_PLAYWRIGHT_MODULE);
const assert=require('node:assert/strict');
(async()=>{
  const browser=await chromium.launch({channel:process.env.ONVIF_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage(); const errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.goto(process.env.ONVIF_BROWSER_ORIGIN);
    await page.getByLabel('Username',{exact:true}).fill('synthetic-admin');
    await page.getByLabel('Password',{exact:true}).fill('synthetic-admin-password');
    await page.getByRole('button',{name:'Sign in',exact:true}).click();
    await page.getByRole('button',{name:'Add camera',exact:true}).waitFor();
    const compiledVersion = await page.evaluate(async () => (await (await fetch('/api/session')).json()).version);
    await page.locator('.app-version').waitFor();
    assert.equal(await page.locator('.app-version').textContent(), 'v' + compiledVersion);
    await page.setViewportSize({width:375,height:812});
    assert(await page.locator('.app-version').isVisible());
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
    await page.setViewportSize({width:1280,height:900});
    await page.getByRole('button',{name:'Event Explorer',exact:true}).click();
    await page.getByText('Synthetic/Deployment',{exact:true}).first().waitFor();
    await page.getByRole('button',{name:'Inspect notification / create rule',exact:true}).first().click();
    assert((await page.locator('#details').innerText()).includes('rawMessageXml'));
    await page.getByRole('button',{name:'Draft topic-only rule',exact:true}).click();
    assert.equal(await page.getByLabel('Exact matching topic',{exact:true}).inputValue(),'Synthetic/Deployment');
    assert.equal(await page.getByLabel('Enabled',{exact:true}).isEnabled(),false);
    await page.getByRole('button',{name:'Cancel',exact:true}).click();
    await page.getByRole('button',{name:'Rules',exact:true}).click();
    await page.getByRole('button',{name:'Edit rule',exact:true}).first().click();
    assert.equal(await page.getByRole('textbox',{name:'Action URL',exact:true}).inputValue(),'••••••••');
    await page.getByRole('button',{name:'Cancel',exact:true}).click();
    await page.getByRole('button',{name:'Log out',exact:true}).click();
    await page.getByRole('button',{name:'Sign in',exact:true}).waitFor();
    assert.deepEqual(errors,[]);
    console.log('PASS: production-image browser login, packaged assets, actual events/inspection, disabled observed draft, masked editor and logout.');
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
