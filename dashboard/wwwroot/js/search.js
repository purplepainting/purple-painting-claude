// Global search in the top bar (Pages/Search). Results drop down as you type, grouped by module, from
// /search?handler=suggest. Up/Down pick a result, Enter opens it (or the full /search page when none is
// picked), Esc closes, "/" focuses search from anywhere. On phones the search icon opens a full-width box.
// Without this file the form still submits to /search, and the phone icon is a link to it.
(function () {
  var form = document.getElementById('search');
  if (!form) return;
  var input = form.querySelector('input[name=q]');
  var box = document.getElementById('search-results');
  var opener = document.querySelector('.search-open');
  var MIN = 2;
  var timer, controller, active = -1, shownFor = null, cache = {};

  function options() { return box.querySelectorAll('[role=option]'); }

  function show() { if (box.firstChild) { box.hidden = false; input.setAttribute('aria-expanded', 'true'); } }
  function hide() { box.hidden = true; input.setAttribute('aria-expanded', 'false'); setActive(-1); }

  // Phones: the icon opens the box over the top bar. Desktop: just focus the field.
  function open() {
    document.body.classList.add('search-active');
    input.focus();
    input.select();
    if (input.value.trim().length >= MIN) query();
  }
  function close() {
    hide();
    document.body.classList.remove('search-active');
    input.blur();
  }

  function setActive(i) {
    var list = options();
    active = list.length === 0 ? -1 : i < -1 ? list.length - 1 : i >= list.length ? -1 : i;
    list.forEach(function (o, n) {
      o.classList.toggle('active', n === active);
      o.setAttribute('aria-selected', n === active ? 'true' : 'false');
    });
    if (active >= 0) {
      input.setAttribute('aria-activedescendant', list[active].id);
      list[active].scrollIntoView({ block: 'nearest' });
    } else {
      input.removeAttribute('aria-activedescendant');
    }
  }

  function query() {
    var q = input.value.trim();
    if (q.length < MIN) { hide(); box.textContent = ''; shownFor = null; return; }
    if (q === shownFor) { show(); return; }
    if (cache[q]) { render(cache[q]); return; }
    if (controller) controller.abort();
    controller = new AbortController();
    form.classList.add('loading');
    fetch('/search?handler=suggest&q=' + encodeURIComponent(q), { credentials: 'same-origin', signal: controller.signal, headers: { Accept: 'application/json' } })
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
      .then(function (data) {
        cache[q] = data;
        if (input.value.trim() === q) render(data);
      })
      .catch(function (e) {
        if (e.name === 'AbortError') return;
        box.textContent = '';
        box.appendChild(el('p', 'search-empty', 'Search isn’t answering. Press Enter to open the results page.'));
        shownFor = null;
        show();
      })
      .finally(function () { form.classList.remove('loading'); });
  }

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text) e.textContent = text;
    return e;
  }

  // Bold the typed words inside a result's title (text only, never HTML from the server).
  function highlight(target, text, terms) {
    var pattern = terms.map(function (t) { return t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }).join('|');
    if (!pattern) { target.textContent = text; return; }
    text.split(new RegExp('(' + pattern + ')', 'ig')).forEach(function (part, i) {
      target.appendChild(i % 2 ? el('mark', null, part) : document.createTextNode(part));
    });
  }

  function render(data) {
    var terms = data.q.split(/\s+/).filter(Boolean);
    var n = 0;
    box.textContent = '';
    data.groups.forEach(function (g) {
      var group = el('div', 'search-group');
      group.setAttribute('role', 'group');
      var head = el('div', 'search-group-head');
      head.id = 'search-g-' + g.key;
      group.setAttribute('aria-labelledby', head.id);
      head.appendChild(el('span', null, g.label));
      if (g.total > g.hits.length) {
        // The module's own list filtered to the search, or else the full results page.
        var more = el('a', null, 'All ' + g.total);
        more.href = g.more || '/search?q=' + encodeURIComponent(data.q);
        more.tabIndex = -1;
        head.appendChild(more);
      } else {
        head.appendChild(el('span', 'search-count', String(g.total)));
      }
      group.appendChild(head);
      g.hits.forEach(function (h) {
        var row = el('div', 'search-row');
        var a = el('a', 'search-option');
        a.href = h.url;
        a.id = 'search-o-' + (n++);
        a.tabIndex = -1;
        a.setAttribute('role', 'option');
        highlight(a.appendChild(el('span', 'search-hit-title')), h.title, terms);
        if (h.sub) a.appendChild(el('span', 'search-hit-sub', h.sub));
        row.appendChild(a);
        if (h.ext) {
          var ext = el('a', 'search-hit-ext', 'JobTread ↗');
          ext.href = h.ext;
          ext.target = '_blank';
          ext.rel = 'noopener';
          ext.tabIndex = -1;
          row.appendChild(ext);
        }
        group.appendChild(row);
      });
      box.appendChild(group);
    });
    if (data.groups.length === 0) box.appendChild(el('p', 'search-empty', 'Nothing found for “' + data.q + '”.'));
    if (data.skipped.length) box.appendChild(el('p', 'search-note', 'Not searched (not connected or still loading): ' + data.skipped.join(', ')));
    var all = el('a', 'search-option search-all', 'All results for “' + data.q + '”');
    all.href = '/search?q=' + encodeURIComponent(data.q);
    all.id = 'search-o-' + n;
    all.tabIndex = -1;
    all.setAttribute('role', 'option');
    box.appendChild(all);
    shownFor = data.q;
    setActive(-1);
    show();
  }

  input.addEventListener('input', function () {
    clearTimeout(timer);
    timer = setTimeout(query, 150);
  });
  input.addEventListener('focus', function () { if (input.value.trim() === shownFor) show(); });

  input.addEventListener('keydown', function (e) {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (box.hidden) { show(); if (box.hidden) return; }
      setActive(active + (e.key === 'ArrowDown' ? 1 : -1));
    } else if (e.key === 'Enter' && active >= 0 && !box.hidden) {
      // Open the picked result. A real click, so nav-feedback shows the loading bar.
      e.preventDefault();
      options()[active].click();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      if (!box.hidden) hide();
      else close();
    }
  });

  // Enter with nothing picked goes to the full results page (a normal form submit).
  form.addEventListener('submit', function (e) {
    if (input.value.trim().length === 0) { e.preventDefault(); return; }
    clearTimeout(timer);
    hide();
  });

  box.addEventListener('click', function (e) {
    var a = e.target.closest('a');
    if (a && a.target !== '_blank') hide();
  });
  box.addEventListener('mousemove', function (e) {
    var o = e.target.closest('[role=option]');
    if (!o) return;
    var i = Array.prototype.indexOf.call(options(), o);
    if (i !== active) setActive(i);
  });

  form.querySelector('.search-close').addEventListener('click', close);
  if (opener) opener.addEventListener('click', function (e) { e.preventDefault(); open(); });

  // Tap or click anywhere else closes the dropdown (and the phone box).
  document.addEventListener('pointerdown', function (e) {
    if (form.contains(e.target) || (opener && opener.contains(e.target))) return;
    if (!box.hidden) hide();
    if (document.body.classList.contains('search-active') && !input.value.trim()) close();
  });

  // "/" focuses search unless you're typing in a field.
  document.addEventListener('keydown', function (e) {
    if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
    var t = e.target;
    if (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName)) return;
    e.preventDefault();
    open();
  });

  // Back/forward restores the page from cache: don't come back to an open dropdown.
  window.addEventListener('pageshow', function () { hide(); document.body.classList.remove('search-active'); });

  // A search result that points at a row on a page (#gear-12, #emp-…) opens that row.
  if (location.hash.length > 1) {
    var target = document.getElementById(decodeURIComponent(location.hash.slice(1)));
    if (target && target.tagName === 'DETAILS') target.open = true;
  }
})();
