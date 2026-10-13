// Small progressive enhancements. Pages work without this file; it only makes them nicer.

// Filter forms marked data-autosubmit apply as soon as a dropdown changes.
document.querySelectorAll('form[data-autosubmit] select').forEach(function (select) {
  select.addEventListener('change', function () { select.form.submit(); });
});

// Expandable rows (<details data-detail-url>) load their detail the first time they open.
document.querySelectorAll('details[data-detail-url]').forEach(function (details) {
  details.addEventListener('toggle', load);
  details.addEventListener('toggle', closeOthers);

  // One open at a time: opening a row closes the others on the page, and keeps the
  // opened row in view if closing a row above it moved the page.
  function closeOthers() {
    if (!details.open) return;
    var moved = false;
    document.querySelectorAll('details[data-detail-url][open]').forEach(function (other) {
      if (other === details) return;
      if (other.compareDocumentPosition(details) & Node.DOCUMENT_POSITION_FOLLOWING) moved = true;
      other.open = false;
    });
    if (moved && details.getBoundingClientRect().top < 0) details.scrollIntoView({ block: 'start' });
  }
  if (details.open) { load(); details.scrollIntoView({ block: 'start' }); }

  function load() {
    if (!details.open || details.dataset.loaded) return;
    var body = details.querySelector('.job-detail-body');
    details.dataset.loaded = 'loading';
    fetch(details.dataset.detailUrl, { credentials: 'same-origin' })
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.text(); })
      .then(function (html) {
        body.innerHTML = html;
        details.dataset.loaded = 'yes';
        showTab(details, details.dataset.tab || 'costs');
      })
      .catch(function () {
        body.innerHTML = '<p class="notice-inline warn"></p>';
        body.firstChild.textContent = details.dataset.error || 'Couldn’t load cost codes. Close and reopen to try again.';
        delete details.dataset.loaded;
      });
  }
});

// Tabs inside an expanded job (Costs / Crew / Job details). Panels are marked data-panel.
function showTab(details, name) {
  details.dataset.tab = name;
  details.querySelectorAll('[data-panel]').forEach(function (panel) {
    panel.hidden = panel.dataset.panel !== name;
  });
  details.querySelectorAll('[data-tab]').forEach(function (button) {
    var active = button.dataset.tab === name;
    button.classList.toggle('active', active);
    button.setAttribute('aria-selected', active ? 'true' : 'false');
  });
}

document.querySelectorAll('details[data-detail-url] [data-tab]').forEach(function (button) {
  button.addEventListener('click', function () {
    showTab(button.closest('details'), button.dataset.tab);
  });
});

// Schedule month view on phones: tapping a day lists its jobs under the grid instead of opening the day.
// Wider screens (and no script) follow the link to the Day view.
document.querySelectorAll('[data-month]').forEach(function (grid) {
  var phone = window.matchMedia('(max-width: 760px)');
  grid.addEventListener('click', function (e) {
    var link = e.target.closest('a.month-date');
    if (!link || !phone.matches) return;
    e.preventDefault();
    var day = link.closest('[data-day]').dataset.day;
    document.querySelectorAll('.month-cell[data-day], .month-day[data-day]').forEach(function (el) {
      el.classList.toggle('selected', el.dataset.day === day);
    });
  });
});
