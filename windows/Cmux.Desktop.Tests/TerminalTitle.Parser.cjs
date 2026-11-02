'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assets = path.resolve(process.argv[2] || path.join(__dirname, 'Assets'));
const Xterm = require(path.join(assets, 'xterm.js'));
const fixtures = [];
let checks = 0;
function check(value, expected, reason) { assert.deepEqual(value, expected, reason); checks++; }
function fixture() {
  const messages = [], observed = [], timers = new Map();
  let listener, timerId = 0;
  const element = {addEventListener() {}, clientWidth: 0, clientHeight: 0};
  function Terminal(options) {
    const term = new Xterm.Terminal(options);
    term.open = () => {};
    Object.defineProperty(term, 'textarea', {value: element});
    Object.defineProperty(term, 'element', {value: element});
    term.onTitleChange(title => observed.push(title));
    return term;
  }
  const context = vm.createContext({Terminal,
    FitAddon: require(path.join(assets, 'addon-fit.js')),
    Unicode11Addon: require(path.join(assets, 'addon-unicode11.js')),
    document: {getElementById() {return element;}},
    window: {chrome: {webview: {postMessage(message) {messages.push(message);}, addEventListener(_, handler) {listener = handler;}}}},
    ResizeObserver: class {observe() {}},
    setTimeout(callback, delay) {check(delay, 100, 'Title publication has a bounded 100ms cadence'); timers.set(++timerId, callback); return timerId;},
    clearTimeout(id) {timers.delete(id);},
    createTerminalSearch: () => ({paste() {return false;}, configure() {}, open() {}, selection() {return null;}}),
  });
  vm.runInContext(fs.readFileSync(path.join(assets, 'terminal.js'), 'utf8'), context, {filename: 'production-terminal.js'});
  const term = vm.runInContext('term', context);
  const result = {term, messages, observed,
    valid: value => vm.runInContext('validTitle(' + JSON.stringify(value) + ')', context),
    titles: () => messages.filter(message => message.kind === 'title').map(message => message.data),
    configure: () => listener({data: {kind: 'configure', titleLimit: 512, cwdLimit: 0, strings: {}, locale: 'en'}}),
    flush: () => {const ready = [...timers.values()]; timers.clear(); for (const run of ready) run();},
    write: text => new Promise(resolve => term.write(text, resolve)),
  };
  fixtures.push(result);
  return result;
}
const title = value => '\x1b]2;' + value + '\x1b\\';
(async () => {
  if (process.argv[3] === '--replay') {
    const capture = JSON.parse(fs.readFileSync(process.argv[4], 'utf8'));
    const replay = fixture(); replay.configure();
    await replay.write(capture.output); replay.flush();
    check(replay.observed.includes(capture.expected), true, 'Actual ConPTY output contains the expected parsed title');
    check(replay.titles().at(-1), capture.expected, 'Actual ConPTY title reaches the native-message boundary');
    console.log(JSON.stringify({status: 'passed', checks, title: replay.titles().at(-1)}));
    return;
  }
  const first = fixture();
  await first.write(title('before configure'));
  first.flush();
  check(first.titles(), [], 'Unconfigured pages do not publish title metadata');
  first.configure();
  const literal = ' 日本語 & 🍀 <tag> "title" ';
  await first.write(title(literal));
  check(first.observed.at(-1), literal, 'The actual pinned parser receives the full title');
  check(first.titles(), [], 'Title does not bypass the coalescing interval');
  first.flush();
  check(first.titles(), [literal], 'A terminal title must reach the native host as literal data');
  await first.write(title(literal)); first.flush();
  check(first.titles(), [literal], 'Repeated titles do not republish');
  await first.write(title('previous valid')); first.flush();
  await first.write(title('new valid') + title('   ')); first.flush();
  check(first.titles().at(-1), 'new valid', 'A rejected blank title cannot discard the latest valid value in a coalesced burst');
  check(first.valid('\ufeff'), true, 'Native-compatible validation does not classify BOM as Unicode whitespace');
  await first.write('\x1b]0;both title and icon\x07'); first.flush();
  check(first.titles().at(-1), 'both title and icon', 'OSC 0 with BEL publishes title');
  const count = first.titles().length;
  await first.write('\x1b]1;icon only\x1b\\'); first.flush();
  check(first.titles().length, count, 'Icon-only OSC 1 is not a window title');
  await first.write('\x1b]2;frag'); first.flush();
  check(first.titles().length, count, 'An incomplete OSC has no published title');
  await first.write('mented\x1b'); first.flush();
  check(first.titles().at(-1), 'fragmented', 'The pinned parser completes OSC on the escape introducing ST');
  const afterEscape = first.titles().length;
  await first.write('\\'); first.flush();
  check(first.titles().length, afterEscape, 'The rest of a split ST does not duplicate the title');
  const beforeEmbedded = first.titles().length;
  await first.write('\x1bP$q]2;not-a-title\x1b\\'); first.flush();
  check(first.titles().length, beforeEmbedded, 'OSC-looking DCS payload is not a title');
  await first.write('\x1b]2;cancelled\x18'); first.flush();
  check(first.titles().length, beforeEmbedded, 'Cancelled OSC is not published');
  const boundary = '🍀'.repeat(256);
  await first.write(title(boundary)); first.flush();
  check(first.titles().at(-1), boundary, '512 UTF-16 units preserve complete supplementary scalars');
  const beforeOversize = first.titles().length;
  await first.write(title('x'.repeat(513))); first.flush();
  check(first.titles().length, beforeOversize, 'Oversized titles do not replace valid metadata');
  await first.write(title('')); first.flush();
  check(first.titles().at(-1), '', 'An empty title clears live metadata');
  const beforeBurst = first.titles().length;
  await first.write(Array.from({length: 1000}, (_, index) => title('⠋ build ' + index)).join(''));
  check(first.titles().length, beforeBurst, 'A burst remains deferred'); first.flush();
  check(first.titles().length, beforeBurst + 1, 'A burst publishes one latest value');
  check(first.titles().at(-1), '⠋ build 999', 'Coalescing preserves the latest literal title');
  const second = fixture(); second.configure();
  await second.write(title('another pane')); second.flush();
  check(second.titles(), ['another pane'], 'A second terminal has its own title');
  check(first.titles().at(-1), '⠋ build 999', 'A second terminal cannot replace the first title');
  await first.write('\x1bc'); first.flush();
  check(first.titles().at(-1), '', 'RIS clears the live title');
  const inputBefore = first.messages.filter(message => message.kind === 'input').length;
  await first.write(title('metadata only')); first.flush();
  check(first.messages.filter(message => message.kind === 'input').length, inputBefore, 'Title metadata never becomes shell input');
  first.term.input('ordinary input');
  check(first.messages.filter(message => message.kind === 'input').at(-1).data, 'ordinary input', 'Ordinary input still uses its existing channel');
  console.log(JSON.stringify({status: 'passed', checks, scope: 'Production terminal.js and real pinned xterm parser; inert DOM only, no GUI/input devices/clipboard.'}));
})().finally(() => {for (const item of fixtures) item.term.dispose();}).catch(error => {console.error(error); process.exitCode = 1;});
