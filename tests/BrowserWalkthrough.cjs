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
  let page;
  try {
    page = await browser.newPage(); page.setDefaultTimeout(20000);
    const errors=[]; page.on('pageerror',e=>errors.push(e.message)); page.on('dialog',d=>d.accept());
    let polls=0; page.on('request',r=>{if(r.url().includes('/api/events')) polls++;});
    if (process.env.ONVIF_BROWSER_SOURCE_ASSETS === '1') {
      // Development-only asset seam; the default walkthrough verifies embedded native assets.
      for (const file of ['app.js','app.css']) await page.route('**/'+file,route=>route.fulfill({path:require('node:path').resolve('src/OnvifWebhookBridge/wwwroot',file),contentType:file.endsWith('.js')?'text/javascript':'text/css'}));
      await page.route(base+'/',route=>route.fulfill({path:require('node:path').resolve('src/OnvifWebhookBridge/wwwroot/index.html'),contentType:'text/html'}));
    }
    await page.goto(base);
    async function login() {
      const username=page.getByLabel('Username',{exact:true}), password=page.getByLabel('Password',{exact:true});
      assert.equal(await username.getAttribute('autocomplete'),'username');
      assert.equal(await password.getAttribute('autocomplete'),'current-password');
      assert.equal(await username.getAttribute('id'),'admin-username'); assert.equal(await password.getAttribute('id'),'admin-password');
      assert.equal(await page.locator('#login').getAttribute('method'),'post');
      const navigation=page.waitForEvent('framenavigated',{predicate:frame=>frame===page.mainFrame()});
      await username.fill('synthetic-admin'); await password.fill('synthetic-admin-password');
      await page.getByRole('button',{name:'Sign in',exact:true}).click(); await navigation;
      await page.getByRole('button',{name:'Add camera',exact:true}).waitFor();
      await page.getByRole('heading',{name:'Overview',exact:true}).waitFor();
      await page.getByText('Cameras subscribed',{exact:true}).waitFor();
      await page.getByRole('button',{name:'Cameras',exact:true}).click();
      assert.equal(await page.locator('#admin-password').inputValue(),'');
      assert.equal(new URL(page.url()).search,'');
    }
    async function api(path, method='GET', body) { return page.evaluate(async ({path,method,body})=>{ const session=await (await fetch('/api/session')).json(); const r=await fetch(path,{method,headers:{'Content-Type':'application/json','X-CSRF-Token':session.csrfToken},body:body===undefined?undefined:JSON.stringify(body)}); return {status:r.status,text:await r.text()}; },{path,method,body}); }
    async function config() { const r=await api('/api/configuration'); assert.equal(r.status,200); return JSON.parse(r.text); }
    async function save() {
      await page.getByRole('button',{name:'Save',exact:true}).click(); await page.locator('#editor').waitFor({state:'hidden'});
      const committed=await config();
      await waitFor(async()=>await page.locator('#screen').getAttribute('data-revision')===String(committed.revision),'committed revision displayed after save');
    }
    async function verifyFieldHelp() {
      const rows = page.locator('#fields .field-row:visible');
      for (const row of await rows.all()) {
        const control = row.locator('input, select, textarea');
        const icon = row.getByRole('button',{name:/^Help for /}); const tip = row.getByRole('tooltip',{includeHidden:true});
        assert(await control.getAttribute('aria-describedby'));
        assert((await tip.textContent()).length > 40);
        await icon.hover(); await tip.waitFor({state:'visible'});
        const bounds = await tip.boundingBox(), viewport = page.viewportSize();
        assert(bounds.x >= 0 && bounds.y >= 0 && bounds.x + bounds.width <= viewport.width && bounds.y + bounds.height <= viewport.height);
        await icon.focus(); await page.mouse.move(0,0); await tip.waitFor({state:'visible'});
        await icon.press('Escape'); await tip.waitFor({state:'hidden'});
        assert(await page.locator('#editor').isVisible());
        await control.focus();
      }
      const firstIcon = rows.first().getByRole('button',{name:/^Help for /});
      await firstIcon.focus();
      await page.screenshot({path:'.local/field-help.png',fullPage:true});
      await firstIcon.press('Escape');
      await firstIcon.click(); assert.equal(await firstIcon.getAttribute('aria-expanded'),'true');
      await rows.first().locator('input,select,textarea').focus();
      await rows.first().getByRole('tooltip').waitFor({state:'visible'});
      await firstIcon.press('Escape');
    }
    async function operation(label,op,value) { const group=page.getByRole('group',{name:new RegExp('^'+label+' ·')}); await group.getByRole('button',{name:op===0?'Undo':op===1?'Edit':'Clear',exact:true}).click(); if(op===1) {
      if(label==='HTTP headers') { const headers=JSON.parse(value); for(let i=0;i<headers.length;i++){ if(i) await group.getByRole('button',{name:'Add header',exact:true}).click(); await group.getByLabel('Header name',{exact:true}).nth(i).fill(headers[i].name); await group.getByLabel('Header value',{exact:true}).nth(i).fill(headers[i].value); } }
      else await group.getByLabel('Replacement value',{exact:true}).fill(value);
    } }
    async function screen(name) { await page.getByRole('button',{name:name==='Events'?'Event Explorer':name,exact:true}).click(); }
    async function verifyActionSpacing() {
      const problems = await page.evaluate(() => {
        const problems = [];
        for (const group of document.querySelectorAll('.actions, .editor-actions')) {
          if (!group.checkVisibility()) continue;
          const buttons = [...group.children].filter(e=>e.tagName==='BUTTON' && e.checkVisibility()).map(e=>e.getBoundingClientRect());
          for (let i=0;i<buttons.length;i++) for(let j=i+1;j<buttons.length;j++) {
            const a=buttons[i], b=buttons[j];
            const horizontal=Math.max(a.left-b.right,b.left-a.right);
            const vertical=Math.max(a.top-b.bottom,b.top-a.bottom);
            if(horizontal<7 && vertical<7) problems.push('Buttons touch or have insufficient separation');
          }
          const bounds=group.getBoundingClientRect();
          if(buttons.some(b=>b.left<bounds.left-1 || b.right>bounds.right+1)) problems.push('Button extends outside its group');
        }
        return problems;
      });
      assert.deepEqual(problems,[]);
    }
    async function trigger(value, selectedTopic=topic) { const r=await fetch(control+'/api/cameras/synthetic-camera/events',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({topic:selectedTopic,state:value,duration:0})}); assert(r.ok); }
    // A rejected sign-in stays on the form; only authenticated success navigates.
    let rejectedNavigations=0;
    const rejectedNavigation=frame=>{if(frame===page.mainFrame()) rejectedNavigations++;};
    page.on('framenavigated',rejectedNavigation);
    await page.getByLabel('Username',{exact:true}).fill('synthetic-admin');
    await page.getByLabel('Password',{exact:true}).fill('synthetic-incorrect-password');
    await page.getByRole('button',{name:'Sign in',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Sign in failed or is temporarily limited.'}).waitFor();
    assert.equal(rejectedNavigations,0); assert(await page.locator('#login').isVisible());
    page.off('framenavigated',rejectedNavigation);
    await login();
    for (const width of [320,390,768,1280]) {
      await page.setViewportSize({width,height:900});
      for (const name of ['Overview','Cameras','Rules','Events']) {
        await screen(name); await verifyActionSpacing();
        assert(await page.locator('#screen > .screen-actions').count()===1);
        assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
      }
      await screen('Overview');
      if(width===390 || width===1280) await page.screenshot({path:`.local/spacing-overview-${width}.png`,fullPage:true});
    }
    await screen('Cameras');
    await page.getByRole('button',{name:'Add camera',exact:true}).click();
    const cameraUsername=page.locator('#camera-username'), cameraPassword=page.locator('#camera-password');
    assert.equal(await page.locator('#edit-form').getAttribute('method'),'post');
    assert.equal(await cameraUsername.isDisabled(),true); assert.equal(await cameraPassword.isDisabled(),true);
    assert.equal(await cameraUsername.getAttribute('name'),'camera-username'); assert.equal(await cameraUsername.getAttribute('type'),'text');
    assert.equal(await cameraPassword.getAttribute('name'),'camera-password'); assert.equal(await cameraPassword.getAttribute('type'),'password');
    const cameraAutocomplete=await cameraUsername.getAttribute('autocomplete');
    assert.match(cameraAutocomplete,/^section-camera-\S+ username$/);
    assert.equal(await cameraPassword.getAttribute('autocomplete'),cameraAutocomplete.replace(/ username$/,' current-password'));
    await verifyFieldHelp();
    await page.getByLabel('Name',{exact:true}).fill(cameraName); await page.getByLabel('Device-service URL',{exact:true}).fill(endpoint); await page.getByLabel('Enabled',{exact:true}).check();
    await operation('Camera username',1,'synthetic-user'); await operation('Camera password',1,'synthetic-password'); await verifyFieldHelp(); await save();
    await page.getByText(/Subscribed · last contact/).waitFor();
    assert.equal(await page.locator('#screen svg').count(),0); assert.equal(await page.evaluate(()=>window.injected),undefined);
    // A poll must also leave the text node in place while the pointer is held to start a selection.
    const cameraHeading = await page.locator('#screen h3').elementHandle();
    const headingBounds = await cameraHeading.boundingBox();
    await page.mouse.move(headingBounds.x+5,headingBounds.y+headingBounds.height/2);
    await page.mouse.down();
    const dragPolls=polls;
    await waitFor(()=>polls>=dragPolls+2,'polling during pointer selection');
    assert(await cameraHeading.evaluate(heading=>heading.isConnected));
    await page.mouse.up();
    await page.evaluate(()=>getSelection().removeAllRanges());
    await page.getByRole('button',{name:'Test connectivity',exact:true}).click(); await page.getByRole('status').filter({hasText:'Reachable: true'}).waitFor();
    // Preserve credentials on an ordinary edit; stale revision cannot silently overwrite.
    await page.getByRole('button',{name:'Edit camera',exact:true}).click();
    assert.equal(await page.locator('#camera-username').inputValue(),''); assert.equal(await page.locator('#camera-password').inputValue(),'');
    assert.equal(await page.locator('#camera-username').isDisabled(),true); assert.equal(await page.locator('#camera-password').isDisabled(),true);
    const initial=await config(); const c=initial.cameras[0];
    assert.equal(await page.locator('#camera-username').getAttribute('autocomplete'),`section-camera-${encodeURIComponent(c.id)} username`);
    assert.equal(await page.locator('#camera-password').getAttribute('autocomplete'),`section-camera-${encodeURIComponent(c.id)} current-password`);
    assert(c.hasUsername && c.hasPassword);
    assert.equal((await api(`/api/cameras/${c.id}/enabled`,'POST',{revision:initial.revision,enabled:true})).status,200);
    await page.getByRole('button',{name:'Save',exact:true}).click(); await page.getByRole('alert').filter({hasText:'Configuration changed'}).waitFor();
    await page.getByRole('button',{name:'Cancel',exact:true}).click(); await page.getByRole('button',{name:'Refresh now',exact:true}).click();
    const refreshedConfig=await config();
    await waitFor(async()=>await page.locator('#screen').getAttribute('data-revision')===String(refreshedConfig.revision),'conflicting revision refreshed before reopening');
    await page.getByRole('button',{name:'Edit camera',exact:true}).click(); await save();
    // A selected, stale screen must not silently edit newer configuration with its newer revision.
    const displayedConfig=await config();
    const selectedHeading=await page.locator('#screen h3').evaluate(heading=>{
      const range=document.createRange(); range.selectNodeContents(heading);
      const selection=getSelection(); selection.removeAllRanges(); selection.addRange(range);
      return selection.toString();
    });
    assert.equal((await api(`/api/cameras/${c.id}/enabled`,'POST',{revision:displayedConfig.revision,enabled:true})).status,200);
    const staleScreenPolls=polls;
    await waitFor(()=>polls>=staleScreenPolls+2,'new configuration fetched while display is selected');
    assert.equal(await page.evaluate(()=>getSelection().toString()),selectedHeading);
    assert.equal(await page.locator('#screen').getAttribute('data-revision'),String(displayedConfig.revision));
    await page.getByRole('button',{name:'Edit camera',exact:true}).click();
    await page.getByRole('button',{name:'Save',exact:true}).click();
    await page.getByRole('alert').filter({hasText:'Configuration changed'}).waitFor();
    await page.getByRole('button',{name:'Cancel',exact:true}).click();
    await page.getByRole('button',{name:'Refresh now',exact:true}).click();
    await trigger(false); await trigger(true); await trigger(false,'Synthetic/Unmatched');
    await screen('Events'); await page.getByText(topic,{exact:true}).first().waitFor();
    await waitFor(async()=>{const r=await api('/api/events');return r.text.includes('false')&&r.text.includes('true')&&r.text.includes('Synthetic/Unmatched');},'both states and unmatched');
    await page.getByRole('button',{name:'Refresh now',exact:true}).click();
    await page.getByText('Synthetic/Unmatched',{exact:true}).waitFor();
    const search=page.getByLabel('Search retained notifications',{exact:true});
    await search.fill('Synthetic/Unmatched'); assert((await page.locator('#screen article').count())>=1); for(const card of await page.locator('#screen article').all()) assert((await card.innerText()).includes('Synthetic/Unmatched'));
    const searchPolls=polls; await waitFor(()=>polls>=searchPolls+2,'search stays focused during polling');
    assert.equal(await search.inputValue(),'Synthetic/Unmatched');
    await search.fill('false');
    assert((await page.locator('#screen article').count())>=1);
    for(const card of await page.locator('#screen article').all()) assert((await card.innerText()).includes('= false'));
    await search.fill('');
    const event=page.locator('article').filter({has:page.getByText(topic,{exact:true})}).first(); await event.getByRole('button').click();
    assert((await page.locator('#details').innerText()).includes('rawMessageXml')); assert.equal(await page.locator('#details img').count(),0);
    await page.getByRole('button',{name:/Draft from data: State =/}).click();
    await verifyFieldHelp();
    assert.equal(await page.getByLabel('Exact matching topic',{exact:true}).inputValue(),topic);
    assert.equal(await page.getByLabel('Field condition',{exact:true}).inputValue(),'1'); assert.equal(await page.getByLabel('Field name',{exact:true}).inputValue(),'State');
    assert.equal(await page.getByLabel('Enabled',{exact:true}).isEnabled(),false);
    assert.equal(await page.getByLabel('Ignore certificate validation',{exact:true}).isChecked(),false);
    await page.getByLabel('Ignore certificate validation',{exact:true}).check();
    await page.getByRole('button',{name:'Sensitive URL',exact:true}).click();
    await page.getByLabel('Rule name',{exact:true}).fill('Synthetic draft'); const recorder=(await api('/fixture/recorder')).text;
    await operation('Action URL',1,recorder); await page.getByLabel('HTTP method',{exact:true}).selectOption('POST');
    const actionUrlGroup=page.getByRole('group',{name:/^Action URL ·/});
    const actionUrlInput=actionUrlGroup.getByLabel('Replacement value',{exact:true});
    assert.equal(await actionUrlInput.getAttribute('type'),'text');
    await actionUrlGroup.getByRole('button',{name:'Hide entered URL',exact:true}).click();
    assert.equal(await actionUrlInput.getAttribute('type'),'password'); assert.equal(await actionUrlInput.inputValue(),recorder);
    await actionUrlGroup.getByRole('button',{name:'Show entered URL',exact:true}).press('Space');
    assert.equal(await actionUrlInput.getAttribute('type'),'text');
    // The reference retains the complete real event through edits, toggles and polls.
    const reference=page.locator('#event-reference');
    await reference.getByRole('button',{name:'Raw JSON',exact:true}).click();
    assert((await reference.locator('pre:visible').innerText()).includes('rawMessageXml'));
    await reference.getByRole('button',{name:'Fields',exact:true}).click();
    await reference.getByRole('button',{name:/^Use data #/}).first().click();
    const editorPolls=polls; const inputHandle=await page.getByLabel('Rule name',{exact:true}).elementHandle();
    await waitFor(()=>polls>=editorPolls+2,'polling continues in editor');
    assert(await inputHandle.evaluate(e=>e.isConnected)); assert.equal(await page.getByLabel('Rule name',{exact:true}).inputValue(),'Synthetic draft');
    await reference.getByRole('button',{name:'Hide event',exact:true}).click();
    await reference.getByRole('button',{name:'View event',exact:true}).click();
    await page.setViewportSize({width:390,height:844});
    await page.locator('#editor').evaluate(e=>e.scrollTop=0);
    await page.screenshot({path:'.local/redesign-mobile-editor.png'});
    await verifyActionSpacing();
    assert(await page.evaluate(()=>document.querySelector('#editor').scrollWidth<=innerWidth));
    await page.getByRole('button',{name:'Save',exact:true}).scrollIntoViewIfNeeded();
    await page.setViewportSize({width:1280,height:900});
    await page.locator('#editor').evaluate(e=>e.scrollTop=0);
    await page.screenshot({path:'.local/redesign-desktop-editor.png'});
    await verifyActionSpacing();
    await page.getByText('Advanced matching & delivery',{exact:true}).evaluate(e=>e.parentElement.open=true);
    await operation('HTTP headers',1,'[{"name":"X-Synthetic","value":"synthetic-browser-token"},{"name":"X-Empty-Synthetic","value":""}]');
    await operation('JSON body',1,'{invalid-synthetic-json'); await page.getByRole('button',{name:'Save',exact:true}).click();
    await page.getByRole('alert').filter({hasText:'The POST body must be valid literal JSON.'}).waitFor();
    await operation('JSON body',1,'{"synthetic":true}'); await verifyFieldHelp();
    await page.setViewportSize({width:390,height:844}); await save(); await page.setViewportSize({width:1280,height:900});
    let cfg=await config(); assert.equal(cfg.rules[0].ignoreCertificateValidation,true); assert.equal(cfg.rules[0].match.enabled,false); assert.equal(cfg.rules[0].urlSensitive,true); assert.equal(cfg.rules[0].publicUrl,null); assert(cfg.rules[0].hasHeaders && cfg.rules[0].hasJsonBody);
    await screen('Rules'); await page.getByRole('button',{name:'Send manual HTTP test…',exact:true}).click(); await page.getByText(/Last completed: Accepted.*HTTP 200/).waitFor();
    await page.getByText('Execution details',{exact:true}).click();
    await page.getByText('HTTP status: 200',{exact:true}).waitFor();
    const responsePreview = page.locator('#screen details pre');
    assert((await responsePreview.innerText()).includes('syntheticResponse'));
    assert.equal(await responsePreview.locator('img').count(),0);
    await page.getByRole('button',{name:'Refresh now',exact:true}).click();
    await responsePreview.waitFor({state:'visible'});
    assert.equal(await page.evaluate(()=>window.injected),undefined);
    // Keep selected response text stable while real polling continues; show the latest result when cleared.
    let selectedRefreshes = 0;
    let releaseSelectedResponse;
    const selectionReady = new Promise(resolve=>{releaseSelectedResponse=resolve;});
    await page.route('**/api/executions',async route=>{
      await selectionReady;
      const response=await route.fetch(); const executions=await response.json();
      for(const execution of executions) execution.responseJson='{"syntheticUpdate":true}';
      await route.fulfill({json:executions}); selectedRefreshes++;
    });
    const selectedResponse = await responsePreview.evaluate(pre=>{
      const range=document.createRange(); range.selectNodeContents(pre);
      const selection=getSelection(); selection.removeAllRanges(); selection.addRange(range);
      return selection.toString();
    });
    releaseSelectedResponse();
    const selectionPolls=polls;
    await waitFor(()=>selectedRefreshes>=2 && polls>=selectionPolls+2,'polling continues during selection');
    assert(polls>=selectionPolls+2);
    assert.equal(await page.evaluate(()=>getSelection().toString()),selectedResponse);
    assert((await responsePreview.innerText()).includes('syntheticResponse'));
    await page.evaluate(()=>getSelection().removeAllRanges());
    await responsePreview.filter({hasText:'syntheticUpdate'}).waitFor();
    await page.unroute('**/api/executions');
    // Browser rendering seam only: transport failures have no fabricated HTTP response.
    await page.route('**/api/executions',async route=>{
      const response=await route.fetch(); const executions=await response.json();
      for(const execution of executions) Object.assign(execution,{result:4,statusCode:null,responseJson:null,responseBodyStatus:null,transportError:'ConnectionError'});
      await route.fulfill({json:executions});
    });
    await page.getByRole('button',{name:'Refresh now',exact:true}).click();
    await page.getByText('HTTP status: No HTTP response received',{exact:true}).waitFor();
    await page.getByText('Transport error category: ConnectionError',{exact:true}).waitFor();
    assert.equal(await page.locator('#screen details pre').count(),0);
    await page.unroute('**/api/executions');
    await page.getByRole('button',{name:'Edit rule',exact:true}).click();
    assert.equal(await actionUrlInput.inputValue(),''); assert.equal(await actionUrlInput.isDisabled(),true);
    assert.equal(await actionUrlInput.getAttribute('type'),'text');
    assert.equal(await actionUrlGroup.getByRole('button',{name:'Hide entered URL',exact:true}).isVisible(),false);
    await page.getByRole('button',{name:'Sensitive URL',exact:true}).click();
    await page.getByRole('button',{name:'Save',exact:true}).click();
    await page.getByRole('alert').filter({hasText:'Re-enter the URL'}).waitFor();
    await page.getByRole('button',{name:'Sensitive URL',exact:true}).click();
    await operation('HTTP headers',2); await operation('HTTP headers',0);
    assert.equal((await config()).rules[0].hasHeaders,true);
    assert.equal(await page.getByLabel('Ignore certificate validation',{exact:true}).isChecked(),true); await page.getByLabel('Ignore certificate validation',{exact:true}).uncheck(); await operation('HTTP headers',2); await operation('JSON body',2); await save(); cfg=await config(); assert.equal(cfg.rules[0].ignoreCertificateValidation,false); assert(!cfg.rules[0].hasHeaders&&!cfg.rules[0].hasJsonBody&&cfg.rules[0].hasUrl);
    await page.getByRole('button',{name:'Enable rule',exact:true}).click();
    await page.getByRole('button',{name:'Disable rule',exact:true}).waitFor();
    // Second independently matching rule via the actual UI.
    await page.getByRole('button',{name:'Add rule',exact:true}).click(); await page.getByLabel('Rule name',{exact:true}).fill('Synthetic second'); await page.getByLabel('Exact matching topic',{exact:true}).fill(topic); await operation('Action URL',1,recorder); await save();
    cfg=await config(); const ordinary=cfg.rules.find(r=>r.name==='Synthetic second');
    assert.equal(ordinary.urlSensitive,false); assert.equal(ordinary.publicUrl,recorder);
    const ordinaryCard=page.locator('#screen article').filter({has:page.getByRole('heading',{name:'Synthetic second',exact:true})});
    await ordinaryCard.getByRole('button',{name:'Edit rule',exact:true}).click();
    assert.equal(await actionUrlInput.inputValue(),recorder);
    assert.equal(await page.getByRole('button',{name:'Sensitive URL',exact:true}).getAttribute('aria-pressed'),'false');
    const cancelledUrl=await actionUrlInput.elementHandle();
    await page.getByRole('button',{name:'Cancel',exact:true}).click();
    await waitFor(()=>cancelledUrl.evaluate(e=>e.value===''),'cancel wipes detached replacement values');
    await page.getByRole('button',{name:'Enable rule',exact:true}).click();
    await waitFor(async()=>await page.getByRole('button',{name:'Disable rule',exact:true}).count()===2,'both rules enabled in refreshed screen');
    await page.screenshot({path:'.local/phase7-rules.png',fullPage:true});
    await verifyActionSpacing();
    const before=Number((await api('/fixture/count')).text); await trigger(true); await waitFor(async()=>Number((await api('/fixture/count')).text)>=before+2,'multiple matching deliveries');
    await screen('Cameras'); await page.getByRole('button',{name:'Delete camera',exact:true}).click(); await page.getByText("Remove this camera's rules before deleting it.",{exact:true}).waitFor();
    // Explicit credential clear: configured connection becomes disconnected, UI remains responsive.
    await page.getByRole('button',{name:'Edit camera',exact:true}).click(); await operation('Camera username',2); await operation('Camera password',2); await save();
    cfg=await config(); assert(!cfg.cameras[0].hasUsername&&!cfg.cameras[0].hasPassword);
    await page.getByText(/(Reconnecting|Error).*last contact/).waitFor();
    await page.getByRole('button',{name:'Edit camera',exact:true}).click(); await operation('Camera username',1,'synthetic-user'); await operation('Camera password',1,'synthetic-password'); await verifyFieldHelp(); await save(); await page.getByText(/Subscribed · last contact/).waitFor();
    // Session expiry on the server clears editors and cancels polling on the next response.
    await page.locator('#screen h3').first().evaluate(heading=>{
      const range=document.createRange(); range.selectNodeContents(heading);
      const selection=getSelection(); selection.removeAllRanges(); selection.addRange(range);
    });
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
    await page.getByRole('button',{name:'Close',exact:true}).click();
    rendering.truncated=false;
    await page.setViewportSize({width:390,height:844});
    await page.getByRole('button',{name:'Refresh now',exact:true}).click();
    await page.getByText(/Initialized · TRUNCATED/).waitFor({state:'hidden'});
    await page.getByRole('button',{name:'Inspect notification / create rule',exact:true}).click();
    await page.getByRole('button',{name:'Draft from source: Duplicate = false',exact:true}).click();
    const mobileReference=page.locator('#event-reference');
    await mobileReference.getByRole('button',{name:'View event',exact:true}).click();
    await mobileReference.getByRole('button',{name:'Use source #2: Duplicate = true',exact:true}).click();
    assert.equal(await page.getByLabel('Field condition',{exact:true}).inputValue(),'0');
    assert.equal(await page.getByLabel('Expected value (exact text)',{exact:true}).inputValue(),'true');
    await mobileReference.getByRole('button',{name:'Raw JSON',exact:true}).click();
    assert((await mobileReference.locator('pre:visible').innerText()).includes('ElementItem'));
    await mobileReference.getByRole('button',{name:'Hide event',exact:true}).click();
    assert.equal(await page.getByLabel('Expected value (exact text)',{exact:true}).inputValue(),'true');
    await page.getByRole('button',{name:'Save',exact:true}).scrollIntoViewIfNeeded();
    await page.getByRole('button',{name:'Cancel',exact:true}).click();
    await page.setViewportSize({width:1280,height:900}); await page.unroute('**/api/events');
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
  } catch(error) { if(page) await page.screenshot({path:'.local/redesign-browser-failure.png',fullPage:true}).catch(()=>{}); throw error; } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exitCode=1;});
