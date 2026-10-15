// Standalone browser verification; uses an externally supplied Playwright installation.
// No frontend dependencies, package manifest, bundler or build step.
const { chromium } = require(process.env.ONVIF_PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const base = process.env.ONVIF_BROWSER_ORIGIN;
const control = process.env.ONVIF_BROWSER_CONTROL;
const endpoint = process.env.ONVIF_BROWSER_CAMERA;
const container = process.env.ONVIF_BROWSER_CONTAINER;
const topic = 'Synthetic/Browser/<img src=x onerror="window.injected=true">';
const cameraName = 'Synthetic <svg onload="window.injected=true">';
async function waitFor(fn, label) { const end = Date.now()+25000; while (Date.now()<end) { if (await fn()) return; await new Promise(r=>setTimeout(r,150)); } throw Error(label); }
(async () => {
  const scenario = { formatVersion:1, config:{bindAddress:[0,0,0,0].join('.'),controlApiAddress:[0,0,0,0].join('.'),discoveryEnabled:false}, cameras:[{id:'synthetic-camera',displayName:'Synthetic browser source',persona:'generic',users:[{username:'synthetic-user',password:'synthetic-password',level:'Administrator'}],network:{bindAddress:[0,0,0,0].join('.'),httpPort:8000,advertisedHost:new URL(endpoint).hostname,advertisedHttpPort:Number(new URL(endpoint).port),rtspPort:8554}}] };
  // Any bind addresses below are test listeners, never real camera configuration.
  assert((await fetch(control+'/api/scenario',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(scenario)})).ok);
  const browser = await chromium.launch({channel:process.env.ONVIF_BROWSER_CHANNEL || 'msedge',headless:true});
  try {
    const page = await browser.newPage(); page.setDefaultTimeout(20000);
    const errors=[]; page.on('pageerror',e=>errors.push(e.message)); page.on('dialog',d=>d.accept());
    let polls=0; page.on('request',r=>{if(r.url().includes('/api/events')) polls++;});
    await page.goto(base);
    async function login() { await page.getByLabel('Username',{exact:true}).fill('synthetic-admin'); await page.getByLabel('Password',{exact:true}).fill('synthetic-admin-password'); await page.getByRole('button',{name:'Sign in',exact:true}).click(); await page.getByRole('button',{name:'Add camera',exact:true}).waitFor(); }
    async function api(path, method='GET', body) { return page.evaluate(async ({path,method,body})=>{ const session=await (await fetch('/api/session')).json(); const r=await fetch(path,{method,headers:{'Content-Type':'application/json','X-CSRF-Token':session.csrfToken},body:body===undefined?undefined:JSON.stringify(body)}); return {status:r.status,text:await r.text()}; },{path,method,body}); }
    async function config() { const r=await api('/api/configuration'); assert.equal(r.status,200); return JSON.parse(r.text); }
    async function save() { await page.getByRole('button',{name:'Save',exact:true}).click(); await page.locator('#editor').waitFor({state:'hidden'}); }
    async function operation(label,op,value) { const group=page.getByRole('group',{name:new RegExp('^'+label+' ·')}); await group.locator('select').selectOption(String(op)); if(op===1) await group.getByLabel('Replacement value').fill(value); }
    async function screen(name) { await page.getByRole('button',{name,exact:true}).click(); }
    async function trigger(value, selectedTopic=topic) { const r=await fetch(control+'/api/cameras/synthetic-camera/events',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({topic:selectedTopic,state:value,duration:0})}); assert(r.ok); }
    await login();
    await page.getByRole('button',{name:'Add camera',exact:true}).click();
    await page.getByLabel('Name',{exact:true}).fill(cameraName); await page.getByLabel('Device-service URL').fill(endpoint); await page.getByLabel('Enabled',{exact:true}).check();
    await operation('Camera username',1,'synthetic-user'); await operation('Camera password',1,'synthetic-password'); await save();
    await page.getByText(/Subscribed · last contact/).waitFor();
    assert.equal(await page.locator('#screen svg').count(),0); assert.equal(await page.evaluate(()=>window.injected),undefined);
    await page.getByRole('button',{name:'Test connectivity',exact:true}).click(); await page.getByRole('status').filter({hasText:'Reachable: true'}).waitFor();
    // Preserve credentials on an ordinary edit; stale revision cannot silently overwrite.
    await page.getByRole('button',{name:'Edit camera',exact:true}).click();
    const initial=await config(); const c=initial.cameras[0];
    assert(c.hasUsername && c.hasPassword);
    assert.equal((await api(`/api/cameras/${c.id}/enabled`,'POST',{revision:initial.revision,enabled:true})).status,200);
    await page.getByRole('button',{name:'Save',exact:true}).click(); await page.getByRole('alert').filter({hasText:'Configuration changed'}).waitFor();
    await page.getByRole('button',{name:'Cancel',exact:true}).click(); await page.getByRole('button',{name:'Refresh now',exact:true}).click();
    await page.getByRole('button',{name:'Edit camera',exact:true}).click(); await save();
    await trigger(false); await trigger(true); await trigger(false,'Synthetic/Unmatched');
    await screen('Events'); await page.getByText(topic,{exact:true}).first().waitFor();
    await waitFor(async()=>{const r=await api('/api/events');return r.text.includes('false')&&r.text.includes('true')&&r.text.includes('Synthetic/Unmatched');},'both states and unmatched');
    const event=page.locator('article').filter({has:page.getByText(topic,{exact:true})}).first(); await event.getByRole('button').click();
    assert((await page.locator('#details').innerText()).includes('rawMessageXml')); assert.equal(await page.locator('#details img').count(),0);
    await page.getByRole('button',{name:/Draft from data: State =/}).click();
    assert.equal(await page.getByLabel('Exact matching topic').inputValue(),topic);
    assert.equal(await page.getByLabel('Field condition').inputValue(),'1'); assert.equal(await page.getByLabel('Field name',{exact:true}).inputValue(),'State');
    assert.equal(await page.getByLabel('Enabled',{exact:true}).isEnabled(),false);
    await page.getByLabel('Rule name').fill('Synthetic draft'); const recorder=(await api('/fixture/recorder')).text;
    await operation('Action URL',1,recorder); await page.getByLabel('HTTP method').selectOption('POST');
    await operation('HTTP headers',1,'[{"name":"X-Synthetic","value":"synthetic-browser-token"}]'); await operation('JSON body',1,'{"synthetic":true}'); await save();
    let cfg=await config(); assert.equal(cfg.rules[0].match.enabled,false); assert(cfg.rules[0].hasHeaders && cfg.rules[0].hasJsonBody);
    await screen('Rules'); await page.getByRole('button',{name:'Send manual HTTP test…',exact:true}).click(); await page.getByText(/Last completed: Accepted.*HTTP 204/).waitFor();
    await page.getByRole('button',{name:'Edit rule',exact:true}).click(); await operation('HTTP headers',2); await operation('JSON body',2); await save(); cfg=await config(); assert(!cfg.rules[0].hasHeaders&&!cfg.rules[0].hasJsonBody&&cfg.rules[0].hasUrl);
    await page.getByRole('button',{name:'Enable rule',exact:true}).click();
    await page.getByRole('button',{name:'Disable rule',exact:true}).waitFor();
    // Second independently matching rule via the actual UI.
    await page.getByRole('button',{name:'Add rule',exact:true}).click(); await page.getByLabel('Rule name').fill('Synthetic second'); await page.getByLabel('Exact matching topic').fill(topic); await operation('Action URL',1,recorder); await save();
    await page.getByRole('button',{name:'Enable rule',exact:true}).click();
    await waitFor(async()=>await page.getByRole('button',{name:'Disable rule',exact:true}).count()===2,'both rules enabled in refreshed screen');
    await page.screenshot({path:'.local/phase7-rules.png',fullPage:true});
    const before=Number((await api('/fixture/count')).text); await trigger(true); await waitFor(async()=>Number((await api('/fixture/count')).text)>=before+2,'multiple matching deliveries');
    await screen('Cameras'); await page.getByRole('button',{name:'Delete camera',exact:true}).click(); await page.getByText("Remove this camera's rules before deleting it.",{exact:true}).waitFor();
    // Explicit credential clear: configured connection becomes disconnected, UI remains responsive.
    await page.getByRole('button',{name:'Edit camera',exact:true}).click(); await operation('Camera username',2); await operation('Camera password',2); await save();
    cfg=await config(); assert(!cfg.cameras[0].hasUsername&&!cfg.cameras[0].hasPassword);
    await page.getByText(/(Reconnecting|Error).*last contact/).waitFor();
    await page.getByRole('button',{name:'Edit camera',exact:true}).click(); await operation('Camera username',1,'synthetic-user'); await operation('Camera password',1,'synthetic-password'); await save(); await page.getByText(/Subscribed · last contact/).waitFor();
    // Session expiry on the server clears editors and cancels polling on the next response.
    assert.equal((await api('/fixture/expire','POST')).status,204); await page.getByRole('button',{name:'Sign in',exact:true}).waitFor();
    let stopped=polls; await page.waitForTimeout(3500); assert.equal(polls,stopped);
    await login(); await page.getByRole('button',{name:'Log out',exact:true}).click(); await page.getByRole('button',{name:'Sign in',exact:true}).waitFor(); stopped=polls; await page.waitForTimeout(3500); assert.equal(polls,stopped);
    await login();
    // Restart the disposable native fixture process, preserving its SQLite directory.
    if(container) { execFileSync('docker',['restart',container],{stdio:'pipe'}); await page.getByRole('button',{name:'Sign in',exact:true}).waitFor(); await login(); cfg=await config(); assert.equal(cfg.rules.length,2); assert.equal(cfg.cameras.length,1); await page.getByText(/Subscribed · last contact/).waitFor(); await trigger(true); await waitFor(async()=>Number((await api('/fixture/count')).text)>=2,'restored recorder delivery'); }
    // Separate rendering unit fixture: onvifsim does not emit arbitrary ElementItems.
    // Clone the observed event and replace only its display content; no simulated protocol claim.
    const observed=JSON.parse((await api('/api/events')).text)[0];
    const rendering={...observed,topic:'Synthetic/Rendering',propertyOperation:'Initialized',key:[{kind:'ElementItem',name:'Key',value:null,xml:'<script>window.injected=true</script>',identityMetadata:'synthetic'}],source:[{kind:'SimpleItem',name:'Duplicate',value:'false',xml:'<synthetic/>',identityMetadata:''},{kind:'SimpleItem',name:'Duplicate',value:'true',xml:'<synthetic/>',identityMetadata:''}],diagnostics:['Synthetic rendering fixture'],rawMessageXml:'<svg onload="window.injected=true"><extension>retained</extension></svg>',truncated:true};
    await page.route('**/api/events',r=>r.fulfill({json:[rendering]})); await screen('Events'); await page.getByRole('button',{name:'Refresh now',exact:true}).click();
    await page.getByText(/Initialized · TRUNCATED/).waitFor(); await page.getByRole('button',{name:'Inspect notification / create rule',exact:true}).click();
    const details=await page.locator('#details').innerText(); assert(details.includes('ElementItem')&&details.includes('extension')&&details.includes('Duplicate'));
    assert.equal(await page.locator('#details script, #details svg').count(),0); assert.equal(await page.getByRole('button',{name:'Draft topic-only rule',exact:true}).count(),0);
    await page.getByRole('button',{name:'Close',exact:true}).click(); await page.unroute('**/api/events');
    // Client absolute-expiry branch, separate browser unit response seam.
    await page.route('**/api/session',async r=>{const response=await r.fetch();const session=await response.json();session.expires=new Date(Date.now()+1000).toISOString();await r.fulfill({json:session});});
    await page.reload(); await page.getByRole('button',{name:'Sign in',exact:true}).waitFor(); stopped=polls; await page.waitForTimeout(3500); assert.equal(polls,stopped);
    await page.unroute('**/api/session'); await login();
    await screen('Rules'); for(let i=0;i<2;i++){await page.getByRole('button',{name:'Delete rule',exact:true}).first().click(); await waitFor(async()=>(await config()).rules.length===1-i,'rule deletion');}
    await screen('Cameras'); await page.getByRole('button',{name:'Disable camera',exact:true}).click(); await page.getByText(/Disabled · last contact/).waitFor();
    await page.getByRole('button',{name:'Enable camera',exact:true}).click(); await page.getByText(/Subscribed · last contact/).waitFor();
    await page.getByRole('button',{name:'Delete camera',exact:true}).click(); await waitFor(async()=>(await config()).cameras.length===0,'camera deletion');
    assert.equal(await page.evaluate(()=>window.injected),undefined); assert.deepEqual(errors,[]);
    await page.screenshot({path:process.env.ONVIF_BROWSER_SCREENSHOT || '.local/phase7-browser.png',fullPage:true});
    console.log('PASS: browser CRUD, simulator event inspection/drafts, inert HTML/XML, secret preserve/replace/clear, stale revisions, connectivity/disconnection, two rules/local recorder, expiry/logout/polling and restart.');
  } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exitCode=1;});
