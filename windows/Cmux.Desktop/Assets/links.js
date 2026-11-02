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
  return {handler, configure: strings => { hint = strings.OpenLinkHint; }};
}
