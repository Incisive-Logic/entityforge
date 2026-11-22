// Node's built-in assertions; no browser, network or package installation required.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../src/Studio/EntityForge.Db.Studio.Browser/wwwroot/studioHost.js', import.meta.url), 'utf8');
const { takeDemoToken } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
const token = 'A'.repeat(64);
let checks = 0;
for (const [hostname, hash, expected, consumed] of [
  ['127.0.0.1', '#demo=' + token, token, true],
  ['localhost', '#demo=' + token.toLowerCase(), token.toLowerCase(), true],
  ['[::1]', '#demo=' + token, token, true],
  ['example.com', '#demo=' + token, null, true],
  ['127.0.0.1.attacker.example', '#demo=' + token, null, true],
  ['127.0.0.1', '#demo=bad', null, true],
  ['127.0.0.1', '#other=value', null, false]
]) {
  globalThis.location = { hostname, hash, pathname: '/studio/', search: '?mode=test' };
  const history = [];
  globalThis.history = { replaceState: (_state, _title, url) => { history.push(url); location.hash = ''; } };
  assert.equal(takeDemoToken(), expected);
  assert.deepEqual(history, consumed ? ['/studio/?mode=test'] : []);
  assert.equal(takeDemoToken(), null, 'Token must be consumed only once');
  checks++;
}
console.log(`${checks} demo-token boundary checks passed.`);
