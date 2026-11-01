'use strict';
const defaultFontSize = 14;
const term = new Terminal({
  fontFamily: 'Cascadia Mono, Consolas, monospace', fontSize: defaultFontSize,
  cursorBlink: true, scrollback: 5000, allowProposedApi: true,
  theme: { background: '#181a1f', foreground: '#dce1eb' },
});
class BoundedFitAddon extends FitAddon.FitAddon {
  proposeDimensions() {
    const size = super.proposeDimensions();
    // Match the native transport limit before changing the renderer grid.
    return size && {cols: Math.min(1000, size.cols), rows: Math.min(1000, size.rows)};
  }
}
const fit = new BoundedFitAddon();
term.loadAddon(fit);
term.loadAddon(new Unicode11Addon.Unicode11Addon());
term.unicode.activeVersion = '11';
term.open(document.getElementById('terminal'));
function send(message) { window.chrome.webview.postMessage(message); }
let win32Input = false;
let titleLimit = 0, pendingTitle = '', publishedTitle = '', titleTimer = null;
function validTitle(data) {
  if (data.length === 0) return true;
  // Match the native display-title policy before replacing a pending value.
  if (/[\u0000-\u001f\u007f-\u009f]/.test(data) || /^\p{White_Space}+$/u.test(data)) return false;
  for (let i = 0; i < data.length; i++) {
    const unit = data.charCodeAt(i);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = data.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
    } else if (unit >= 0xdc00 && unit <= 0xdfff) return false;
  }
  return true;
}
function queueTitle(data) {
  if (titleLimit <= 0 || data.length > titleLimit || !validTitle(data)) return;
  pendingTitle = data;
  if (titleTimer !== null) return;
  // Use a fixed cadence so continuous spinner output cannot starve delivery.
  titleTimer = setTimeout(() => {
    titleTimer = null;
    if (publishedTitle === pendingTitle) return;
    publishedTitle = pendingTitle;
    send({kind:'title', data:pendingTitle});
  }, 100);
}
term.onTitleChange(queueTitle);
let bellTimer = null;
term.onBell(() => {
  if (bellTimer !== null) return;
  // Deliver the first BEL immediately. A bounded leading-edge cooldown avoids
  // floods without publishing a stale trailing BEL after focus changes.
  send({kind:'bell'});
  bellTimer = setTimeout(() => { bellTimer = null; }, 100);
});
// ConPTY requests native key records with DECSET 9001. xterm still handles
// other modes in the same sequence; non-ASCII text can need lossless records.
for (const [final, enabled] of [['h', true], ['l', false]]) {
  term.parser.registerCsiHandler({prefix:'?', final}, params => {
    if (params.includes(9001)) win32Input = enabled;
    return false;
  });
}
term.parser.registerEscHandler({final:'c'}, () => { win32Input = false; queueTitle(''); return false; });
const find = createTerminalSearch(term);
let cwdLimit = 0;
// xterm owns OSC framing across output chunks. The native side independently
// validates the URI and local filesystem boundary before accepting metadata.
term.parser.registerOscHandler(7, data => {
  if (cwdLimit > 0 && data.length <= cwdLimit) send({kind:'cwd', data});
  return true;
});
function resize() { const host=document.getElementById('terminal'); if (host.clientWidth > 30 && host.clientHeight > 30) fit.fit(); }
function fontSize(action) {
  const current = term.options.fontSize;
  let next;
  if (action === 'increase') next = Math.min(72, current + 1);
  else if (action === 'decrease') next = Math.max(6, current - 1);
  else if (action === 'reset') next = defaultFontSize;
  else return;
  if (next === current) return;
  term.options.fontSize = next;
  resize(); // The existing onResize path updates the selected pane's ConPTY.
}
term.onData(data => send({kind:'input', data, win32Input}));
term.onResize(size => send({kind:'resize', ...size}));
term.textarea.addEventListener('focus', () => send({kind:'focus'}));
term.element.addEventListener('pointerdown', event => { if (event.isTrusted) send({kind:'activate'}); });
document.getElementById('find-bar').addEventListener('pointerdown', event => { if (event.isTrusted) send({kind:'activate'}); });
window.chrome.webview.addEventListener('message', event => {
  const m = event.data;
  if (m.kind === 'output') term.write(m.data, () => send({kind:'ack', id:m.id}));
  else if (m.kind === 'paste') { if (!find.paste(m.data)) term.paste(m.data); }
  else if (m.kind === 'configure') {
    cwdLimit = m.cwdLimit;
    titleLimit = Number.isSafeInteger(m.titleLimit) && m.titleLimit > 0 ? m.titleLimit : 0;
    // ConPTY repaints after row growth instead of restoring old scrollback.
    if (Number.isSafeInteger(m.windowsBuild) && m.windowsBuild > 0)
      term.options.windowsPty = {backend: 'conpty', buildNumber: m.windowsBuild};
    find.configure(m.strings, m.locale);
  }
  else if (m.kind === 'find') find.open();
  else if (m.kind === 'fontSize') fontSize(m.action);
});
new ResizeObserver(resize).observe(document.getElementById('terminal'));
resize();
send({kind:'ready', cols:term.cols, rows:term.rows});
// Read-only renderer inspection also supports application-level integration tests.
window.cmux = Object.freeze({
  selection: () => term.getSelection(),
  copySelection: () => find.selection() || term.getSelection(),
  text: () => Array.from({length:term.buffer.active.length}, (_,i) => term.buffer.active.getLine(i).translateToString(true)).join('\n'),
  size: () => ({cols:term.cols, rows:term.rows}),
  focus: () => term.focus(),
  foreground: text => {
    for (let i = 0; i < term.buffer.active.length; i++) {
      const line = term.buffer.active.getLine(i);
      const index = line.translateToString(true).indexOf(text);
      if (index >= 0) return line.getCell(index).getFgColor();
    }
    return null;
  },
});
