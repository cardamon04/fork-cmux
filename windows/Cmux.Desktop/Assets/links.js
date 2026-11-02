'use strict';
function createTerminalLinks(term, send) {
  let hint = '';
  function address(text) {
    if (typeof text !== 'string' || text.length === 0 || text.length > 32768
        || text !== text.trim() || /[\u0000-\u001f\u007f-\u009f]/.test(text)) return null;
    try {
      const url = new URL(text);
      return (url.protocol === 'http:' || url.protocol === 'https:') && url.hostname
        && !url.username && !url.password ? url.href : null;
    } catch { return null; }
  }
  const handler = {
    allowNonHttpProtocols: false,
    activate(event, text) {
      if (!event.isTrusted || event.button !== 0 || !event.ctrlKey
          || event.altKey || event.shiftKey || event.metaKey) return;
      const target = address(text);
      if (target !== null) { event.preventDefault(); send({kind: 'openLink', data: target}); }
    },
    hover(event, text) { term.element.title = address(text) === null ? '' : hint; },
    leave() { term.element.removeAttribute('title'); },
  };
  function complete(link) {
    const buffer = term.buffer.active, {start, end} = link.range;
    // The pinned addon bounds its wrapped-line scan. A match at a cut
    // continuation boundary is only a fragment, never a navigation target.
    const first = buffer.getLine(start.y - 1), previous = buffer.getLine(start.y - 2);
    if (start.x === 1 && first?.isWrapped && /\S$/.test(previous?.translateToString(false) || '')) return false;
    const next = end.x === 0 ? buffer.getLine(end.y - 1)
      : end.x === term.cols ? buffer.getLine(end.y) : null;
    return !next?.isWrapped || !/\S/.test(next.getCell(0)?.getChars() || '');
  }
  let addon;
  const plainAddon = {
    activate(terminal) {
      addon = new WebLinksAddon.WebLinksAddon(handler.activate, {hover: handler.hover, leave: handler.leave});
      // Keep xterm's public provider/mapping contract and the upstream regex;
      // filter partial candidates without copying or patching the addon.
      addon.activate({
        get buffer() { return terminal.buffer; }, get cols() { return terminal.cols; },
        registerLinkProvider: provider => terminal.registerLinkProvider({
          provideLinks(row, callback) { provider.provideLinks(row, candidates => callback(candidates?.filter(complete))); },
        }),
      });
    },
    dispose() { addon?.dispose(); },
  };
  return {handler, plainAddon, configure: strings => { hint = strings.OpenLinkHint; }};
}
