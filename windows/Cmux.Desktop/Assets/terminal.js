'use strict';
const term = new Terminal({
  fontFamily: 'Cascadia Mono, Consolas, monospace', fontSize: 14,
  cursorBlink: true, scrollback: 5000, allowProposedApi: true,
  theme: { background: '#181a1f', foreground: '#dce1eb' },
});
const fit = new FitAddon.FitAddon();
term.loadAddon(fit);
term.loadAddon(new Unicode11Addon.Unicode11Addon());
term.unicode.activeVersion = '11';
term.open(document.getElementById('terminal'));
function send(message) { window.chrome.webview.postMessage(message); }
const find = createTerminalSearch(term);
let cwdLimit = 0;
// xterm owns OSC framing across output chunks. The native side independently
// validates the URI and local filesystem boundary before accepting metadata.
term.parser.registerOscHandler(7, data => {
  if (cwdLimit > 0 && data.length <= cwdLimit) send({kind:'cwd', data});
  return true;
});
function resize() { const host=document.getElementById('terminal'); if (host.clientWidth > 30 && host.clientHeight > 30) fit.fit(); }
term.onData(data => send({kind:'input', data}));
term.onResize(size => send({kind:'resize', ...size}));
term.textarea.addEventListener('focus', () => send({kind:'focus'}));
term.element.addEventListener('pointerdown', event => { if (event.isTrusted) send({kind:'activate'}); });
document.getElementById('find-bar').addEventListener('pointerdown', event => { if (event.isTrusted) send({kind:'activate'}); });
window.chrome.webview.addEventListener('message', event => {
  const m = event.data;
  if (m.kind === 'output') term.write(m.data, () => send({kind:'ack', id:m.id}));
  else if (m.kind === 'paste') { if (!find.paste(m.data)) term.paste(m.data); }
  else if (m.kind === 'configure') { cwdLimit = m.cwdLimit; find.configure(m.strings, m.locale); }
  else if (m.kind === 'find') find.open();
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
