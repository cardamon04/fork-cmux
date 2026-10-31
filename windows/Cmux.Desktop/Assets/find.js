'use strict';
function createTerminalSearch(term) {
  const search = new SearchAddon.SearchAddon({highlightLimit:1000});
  term.loadAddon(search);
  const bar = document.getElementById('find-bar');
  const query = document.getElementById('find-query');
  const previous = document.getElementById('find-previous');
  const next = document.getElementById('find-next');
  const matchCase = document.getElementById('find-case');
  const results = document.getElementById('find-results');
  const close = document.getElementById('find-close');
  const decorations = {
    matchBackground:'#5e4b16', matchOverviewRuler:'#d9ad32',
    activeMatchBackground:'#245d99', activeMatchColorOverviewRuler:'#75adff',
  };
  let strings, numbers, pending, composing = false;
  function clear() {
    clearTimeout(pending);
    search.clearDecorations();
    term.clearSelection();
    next.disabled = previous.disabled = true;
  }
  function run(backward = false, incremental = false) {
    clearTimeout(pending);
    if (bar.hidden || composing) return;
    if (!query.value) { clear(); results.textContent = strings.FindEmpty; return; }
    const options = {regex:false, caseSensitive:matchCase.checked, incremental, decorations};
    if (backward) search.findPrevious(query.value, options);
    else search.findNext(query.value, options);
  }
  function changed() {
    clear();
    if (bar.hidden || composing) return;
    results.textContent = strings[query.value ? 'FindSearching' : 'FindEmpty'];
    if (query.value) pending = setTimeout(() => run(false, true), 150);
  }
  search.onDidChangeResults(({resultIndex, resultCount}) => {
    if (bar.hidden || composing) return;
    next.disabled = previous.disabled = resultCount === 0;
    // The addon caps highlighted results at 1,000; never present that cap as an exact total.
    results.textContent = resultCount === 0 ? strings.FindMissing
      : resultCount >= 1000 ? '≥ ' + numbers.format(1000)
      : numbers.format(resultIndex + 1) + ' / ' + numbers.format(resultCount);
  });
  bar.addEventListener('submit', event => event.preventDefault());
  query.addEventListener('input', changed);
  query.addEventListener('compositionstart', () => { composing = true; clear(); });
  query.addEventListener('compositionend', () => { composing = false; changed(); });
  matchCase.addEventListener('change', changed);
  previous.addEventListener('click', () => run(true));
  next.addEventListener('click', () => run());
  function dismiss() {
    clear(); bar.hidden = true; term.focus();
  }
  close.addEventListener('click', dismiss);
  bar.addEventListener('keydown', event => {
    if (event.isComposing || composing) return;
    if (event.key === 'Escape') { event.preventDefault(); dismiss(); }
    else if (event.key === 'Enter' && event.target === query) { event.preventDefault(); run(event.shiftKey); }
  });
  window.addEventListener('pagehide', clear);
  return Object.freeze({
    configure: (localized, locale) => {
      strings = localized;
      document.documentElement.lang = locale || 'en';
      numbers = new Intl.NumberFormat(locale || 'en');
      query.placeholder = query.ariaLabel = strings.FindPlaceholder;
      for (const [button, key] of [[previous,'FindPrevious'],[next,'FindNext'],[close,'FindClose']])
        button.title = button.ariaLabel = strings[key];
      document.getElementById('find-case-label').textContent = strings.FindCase;
      bar.ariaLabel = results.ariaLabel = strings.FindResults;
    },
    open: () => {
      if (!strings) return;
      bar.hidden = false; query.focus(); query.select(); changed();
    },
  });
}
