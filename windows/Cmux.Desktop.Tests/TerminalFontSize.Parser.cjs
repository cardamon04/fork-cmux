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
    WebLinksAddon: require(path.join(assets, 'addon-web-links.js')),
    Unicode11Addon: require(path.join(assets, 'addon-unicode11.js')),
    document: {getElementById() {return element;}},
    window: {chrome: {webview: {postMessage(m) {messages.push(m);}, addEventListener(_, handler) {listener = handler;}}}},
    ResizeObserver: class {observe() {}}, setTimeout, clearTimeout,
    createTerminalSearch: () => ({paste() {return false;}, configure() {}, open() {}, selection() {return null;}}),
  });
  vm.runInContext(fs.readFileSync(path.join(assets, 'addon-fit.js'), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(assets, 'links.js'), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(assets, 'terminal.js'), 'utf8'), context);
  const term = vm.runInContext('term', context);
  const result = {term, messages, send: action => listener({data: {kind: 'fontSize', action}}),
    configure: windowsBuild => listener({data: {kind: 'configure', windowsBuild, cwdLimit: 0, titleLimit: 0, strings: {}, locale: 'en'}}),
    write: text => new Promise(resolve => term.write(text, resolve)),
    text: () => vm.runInContext('window.cmux.text()', context),
    fittedGrid(width, height, cellWidth, cellHeight) {
      element.clientWidth = width; element.clientHeight = height;
      const measured = {cols: 80, rows: 24, options: {scrollback: 5000}, element: {parentElement: {}},
        _core: {_renderService: {dimensions: {css: {cell: {width: cellWidth, height: cellHeight}}}, clear() {}}},
        resize(cols, rows) {this.cols = cols; this.rows = rows;}};
      context.window.getComputedStyle = () => ({getPropertyValue(name) {
        return name === 'width' ? String(width) : name === 'height' ? String(height) : '0';
      }});
      vm.runInContext('fit', context).activate(measured);
      vm.runInContext('resize()', context);
      return {cols: measured.cols, rows: measured.rows};
    }};
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
  const wide = fixture();
  check(wide.fittedGrid(4900, 900, 8.4, 17), {cols: 581, rows: 52}, 'An ordinary fitted grid retains the pinned addon calculation');
  check(wide.fittedGrid(4900, 900, 4, 8), {cols: 1000, rows: 112}, 'A small font must not resize the renderer beyond the native column limit');
  check(wide.fittedGrid(9000, 9000, 4, 8), {cols: 1000, rows: 1000}, 'Both fitted axes must respect the existing native size limit');
  for (const build of [26100, 20348]) {
    const conpty = fixture(); conpty.configure(build);
    conpty.term.resize(80, 3);
    await conpty.write(Array.from({length: 10}, (_, i) => 'CONPTY_LINE_' + i + '\r\n').join(''));
    const scrollback = conpty.term.buffer.active.baseY;
    assert.ok(scrollback > 0, 'The ConPTY row-grow discriminator needs scrollback');
    conpty.term.resize(80, 8);
    check(conpty.term.buffer.active.baseY, scrollback, 'ConPTY row growth must not pull retained output back into the viewport: Windows build ' + build);
  }
  console.log(JSON.stringify({status: 'passed', checks, scope: 'Production terminal.js and pinned xterm options/buffer with inert DOM; geometry, focus and ConPTY resize require hosted GUI.'}));
})().finally(() => {for (const f of fixtures) f.term.dispose();}).catch(error => {console.error(error); process.exitCode = 1;});
