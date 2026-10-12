// Instant feedback when moving between pages. Pages load data from JobTread/QuickBooks on the server,
// which can take a moment; this shows the click registered: the tapped pill/button lights up, a bar runs
// across the top, and the page dims until the next page arrives.
(function () {
  function start(el) {
    document.body.classList.add('navigating');
    if (el) el.classList.add('pressed');
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
    start(form.querySelector('[type=submit]') || null);
  });
  // Coming back with the browser's Back button restores the page from cache: clear the state.
  window.addEventListener('pageshow', function () {
    document.body.classList.remove('navigating');
    document.querySelectorAll('.pressed').forEach(function (el) { el.classList.remove('pressed'); });
  });
})();
