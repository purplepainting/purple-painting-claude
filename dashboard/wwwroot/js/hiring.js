// Hiring pages (Employees → Hiring). Pages work without this file; it adds copy buttons and instant checkboxes.

// Copy a text template to the clipboard, ready to paste into Google Voice.
document.addEventListener('click', function (ev) {
  var button = ev.target.closest && ev.target.closest('[data-copy]');
  if (!button) return;
  var body = button.closest('.hire-text').querySelector('.hire-text-body');
  copy(body.textContent).then(function () { flash(button, 'Copied'); }, function () { flash(button, 'Select & copy'); selectText(body); });
});

function copy(text) {
  if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(text);
  // Older browsers and plain-http dev: a hidden textarea and execCommand.
  return new Promise(function (resolve, reject) {
    var area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    var ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    document.body.removeChild(area);
    if (ok) resolve(); else reject();
  });
}

function flash(button, label) {
  var original = button.dataset.label || button.textContent;
  button.dataset.label = original;
  button.textContent = label;
  button.classList.add('copied');
  clearTimeout(button._t);
  button._t = setTimeout(function () { button.textContent = original; button.classList.remove('copied'); }, 1600);
}

function selectText(el) {
  var range = document.createRange();
  range.selectNodeContents(el);
  var sel = window.getSelection();
  sel.removeAllRanges();
  sel.addRange(range);
}

// Reminder / confirmed / onboarding checkboxes save as soon as they're ticked. Inside a [data-live] section
// they post in the background (one at a time, in order) and the section is swapped for the saved version,
// so ticking several boxes quickly never loses one. Elsewhere, or without fetch, the form submits normally.
var saving = Promise.resolve();
document.addEventListener('change', function (ev) {
  var box = ev.target;
  if (!box.matches || !box.matches('input[data-autosubmit-check]')) return;
  var form = box.form;
  var live = box.closest('[data-live]');
  var label = box.closest('label');
  if (label) label.classList.add('saving');
  if (!live || !window.fetch || !window.DOMParser) { form.submit(); return; }
  var data = new FormData(form);
  var action = form.action;
  saving = saving.then(function () {
    return fetch(action, { method: 'POST', body: data, credentials: 'same-origin' })
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.text(); })
      .then(function (html) { swapLive(new DOMParser().parseFromString(html, 'text/html')); })
      .catch(function () {
        box.checked = !box.checked;
        if (label) { label.classList.remove('saving'); label.classList.add('save-failed'); label.title = 'Couldn’t save. Check the connection and try again.'; }
      });
  });
});

// Replace each [data-live] section (and [data-live-also] summary) with the copy from the saved page, keeping
// open dropdowns open.
function swapLive(doc) {
  document.querySelectorAll('[data-live][id], [data-live-also][id]').forEach(function (el) {
    var fresh = doc.getElementById(el.id);
    if (!fresh) return;
    var open = Array.prototype.map.call(el.querySelectorAll('details'), function (d) { return d.open; });
    var freshDetails = fresh.querySelectorAll('details');
    open.forEach(function (o, i) { if (o && freshDetails[i]) freshDetails[i].open = true; });
    el.replaceWith(document.importNode(fresh, true));
  });
}

// Interview sheet: Sales Rep candidates get the sales talking points instead of the painter points.
document.querySelectorAll('input[name="Form.Position"]').forEach(function (radio) {
  radio.addEventListener('change', function () {
    var sales = radio.dataset.sales === '1';
    document.querySelectorAll('[data-points]').forEach(function (set) {
      var show = (set.dataset.points === 'sales') === sales;
      set.hidden = !show;
      set.querySelectorAll('input').forEach(function (i) { i.disabled = !show; });
    });
  });
});
