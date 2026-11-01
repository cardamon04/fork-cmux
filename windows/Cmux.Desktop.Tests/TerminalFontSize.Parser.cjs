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
  let listener;
  const messages = [];
  const element = {addEventListener() {}, clientWidth: 0, clientHeight: 0};
  function Terminal(options) {
    const term = new Xterm.Terminal(options);
    term.open = () => {};
    Object.defineProperty(term, 'textarea', {value: element});
    Object.defineProperty(term, 'element', {value: element});
    return term;
  }
  const context = vm.createContext({Terminal,
    FitAddon: require(path.join(assets, 'addon-fit.js')),
    Unicode11Addon: require(path.join(assets, 'addon-unicode11.js')),
    document: {getElementById() {return element;}},
    window: {chrome: {webview: {postMessage(m) {messages.push(m);}, addEventListener(_, handler) {listener = handler;}}}},
    ResizeObserver: class {observe() {}}, setTimeout, clearTimeout,
    createTerminalSearch: () => ({paste() {return false;}, configure() {}, open() {}, selection() {return '';}}),
  });
  vm.runInContext(fs.readFileSync(path.join(assets, 'terminal.js'), 'utf8'), context);
  const term = vm.runInContext('term', context);
  const result = {term, messages, send: action => listener({data: {kind: 'fontSize', action}}),
    write: text => new Promise(resolve => term.write(text, resolve)),
    text: () => vm.runInContext('window.cmux.text()', context)};
  fixtures.push(result);
  return result;
}
(async () => {
  const first = fixture(), second = fixture();
  check(first.term.options.fontSize, 14, 'Initial renderer font size');
  await first.write('FONT_LITERAL 日本語 🍀\r\n');
  const before = first.text();
  const inputBefore = first.messages.filter(m => m.kind === 'input').length;
  first.send('increase');
  check(first.term.options.fontSize, 15, 'The user must be able to enlarge the selected terminal font');
  check(first.text(), before, 'Font changes preserve actual xterm buffer contents');
  check(second.term.options.fontSize, 14, 'Other panes retain their font size');
  first.send('increase'); first.send('decrease');
  check(first.term.options.fontSize, 15, 'Increment and decrement share the same option');
  first.send('reset');
  check(first.term.options.fontSize, 14, 'Reset restores the initial renderer font size');
  for (let i = 0; i < 100; i++) first.send('decrease');
  check(first.term.options.fontSize, 6, 'Small font size is bounded');
  for (let i = 0; i < 100; i++) first.send('increase');
  check(first.term.options.fontSize, 72, 'Large font size is bounded');
  for (const action of ['unknown', 1, null, {}, '__proto__']) first.send(action);
  check(first.term.options.fontSize, 72, 'Malformed actions do not change font size');
  check(first.messages.filter(m => m.kind === 'input').length, inputBefore, 'Font actions never write shell input');
  first.send('reset'); second.send('decrease');
  check(first.term.options.fontSize, 14, 'Reset retains per-renderer isolation');
  check(second.term.options.fontSize, 13, 'A second renderer can change independently');
  check(first.text(), before, 'Bounded changes preserve Unicode buffer content');
  console.log(JSON.stringify({status: 'passed', checks, scope: 'Production terminal.js and pinned xterm options/buffer with inert DOM; geometry, focus and ConPTY resize require hosted GUI.'}));
})().finally(() => {for (const f of fixtures) f.term.dispose();}).catch(error => {console.error(error); process.exitCode = 1;});
