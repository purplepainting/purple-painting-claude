// Small progressive enhancements. Pages work without this file; it only makes them nicer.

// Filter forms marked data-autosubmit apply as soon as a dropdown changes.
document.querySelectorAll('form[data-autosubmit] select').forEach(function (select) {
  select.addEventListener('change', function () { select.form.submit(); });
});

// Expandable rows (<details data-detail-url>) load their detail the first time they open.
document.querySelectorAll('details[data-detail-url]').forEach(function (details) {
  details.addEventListener('toggle', function () {
    if (!details.open || details.dataset.loaded) return;
    var body = details.querySelector('.job-detail-body');
    details.dataset.loaded = 'loading';
    fetch(details.dataset.detailUrl, { credentials: 'same-origin' })
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.text(); })
      .then(function (html) { body.innerHTML = html; details.dataset.loaded = 'yes'; })
      .catch(function () {
        body.innerHTML = '<p class="notice-inline warn">Couldn’t load cost codes. Close and reopen to try again.</p>';
        delete details.dataset.loaded;
      });
  });
});
