// Production HTTPS smoke: validate the synthetic trust anchor, host name and session.
const https = require('node:https');
const fs = require('node:fs');
const assert = require('node:assert/strict');
const origin = process.env.ONVIF_TLS_ORIGIN;
const ca = fs.readFileSync(process.env.ONVIF_TLS_CA);
function request(path, body, cookie, csrf, selectedOrigin = origin) {
  return new Promise((resolve, reject) => {
    const data = body === undefined ? undefined : JSON.stringify(body);
    const headers = data === undefined ? {} : {
      Origin: selectedOrigin, 'Content-Type': 'application/json',
      'Content-Length': Buffer.byteLength(data),
      ...(cookie ? {Cookie: cookie} : {}), ...(csrf ? {'X-CSRF-Token': csrf} : {})
    };
    const r = https.request(origin + path, {method: data === undefined ? 'GET' : 'POST', ca, headers}, res => {
      let text = '';
      res.on('data', d => { text += d; if (text.length > 8192) { res.destroy(new Error('TLS smoke response bound')); } });
      res.on('error', reject);
      res.on('end', () => resolve({status: res.statusCode, headers: res.headers, text}));
    });
    r.setTimeout(30000, () => r.destroy(new Error('TLS smoke client deadline')));
    r.on('error', reject);
    r.end(data);
  });
}
(async () => {
  // HTTP liveness can precede a cold emulated TLS path. Retry only anonymous health
  // transport readiness, never certificate errors or login/mutation assertions.
  const end = Date.now() + 60000;
  let retries = 0;
  while (true) {
    try {
      const health = await request('/health');
      assert.equal(health.status, 200); assert.equal(health.text, 'Healthy');
      break;
    } catch (error) {
      if (!['ECONNRESET', 'ECONNREFUSED', 'ETIMEDOUT'].includes(error.code) || Date.now() >= end) { throw error; }
      retries++;
      await new Promise(resolve => setTimeout(resolve, 500));
    }
  }
  if (retries) { console.log('TLS transport readiness retries: ' + retries); }
  const login = await request('/api/login', {username: 'synthetic-admin', password: 'synthetic-admin-password'});
  assert.equal(login.status, 200);
  const cookie = login.headers['set-cookie'][0];
  assert(/;\s*secure/i.test(cookie)); assert(/httponly/i.test(cookie)); assert(/samesite=strict/i.test(cookie));
  const csrf = JSON.parse(login.text).csrfToken;
  assert.equal((await request('/api/logout', {}, cookie.split(';')[0], csrf, 'https://foreign.invalid')).status, 403);
  assert.equal((await request('/api/logout', {}, cookie.split(';')[0], csrf)).status, 204);
  console.log('PASS: production HTTPS certificate/CA/hostname validation, Secure/HttpOnly/SameSite session, same-origin CSRF logout and foreign-origin rejection.');
})().catch(error => { console.error(error); process.exitCode = 1; });
