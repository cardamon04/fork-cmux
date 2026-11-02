'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assets = path.resolve(process.argv[2] || path.join(__dirname, 'Assets'));
const {Terminal} = require(path.join(assets, 'xterm.js'));
const {WebLinksAddon} = require(path.join(assets, 'addon-web-links.js'));
let checks = 0;
function check(actual, expected, reason) { assert.deepEqual(actual, expected, reason); checks++; }
const messages = [];
const element = {title: '', removeAttribute(name) {this[name] = '';}};
const context = vm.createContext({URL});
vm.runInContext(fs.readFileSync(path.join(assets, 'links.js'), 'utf8'), context);
const links = context.createTerminalLinks({element}, message => messages.push(JSON.parse(JSON.stringify(message))));
links.configure({OpenLinkHint: 'LOCALIZED_HINT'});
function event(extra = {}) {return {isTrusted: true, button: 0, ctrlKey: true, altKey: false, shiftKey: false, metaKey: false, preventDefault() {this.prevented = true;}, ...extra};}
const valid = ['https://example.invalid/path?q=1#part', 'http://127.0.0.1:8123/', 'HTTPS://example.invalid/日本語', 'https://[::1]:8080/'];
for (const url of valid) {
  const before = messages.length, click = event();
  links.handler.activate(click, url);
  check(messages.length, before + 1, 'A valid explicit HTTP(S) activation is forwarded');
  check(messages.at(-1).data, new URL(url).href, 'The requested target is canonicalized');
  check(click.prevented, true, 'Accepted links consume the default navigation');
}
const invalid = ['', 'about:blank', 'file:///C:/test', 'javascript:alert(1)', 'data:text/html,test', 'cmux://test',
  'example.invalid', '//example.invalid', 'https://user:password@example.invalid/', 'https://user@example.invalid/',
  'https://example.invalid/\nnext', '\u0085https://example.invalid/', ' https://example.invalid/', 'https://example.invalid/ ',
  'https://example.invalid/' + 'x'.repeat(32768)];
for (const url of invalid) {
  const before = messages.length;
  links.handler.activate(event(), url);
  links.handler.hover(event(), url);
  check(messages.length, before, 'Unsafe links do not publish an open request');
  check(element.title, '', 'Unsafe link hover does not advertise an actionable link');
}
for (const extra of [{isTrusted: false}, {button: 1}, {button: 2}, {ctrlKey: false}, {altKey: true}, {shiftKey: true}, {metaKey: true}]) {
  const before = messages.length;
  links.handler.activate(event(extra), valid[0]);
  check(messages.length, before, 'Only trusted left Ctrl+click activates a link');
}
links.handler.hover(event(), valid[0]);
check(element.title, 'LOCALIZED_HINT', 'Valid link hover is localized');
links.handler.leave();
check(element.title, '', 'Leaving a link clears the tooltip');
check(links.handler.allowNonHttpProtocols, false, 'OSC 8 cannot enable non-HTTP protocols');
(async () => {
  const term = new Terminal({cols: 24, rows: 8, allowProposedApi: true});
  let provider;
  term.registerLinkProvider = value => {provider = value; return {dispose() {}};};
  const addon = new WebLinksAddon(links.handler.activate, {hover: links.handler.hover, leave: links.handler.leave});
  term.loadAddon(addon);
  await new Promise(resolve => term.write('prefix https://example.invalid/a/long/path\r\n', resolve));
  let found;
  provider.provideLinks(1, value => {found = value;});
  check(found.length, 1, 'The actual pinned addon discovers a wrapped plain URL');
  check(found[0].text, 'https://example.invalid/a/long/path', 'The wrapped URL retains its complete target');
  check(found[0].range.end.y > found[0].range.start.y, true, 'The fixture actually spans multiple terminal rows');
  found[0].hover(event(), found[0].text);
  check(element.title, 'LOCALIZED_HINT', 'The actual addon delegates to the shared hover policy');
  const before = messages.length;
  found[0].activate(event(), found[0].text);
  check(messages.length, before + 1, 'The actual addon delegates to the shared activation policy');
  found[0].leave();
  check(element.title, '', 'The addon uses the shared leave callback');
  term.dispose();

  // Exercise the actual production renderer wiring, including its provider.
  // A bounded addon window must never turn a target into a different URL.
  for (const count of [2100, 4000, 33000]) {
    const target = 'https://example.invalid/' + 'a'.repeat(count) + '/END';
    const published = [];
    let actualProvider;
    const node = {addEventListener() {}, clientWidth: 0, clientHeight: 0, title: '', removeAttribute() {this.title = '';}};
    function Renderer(options) {
      const renderer = new Terminal(options);
      renderer.open = () => {};
      Object.defineProperty(renderer, 'textarea', {value: node});
      Object.defineProperty(renderer, 'element', {value: node});
      renderer.registerLinkProvider = value => {actualProvider = value; return {dispose() {}};};
      return renderer;
    }
    const scope = vm.createContext({URL, Terminal: Renderer, WebLinksAddon: {WebLinksAddon},
      FitAddon: require(path.join(assets, 'addon-fit.js')), Unicode11Addon: require(path.join(assets, 'addon-unicode11.js')),
      document: {getElementById() {return node;}}, ResizeObserver: class {observe() {}}, setTimeout, clearTimeout,
      createTerminalSearch: () => ({paste() {}, configure() {}, selection() {return null;}}),
      window: {chrome: {webview: {postMessage(m) {published.push(JSON.parse(JSON.stringify(m)));}, addEventListener() {}}}},
    });
    vm.runInContext(fs.readFileSync(path.join(assets, 'links.js'), 'utf8'), scope);
    vm.runInContext(fs.readFileSync(path.join(assets, 'terminal.js'), 'utf8'), scope);
    const renderer = vm.runInContext('term', scope);
    renderer.resize(80, 8);
    await new Promise(resolve => renderer.write(target + '\r\n', resolve));
    let candidates;
    actualProvider.provideLinks(1, value => {candidates = value || [];});
    for (const candidate of candidates) candidate.activate(event(), candidate.text);
    const requests = published.filter(m => m.kind === 'openLink');
    console.log('TERMINAL_LINK_WINDOW ' + JSON.stringify({originalLength: target.length,
      candidates: candidates.map(candidate => ({length:candidate.text.length,range:candidate.range})),
      forwardedLengths: requests.map(request => request.data.length)}));
    check(requests.every(request => request.data === target), true, 'A plain URL must never open a truncated addon window');
    check(target.length <= 32768 || requests.length === 0, true, 'A truncated oversized URL must not bypass the target bound');
    if (count === 2100) check(requests.length, 1, 'A complete wrapped URL remains available');
    renderer.dispose();
  }
  console.log(JSON.stringify({status: 'passed', checks}));
})().catch(error => {console.error(error); process.exitCode = 1;});
