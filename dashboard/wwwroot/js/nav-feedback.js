// Instant feedback while the next page loads: whatever was clicked lights up, a bar runs across the top,
// and on phones the menu closes right away. Links are also prefetched on hover / touch (see _Layout).
(function () {
  function start(el) {
    document.body.classList.add('navigating');
    if (el) el.classList.add('pressed');
    var toggle = document.getElementById('nav-toggle');
    if (toggle) toggle.checked = false;
  }

  document.addEventListener('click', function (e) {
    if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    var a = e.target.closest('a[href]');
    if (!a || a.target === '_blank' || a.hasAttribute('download')) return;
    var href = a.getAttribute('href');
    if (!href || href.charAt(0) === '#' || href.indexOf('mailto:') === 0 || href.indexOf('tel:') === 0) return;
    if (a.origin && a.origin !== location.origin) return;
    start(a);
  });

  document.addEventListener('submit', function (e) {
    if (e.defaultPrevented) return;
    var form = e.target;
    if (form.dataset.noFeedback !== undefined) return;
    start(e.submitter || form.querySelector('[type=submit], button:not([type])'));
  });

  // Back/forward restores the old page from cache: clear the loading state.
  window.addEventListener('pageshow', function () {
    document.body.classList.remove('navigating');
    document.querySelectorAll('.pressed').forEach(function (el) { el.classList.remove('pressed'); });
  });
})();
