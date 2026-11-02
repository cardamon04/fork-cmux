'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assets = path.resolve(process.argv[2] || path.join(__dirname, 'Assets'));
const Xterm = require(path.join(assets, 'xterm.js'));
const fixtures = [];
let checks = 0;
function check(actual, expected, reason) { assert.deepEqual(actual, expected, reason); checks++; }
function fixture() {
  const messages = [], timers = new Map();
  let nextTimer = 0;
  const element = {addEventListener() {}, clientWidth: 0, clientHeight: 0};
  function Terminal(options) {
    const term = new Xterm.Terminal(options);
    term.open = () => {};
    Object.defineProperty(term, 'textarea', {value: element});
    Object.defineProperty(term, 'element', {value: element});
    return term;
  }
  const context = vm.createContext({Terminal,
    WebLinksAddon: require(path.join(assets, 'addon-web-links.js')),
    Unicode11Addon: require(path.join(assets, 'addon-unicode11.js')),
    document: {getElementById() {return element;}},
    window: {chrome: {webview: {postMessage(m) {messages.push(m);}, addEventListener() {}}}},
    ResizeObserver: class {observe() {}},
    setTimeout(callback) {const id = ++nextTimer; timers.set(id, callback); return id;},
    clearTimeout(id) {timers.delete(id);},
    createTerminalSearch: () => ({paste() {return false;}, configure() {}, open() {}, selection() {return null;}}),
  });
  vm.runInContext(fs.readFileSync(path.join(assets, 'addon-fit.js'), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(assets, 'links.js'), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(assets, 'terminal.js'), 'utf8'), context);
  const term = vm.runInContext('term', context);
  const result = {term, messages,
    bells: () => messages.filter(message => message.kind === 'bell').length,
    write: text => new Promise(resolve => term.write(text, resolve)),
    tick() {const callbacks = [...timers.values()]; timers.clear(); callbacks.forEach(callback => callback());},
    timerCount: () => timers.size,
  };
  fixtures.push(result);
  return result;
}
(async () => {
  const first = fixture(), second = fixture();
  await first.write('ordinary output\r\n\x1b]2;OSC title\x07\x1b]7;file:///C:/temp\x07');
  check(first.bells(), 0, 'OSC BEL terminators must not create unread notifications');
  await first.write('\x07');
  check(first.bells(), 1, 'A real terminal BEL must reach the native unread-attention path immediately');
  check(second.bells(), 0, 'BEL arrival belongs only to its renderer');
  await first.write('\x07'.repeat(32768));
  check(first.bells(), 1, 'A BEL flood cannot flood the native message queue');
  check(first.timerCount(), 1, 'The renderer retains one bounded cooldown rather than a timer per BEL');
  first.tick();
  check(first.bells(), 1, 'Cooldown expiry must not invent a delayed BEL after the user switched panes');
  await first.write('\x07');
  check(first.bells(), 2, 'A later BEL can notify again');
  await second.write('\x07');
  check(second.bells(), 1, 'A second renderer has an independent cooldown');
  check(first.messages.some(message => message.kind === 'input'), false, 'Attention never writes shell input');
  console.log(JSON.stringify({status: 'passed', checks, scope: 'Actual pinned xterm parser and production terminal.js; inert DOM, no native GUI.'}));
})().finally(() => {for (const fixture of fixtures) fixture.term.dispose();}).catch(error => {console.error(error); process.exitCode = 1;});
