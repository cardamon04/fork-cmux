'use strict';
// Inert Node only: exact production renderer, xterm and provider; no native UI.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
let assets;
let Terminal, WebLinksAddon;
async function exercise(name, target, row, columns = 80) {
  let provider;
  const messages = [];
  const node = {addEventListener() {}, clientWidth: 0, clientHeight: 0, title: '', removeAttribute() {this.title = '';}};
  function Renderer(options) {
    const term = new Terminal(options);
    term.open = () => {};
    Object.defineProperty(term, 'textarea', {value: node});
    Object.defineProperty(term, 'element', {value: node});
    term.registerLinkProvider = value => {provider = value; return {dispose() {}};};
    return term;
  }
  const scope = vm.createContext({URL, Terminal: Renderer, WebLinksAddon,
    FitAddon: require(path.join(assets, 'addon-fit.js')),
    Unicode11Addon: require(path.join(assets, 'addon-unicode11.js')),
    document: {getElementById() {return node;}}, ResizeObserver: class {observe() {}},
    setTimeout, clearTimeout,
    createTerminalSearch: () => ({paste() {}, configure() {}, selection() {return null;}}),
    window: {chrome: {webview: {postMessage(m) {messages.push(JSON.parse(JSON.stringify(m)));}, addEventListener() {}}}},
  });
  vm.runInContext(fs.readFileSync(path.join(assets, 'links.js'), 'utf8'), scope);
  vm.runInContext(fs.readFileSync(path.join(assets, 'terminal.js'), 'utf8'), scope);
  const term = vm.runInContext('term', scope);
  term.resize(columns, 8);
  await new Promise(resolve => term.write(target + '\r\n', resolve));
  let candidates;
  provider.provideLinks(row, value => {candidates = value || [];});
  for (const candidate of candidates) candidate.activate({isTrusted: true, button: 0,
    ctrlKey: true, altKey: false, shiftKey: false, metaKey: false, preventDefault() {}}, candidate.text);
  const requests = messages.filter(m => m.kind === 'openLink');
  const result = {name, targetLength: target.length, row,
    candidates: candidates.map(c => ({length: c.text.length, host: new URL(c.text).hostname, range: c.range})),
    requests: requests.map(m => ({length: m.data.length, host: new URL(m.data).hostname, exact: m.data === new URL(target).href})),
    integrity: requests.every(m => m.data === new URL(target).href)};
  term.dispose();
  return result;
}

async function run(directory) {
  assets = path.resolve(directory);
  ({Terminal} = require(path.join(assets, 'xterm.js')));
  WebLinksAddon = require(path.join(assets, 'addon-web-links.js'));
  const results = [];
  results.push(await exercise('complete-2128-control', 'https://example.invalid/' + 'a'.repeat(2100) + '/END', 1));
  assert.equal(results[0].requests.length, 1, 'A complete 2128-character wrapped URL remains available');
  for (const columns of [24, 40, 80, 120]) {
    const edge = (1 + Math.ceil(2048 / columns)) * columns - 1;
    for (const punctuation of ['?', ',', ':', '!', '~', ']', '.']) {
      const source = 'https://example.invalid/' + 'a'.repeat(4000) + '/END';
      const target = source.slice(0, edge) + punctuation + source.slice(edge + 1);
      results.push(await exercise('punctuation-cut-' + columns + '-' + punctuation, target, 1, columns));
    }
  }
  const oversized = ('https://example.invalid/' + 'a'.repeat(33000) + '/END').split('');
  oversized[2159] = '?';
  results.push(await exercise('oversized-punctuation-cut', oversized.join(''), 1));
  const nested = 'https://example.invalid/' + 'a'.repeat(3000) + 'https://other.invalid/end';
  for (const row of [38, 39]) results.push(await exercise('nested-authority-' + row, nested, row));
  const long = 'https://example.invalid/' + 'a'.repeat(4000) + '/END';
  for (const row of [1, 6, 11, 16, 21, 26, 31, 36, 41, 46, 51]) results.push(await exercise('both-scan-edges-' + row, long, row));
  results.push(await exercise('unicode-wrapped-control', 'https://example.invalid/' + '日本語😀'.repeat(40), 1));
  console.log('TERMINAL_LINK_INTEGRITY ' + JSON.stringify({cases:results.length, results}));
  for (const result of results) {
    assert.equal(result.integrity, true, 'A bounded provider must never forward a different URL: ' + result.name);
    if (result.targetLength > 32768) assert.equal(result.requests.length, 0, 'Oversized source must not become a smaller request');
  }
  assert.equal(results.at(-1).requests.length, 1, 'Complete Unicode wrapped URL remains available');
  return results.length + 3;
}
module.exports = run;
if (require.main === module) run(process.argv[2]).then(checks => console.log(JSON.stringify({status:'passed', checks})))
  .catch(error => {console.error(error); process.exitCode=1;});
