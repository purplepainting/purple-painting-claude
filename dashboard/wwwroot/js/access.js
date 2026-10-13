// Settings → Users & access. Picking a role redraws the permission boxes for that role (keeping any box that
// was changed from the old role), ticking an edit box ticks its view box, unticking view unticks edit, and
// boxes that differ from the role's default are shown bold. The server works the same without this script.
(function () {
  var el = document.getElementById('role-defaults');
  if (!el) return;
  var defaults = JSON.parse(el.textContent);

  function isDefault(role, key) { return (defaults[role] || []).indexOf(key) >= 0; }

  function mark(form) {
    var role = form.querySelector('[data-role]').value;
    form.querySelectorAll('input[data-key]').forEach(function (box) {
      box.closest('.access-check').classList.toggle('changed', box.checked !== isDefault(role, box.dataset.key));
    });
  }

  document.querySelectorAll('[data-access-form]').forEach(function (form) {
    var from = form.querySelector('input[name="fromRole"]');
    var select = form.querySelector('[data-role]');
    select.addEventListener('change', function () {
      var oldRole = from.value, newRole = select.value;
      form.querySelectorAll('input[data-key]').forEach(function (box) {
        if (box.checked === isDefault(oldRole, box.dataset.key)) box.checked = isDefault(newRole, box.dataset.key);
      });
      from.value = newRole;
      mark(form);
    });
    form.addEventListener('change', function (e) {
      var box = e.target;
      if (!box.dataset || !box.dataset.key) return;
      var key = box.dataset.key;
      if (box.checked && /\.edit$/.test(key)) {
        var view = form.querySelector('input[data-key="' + key.replace(/\.edit$/, '.view') + '"]');
        if (view) view.checked = true;
      }
      if (!box.checked && /\.view$/.test(key)) {
        var edit = form.querySelector('input[data-key="' + key.replace(/\.view$/, '.edit') + '"]');
        if (edit) edit.checked = false;
      }
      mark(form);
    });
  });
})();
