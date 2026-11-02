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
  function completeWindow(row) {
    const buffer = term.buffer.active;
    if (!buffer.getLine(row - 1)) return false;
    // The pinned addon expands at most 2048 characters in each direction.
    // Refuse a whole logical block if that budget cannot cover it. Looking
    // at candidate endpoints misses trimmed punctuation and nested schemes.
    let characters = 0, lines = 0;
    for (let index = row - 1; buffer.getLine(index)?.isWrapped;) {
      const previous = buffer.getLine(--index);
      if (!previous || ++lines > 2048) return false;
      characters += previous.translateToString(true).length;
      if (characters > 2048) return false;
    }
    characters = lines = 0;
    for (let index = row; buffer.getLine(index)?.isWrapped; index++) {
      characters += buffer.getLine(index).translateToString(true).length;
      if (characters > 2048 || ++lines > 2048) return false;
    }
    return true;
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
          provideLinks(row, callback) {
            if (completeWindow(row)) provider.provideLinks(row, callback);
            else callback([]);
          },
        }),
      });
    },
    dispose() { addon?.dispose(); },
  };
  return {handler, plainAddon, configure: strings => { hint = strings.OpenLinkHint; }};
}
