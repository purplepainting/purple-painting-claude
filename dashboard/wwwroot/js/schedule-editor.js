// Schedule editor: edit the week in a draft, then push the changes to JobTread.
// Data comes from <script id="schedule-data"> (built in Pages/Scheduling/Index.cshtml.cs).
// Nothing is sent to JobTread until "Push" in the review. The draft is kept in this browser
// (localStorage) so a reload doesn't lose it.
(function () {
  var dataEl = document.getElementById('schedule-data');
  var editBtn = document.getElementById('edit-schedule');
  var root = document.getElementById('schedule-editor');
  var view = document.getElementById('schedule-view');
  if (!dataEl || !editBtn || !root) return;

  var data = JSON.parse(dataEl.textContent);
  var token = (document.querySelector('input[name="__RequestVerificationToken"]') || {}).value;
  var draftKey = 'schedule-draft:' + data.weekStart + ':' + (data.who || 'crew');
  var DOW = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

  var people = {};
  data.people.forEach(function (p) { people[p.id] = p; });
  var allJobs = {};
  data.jobs.concat(data.addableJobs || []).forEach(function (j) { allJobs[j.id] = j; });

  // ---- State ----
  var original = {};            // task id → fields as loaded
  var tasks = [];               // working copy (includes new and deleted)
  var shownJobs = data.jobs.map(function (j) { return j.id; });
  var selected = null;          // { name/id of painter, fromKey, day } in tap-to-move mode
  var nextKey = 1;

  function fieldsOf(t) {
    return { name: t.name, startDate: t.start, endDate: t.end, startTime: t.startTime || null, endTime: t.endTime || null, crew: t.crew.slice() };
  }

  function load() {
    original = {};
    tasks = data.tasks.map(function (t) {
      original[t.id] = fieldsOf(t);
      return { key: 'k' + (nextKey++), id: t.id, jobId: t.jobId, name: t.name, start: t.start, end: t.end, startTime: t.startTime, endTime: t.endTime,
        crew: t.crew.slice(), parentTaskId: t.parentTaskId, taskTypeId: t.taskTypeId, dependents: t.dependents, isNew: false, deleted: false };
    });
    shownJobs = data.jobs.map(function (j) { return j.id; });
  }

  // ---- Draft persistence (per week, this browser only) ----
  function saveDraft() {
    try {
      if (changes().length === 0) localStorage.removeItem(draftKey);
      else localStorage.setItem(draftKey, JSON.stringify({ tasks: tasks, shownJobs: shownJobs, savedAt: Date.now() }));
    } catch (e) { /* storage unavailable: draft just isn't kept */ }
  }
  function readDraft() {
    try { var d = JSON.parse(localStorage.getItem(draftKey) || 'null'); return d && d.tasks ? d : null; } catch (e) { return null; }
  }
  function clearDraft() { try { localStorage.removeItem(draftKey); } catch (e) { } }

  // ---- Helpers ----
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  function parse(d) { var p = d.split('-'); return new Date(+p[0], +p[1] - 1, +p[2]); }
  function iso(dt) { return dt.getFullYear() + '-' + String(dt.getMonth() + 1).padStart(2, '0') + '-' + String(dt.getDate()).padStart(2, '0'); }
  function addDays(d, n) { var dt = parse(d); dt.setDate(dt.getDate() + n); return iso(dt); }
  function diffDays(a, b) { return Math.round((parse(b) - parse(a)) / 864e5); }
  function dayLabel(d) { var dt = parse(d); return DOW[dt.getDay()] + ' ' + (dt.getMonth() + 1) + '/' + dt.getDate(); }
  function covers(t, d) { return t.start <= d && d <= t.end; }
  function clock(t) {
    if (!t) return '';
    var p = t.split(':'), h = +p[0], m = p[1];
    var ap = h >= 12 ? 'PM' : 'AM'; h = h % 12 || 12;
    return h + (m === '00' ? '' : ':' + m) + ' ' + ap;
  }
  function times(t) { return t.startTime ? clock(t.startTime) + (t.endTime ? ' – ' + clock(t.endTime) : '') : ''; }
  var datePrefix = /^(Day\s*\d+\s*\([^)]*\)|(Mon|Tue|Wed|Thu|Fri|Sat|Sun)[a-z]*,?\s*[\d./]+)\s*[—–-]\s*/i;
  function shortName(n) { var s = n.replace(datePrefix, '').trim(); return s || n; }
  // Task names often start with the date ("Mon, 10.12 — Paint"). Keep that prefix in step with the new date.
  function renameForDate(name, date) {
    if (!datePrefix.test(name)) return name;
    var dt = parse(date);
    return DOW[dt.getDay()] + ', ' + String(dt.getMonth() + 1).padStart(2, '0') + '.' + String(dt.getDate()).padStart(2, '0') + ' — ' + shortName(name);
  }
  function level(id) { return (people[id] || {}).level || 'painter'; }
  var RANK = { foreman: 0, painter: 1, helper: 2, supervisor: 3 };
  // Foremen first, then painters, helpers, supervisors.
  function ordered(ids) { return ids.slice().sort(function (a, b) { return (RANK[level(a)] - RANK[level(b)]) || first(a).localeCompare(first(b)); }); }
  function first(id) { return (people[id] || {}).first || '?'; }
  function sameCrew(a, b) { return a.slice().sort().join() === b.slice().sort().join(); }
  function isChanged(t) {
    if (t.isNew) return !t.deleted;
    if (t.deleted) return true;
    var o = original[t.id];
    return o.name !== t.name || o.startDate !== t.start || o.endDate !== t.end || (o.startTime || null) !== (t.startTime || null)
      || (o.endTime || null) !== (t.endTime || null) || !sameCrew(o.crew, t.crew);
  }
  function label(t) { return ((allJobs[t.jobId] || {}).name || 'Job') + ' · ' + shortName(t.name); }
  function live() { return tasks.filter(function (t) { return !t.deleted; }); }
  function byKey(k) { return tasks.find(function (t) { return t.key === k; }); }
  function balance(crew) {
    var n = { foreman: 0, painter: 0, helper: 0 };
    crew.forEach(function (id) { var l = level(id); if (n[l] !== undefined) n[l]++; });
    return [n.foreman ? n.foreman + 'F' : '', n.painter ? n.painter + 'P' : '', n.helper ? n.helper + 'H' : ''].filter(Boolean).join(' · ');
  }
  function money(n) { return '$' + Math.round(n).toLocaleString('en-US'); }

  // ---- Changes for the review / push ----
  function changes() {
    return tasks.filter(isChanged).map(function (t) {
      var upd = fieldsOf(t);
      if (t.isNew) return { kind: 'create', jobId: t.jobId, parentTaskId: t.parentTaskId, taskTypeId: t.taskTypeId, updated: upd, label: label(t), _t: t };
      if (t.deleted) {
        // If JobTread won't allow the delete, the server falls back to these values (DELETE, Sunday, no crew).
        var o = original[t.id], sunday = data.days[6];
        var fallback = { name: /^DELETE\b/i.test(o.name) ? o.name : DELETE_PREFIX + o.name, startDate: sunday, endDate: sunday, startTime: o.startTime, endTime: o.endTime, crew: [] };
        return { kind: 'delete', taskId: t.id, original: o, updated: fallback, label: label(t), _t: t };
      }
      return { kind: 'update', taskId: t.id, original: original[t.id], updated: upd, label: label(t), _t: t };
    });
  }

  // ---- Rendering ----
  function render() {
    var days = data.days;
    var shown = shownJobs.map(function (id) { return allJobs[id]; }).filter(Boolean);
    var addable = (data.addableJobs || []).filter(function (j) { return shownJobs.indexOf(j.id) < 0; });
    var n = changes().length;

    var html = '';
    html += '<div class="editor-toolbar">'
      + '<span class="edit-pill">Editing</span>'
      + '<button type="button" class="button secondary" data-act="new-task">+ New task</button>'
      + (addable.length ? '<label class="add-job"><span class="visually-hidden">Add a job to the schedule</span><select data-act="add-job"><option value="">+ Add job…</option>'
        + addable.map(function (j) { return '<option value="' + esc(j.id) + '">' + esc(j.name) + '</option>'; }).join('') + '</select></label>' : '')
      + '<button type="button" class="button secondary" data-act="done">Done editing</button>'
      + '</div>';
    html += '<p class="muted editor-help">Drag a box to another day to move the whole crew. Drag a painter (or tap a painter, then tap where they go) to move just them; '
      + 'drop on <b>Not scheduled</b> to take them off. Tap a box to change times, crew or name. Nothing goes to JobTread until you push.</p>';
    if (selected) html += '<div class="select-hint">Moving <b>' + esc(first(selected.id)) + '</b> on ' + esc(dayLabel(selected.day)) + ': tap a job box that day, or Not scheduled. <button type="button" class="link-button" data-act="cancel-select">Cancel</button></div>';

    html += '<div class="card schedule-grid-card"><div class="table-wrap"><table class="schedule-grid editor-grid"><thead><tr><th class="row-head">Job</th>';
    days.forEach(function (d) { html += '<th class="' + (d === data.today ? 'today' : '') + '">' + esc(dayLabel(d)) + '</th>'; });
    html += '</tr></thead><tbody>';

    shown.forEach(function (j) {
      html += '<tr style="--h:' + j.hue + '"><th class="row-head" scope="row"><span class="swatch"></span><span class="row-title">' + esc(j.name) + '</span>'
        + '<span class="row-sub muted">' + esc(j.type || '') + (j.pm ? ' <span class="pm-tag" style="color:' + esc(j.pmColor) + ';background:color-mix(in srgb,' + esc(j.pmColor) + ' 12%,transparent);border-color:color-mix(in srgb,' + esc(j.pmColor) + ' 35%,transparent)">PM ' + esc(j.pm.split(' ')[0]) + '</span>' : '') + '</span>';
      var week = 0, est = false;
      Object.keys(j.costs || {}).forEach(function (d) { week += j.costs[d].amount; if (j.costs[d].estimate) est = true; });
      if (week > 0) html += '<span class="row-cost num">Week ' + money(week) + (est ? '<span class="muted"> · incl. est.</span>' : '') + '</span>';
      html += '</th>';
      days.forEach(function (d) {
        html += '<td class="drop" data-job="' + esc(j.id) + '" data-day="' + d + '">';
        live().filter(function (t) { return t.jobId === j.id && covers(t, d); }).forEach(function (t) {
          var ch = isChanged(t);
          html += '<div class="block editable' + (isMarked(t) ? ' marked-delete' : '') + (t.isNew ? ' is-new' : ch ? ' is-changed' : '') + (selected && selected.day === d ? ' targetable' : '') + '" draggable="true" data-key="' + t.key + '" data-day="' + d + '" style="--h:' + j.hue + '">'
            + (t.isNew ? '<span class="edit-tag new">New</span>' : ch ? '<span class="edit-tag">Changed</span>' : '')
            + '<span class="block-title">' + esc(shortName(t.name)) + '</span>'
            + '<span class="block-time">' + esc(times(t)) + (t.start !== t.end ? ' · ' + esc(dayLabel(t.start)) + '–' + esc(dayLabel(t.end)) : '') + '</span>'
            + '<span class="pchips">' + (t.crew.length ? ordered(t.crew).map(function (id) {
              return '<span class="pchip lvl-' + level(id) + (selected && selected.id === id && selected.fromKey === t.key ? ' selected' : '') + '" draggable="true" data-person="' + esc(id) + '" data-from="' + t.key + '" data-day="' + d + '">' + esc(first(id)) + '</span>';
            }).join('') : '<em class="block-sub">Unassigned</em>') + '</span>'
            + (balance(t.crew) ? '<span class="block-balance">' + balance(t.crew) + '</span>' : '')
            + (t.dependents && !t.isNew ? '<span class="block-sub">⛓ ' + t.dependents + ' linked task' + (t.dependents > 1 ? 's' : '') + ' move with it</span>' : '')
            + '</div>';
        });
        var dc = (j.costs || {})[d];
        if (dc) html += '<div class="day-cost num' + (dc.estimate ? ' est' : '') + '">' + (dc.estimate ? 'est. ' : '') + money(dc.amount) + '</div>';
        html += '<button type="button" class="add-day" data-act="add-day" data-job="' + esc(j.id) + '" data-day="' + d + '">+ Add day</button></td>';
      });
      html += '</tr>';
    });

    // Not scheduled: field staff not on any task that day.
    html += '<tr class="free-row"><th class="row-head" scope="row">Not scheduled<span class="row-sub muted">Drag or tap ' + (data.who === 'supervisors' ? 'a supervisor' : 'a painter') + ' onto a job</span></th>';
    days.forEach(function (d) {
      var busy = {};
      live().filter(function (t) { return covers(t, d); }).forEach(function (t) { t.crew.forEach(function (id) { busy[id] = true; }); });
      var wantSupervisors = data.who === 'supervisors';
      var free = data.people.filter(function (p) { return (p.level === 'supervisor') === wantSupervisors && !busy[p.id]; })
        .sort(function (a, b) { return (RANK[a.level] - RANK[b.level]) || a.first.localeCompare(b.first); });
      html += '<td class="drop free' + (selected && selected.day === d ? ' targetable' : '') + '" data-free="1" data-day="' + d + '"><span class="pchips">'
        + free.map(function (p) { return '<span class="pchip lvl-' + p.level + (selected && selected.id === p.id && selected.fromKey === null && selected.day === d ? ' selected' : '') + '" draggable="true" data-person="' + esc(p.id) + '" data-from="" data-day="' + d + '">' + esc(p.first) + '</span>'; }).join('')
        + '</span></td>';
    });
    html += '</tr></tbody></table></div></div>';

    html += '<div class="pending-bar' + (n ? '' : ' empty') + '"><span><b>' + n + ' ' + (n === 1 ? 'change' : 'changes') + '</b> ' + (n ? 'waiting · nothing sent to JobTread yet' : '') + '</span>'
      + '<span class="pending-actions"><button type="button" class="button secondary" data-act="undo-all"' + (n ? '' : ' disabled') + '>Undo all</button>'
      + '<button type="button" class="button" data-act="review"' + (n ? '' : ' disabled') + '>Review &amp; push</button></span></div>';

    root.innerHTML = html + panelHtml + reviewHtml;
    saveDraft();
  }

  // ---- Edit panel ----
  var panelHtml = '', reviewHtml = '';

  function openPanel(t, isCreate) {
    var jobOptions = shownJobs.map(function (id) { var j = allJobs[id]; return '<option value="' + esc(id) + '"' + (t.jobId === id ? ' selected' : '') + '>' + esc(j.name) + '</option>'; }).join('');
    var groups = { foreman: 'Foremen', painter: 'Painters', helper: 'Helpers', supervisor: 'Supervisors' };
    var crewHtml = Object.keys(groups).map(function (lvl) {
      var list = data.people.filter(function (p) { return p.level === lvl; });
      if (!list.length) return '';
      return '<fieldset class="crew-group"><legend><span class="pchip lvl-' + lvl + '">' + groups[lvl] + '</span></legend>'
        + list.map(function (p) { return '<label class="crew-check"><input type="checkbox" name="crew" value="' + esc(p.id) + '"' + (t.crew.indexOf(p.id) >= 0 ? ' checked' : '') + '> ' + esc(p.name) + '</label>'; }).join('')
        + '</fieldset>';
    }).join('');

    panelHtml = '<div class="drawer-scrim" data-act="close-panel"></div><aside class="drawer card" role="dialog" aria-label="Edit task">'
      + '<form id="task-form" data-key="' + (t.key || '') + '" data-create="' + (isCreate ? '1' : '') + '">'
      + '<h2>' + (isCreate ? 'New task' : esc(shortName(t.name))) + '</h2>'
      + (isCreate ? '<label class="field"><span>Job</span><select name="jobId" id="f-job">' + jobOptions + '</select></label>' : '<p class="muted">' + esc((allJobs[t.jobId] || {}).name) + '</p>')
      + '<label class="field"><span>Task name</span><input name="name" id="f-name" value="' + esc(t.name) + '" required></label>'
      + '<div class="two"><label class="field"><span>Start date</span><input type="date" name="start" id="f-start" value="' + esc(t.start) + '" required></label>'
      + '<label class="field"><span>End date</span><input type="date" name="end" id="f-end" value="' + esc(t.end) + '" required></label></div>'
      + '<div class="two"><label class="field"><span>Start time</span><input type="time" name="startTime" id="f-st" value="' + esc(t.startTime || '') + '"></label>'
      + '<label class="field"><span>End time</span><input type="time" name="endTime" id="f-et" value="' + esc(t.endTime || '') + '"></label></div>'
      + '<div class="field"><span>Crew</span><div class="crew-picker">' + crewHtml + '</div></div>'
      + (t.dependents && !t.isNew ? '<p class="notice-inline">Changing the dates also moves ' + t.dependents + ' linked task' + (t.dependents > 1 ? 's' : '') + ' in JobTread.</p>' : '')
      + '<p class="muted small">The crew gets a JobTread notification when you push.</p>'
      + '<div class="drawer-actions"><button type="submit" class="button">Save to draft</button>'
      + '<button type="button" class="button secondary" data-act="close-panel">Cancel</button>'
      + (!isCreate && t.isNew ? '<button type="button" class="button danger" data-act="delete-task" data-key="' + t.key + '">Remove this new day</button>' : '')
      + (!isCreate && !t.isNew ? '<button type="button" class="button danger" data-act="mark-delete" data-key="' + t.key + '" title="Deletes it in JobTread when you push. If JobTread doesn\u2019t allow deleting, it\u2019s renamed DELETE, moved to Sunday and its crew cleared instead.">Delete</button>' : '')
      + '</div></form></aside>';
    render();
    var nameInput = document.getElementById('f-name');
    if (nameInput) nameInput.focus();
  }
  function closePanel() { panelHtml = ''; render(); }

  function savePanel(form) {
    var f = new FormData(form);
    var start = f.get('start'), end = f.get('end') || start;
    if (end < start) end = start;
    var values = { name: (f.get('name') || '').trim() || 'New task', start: start, end: end, startTime: f.get('startTime') || null, endTime: f.get('endTime') || null, crew: f.getAll('crew') };
    if (form.dataset.create) {
      var jobId = f.get('jobId');
      var model = live().filter(function (t) { return t.jobId === jobId; }).sort(function (a, b) { return a.start < b.start ? 1 : -1; })[0];
      tasks.push(Object.assign({ key: 'k' + (nextKey++), id: null, jobId: jobId, parentTaskId: model ? model.parentTaskId : null, taskTypeId: model ? model.taskTypeId : null, dependents: 0, isNew: true, deleted: false }, values));
    } else {
      Object.assign(byKey(form.dataset.key), values);
    }
    panelHtml = ''; render();
  }

  // ---- Review & push ----
  function openReview() {
    var list = changes();
    reviewHtml = '<div class="drawer-scrim" data-act="close-review"></div><aside class="review-modal card" role="dialog" aria-label="Review changes">'
      + '<h2>Review ' + list.length + ' ' + (list.length === 1 ? 'change' : 'changes') + '</h2>'
      + '<ul class="review-list">' + list.map(function (c) {
        var t = c._t, o = c.original, lines = [];
        if (c.kind === 'delete') lines.push('Delete from JobTread (' + dayLabel(o.startDate) + '). If JobTread doesn\u2019t allow it, it\u2019s renamed DELETE and moved to Sunday instead.');
        else if (c.kind === 'create') lines.push('New: ' + dayLabel(t.start) + (t.end !== t.start ? '–' + dayLabel(t.end) : '') + ' · ' + times(t) + ' · ' + (t.crew.map(first).join(', ') || 'no crew'));
        else {
          if (o.startDate !== t.start || o.endDate !== t.end) lines.push('Date ' + dayLabel(o.startDate) + ' → ' + dayLabel(t.start) + (t.dependents ? ' · ⛓ ' + t.dependents + ' linked task' + (t.dependents > 1 ? 's' : '') + ' move too' : ''));
          if ((o.startTime || null) !== (t.startTime || null) || (o.endTime || null) !== (t.endTime || null)) lines.push('Time ' + (times({ startTime: o.startTime, endTime: o.endTime }) || 'none') + ' → ' + (times(t) || 'none'));
          if (!sameCrew(o.crew, t.crew)) {
            var added = t.crew.filter(function (id) { return o.crew.indexOf(id) < 0; }).map(first);
            var removed = o.crew.filter(function (id) { return t.crew.indexOf(id) < 0; }).map(first);
            lines.push('Crew' + (added.length ? ' +' + added.join(', +') : '') + (removed.length ? ' −' + removed.join(', −') : ''));
          }
          if (o.name !== t.name) lines.push(isMarked(t) && !/^DELETE\b/i.test(o.name) ? 'Marked for deletion (renamed DELETE, moved to Sunday, crew cleared)' : 'Name → ' + t.name);
        }
        return '<li class="review-item ' + c.kind + '"><b>' + esc(c.label) + '</b><span>' + lines.map(esc).join('<br>') + '</span></li>';
      }).join('') + '</ul>'
      + '<div id="push-results"></div>'
      + '<div class="drawer-actions"><button type="button" class="button" data-act="push">Push ' + list.length + ' to JobTread</button>'
      + '<button type="button" class="button secondary" data-act="close-review">Back to editing</button></div></aside>';
    render();
  }

  function push(button) {
    var list = changes().map(function (c) { var x = Object.assign({}, c); delete x._t; return x; });
    button.disabled = true; button.textContent = 'Pushing…';
    fetch('?handler=Push', {
      method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
      body: JSON.stringify({ changes: list })
    })
      .then(function (r) { return r.json().then(function (body) { if (!r.ok) throw new Error(body.error || ('Error ' + r.status)); return body; }); })
      .then(function (body) {
        var results = body.results || [];
        var ok = results.filter(function (r) { return r.status === 'Done'; }).length;
        var box = document.getElementById('push-results');
        box.innerHTML = '<p><b>' + ok + ' of ' + results.length + ' pushed.</b></p><ul class="review-list">' + results.map(function (r) {
          return '<li class="review-item result-' + r.status.toLowerCase() + '"><b>' + esc(r.label) + '</b><span>' + esc(r.message) + '</span></li>';
        }).join('') + '</ul>';
        clearDraft();
        button.textContent = 'Reload schedule';
        button.disabled = false;
        button.dataset.act = 'reload';
      })
      .catch(function (e) {
        button.disabled = false; button.textContent = 'Try again';
        document.getElementById('push-results').innerHTML = '<p class="notice-inline warn">Couldn’t push: ' + esc(e.message) + '. Your draft is still here.</p>';
      });
  }

  // ---- Moving painters ----
  function movePerson(personId, fromKey, day, target) {
    var from = fromKey ? byKey(fromKey) : null;
    if (target === 'free') {
      if (from) from.crew = from.crew.filter(function (id) { return id !== personId; });
    } else {
      var to = byKey(target);
      if (!to || to === from || !covers(to, day) || to.crew.indexOf(personId) >= 0) return false;
      if (from) from.crew = from.crew.filter(function (id) { return id !== personId; });
      to.crew.push(personId);
    }
    return true;
  }

  function moveTask(key, fromDay, toDay, jobId) {
    var t = byKey(key);
    if (!t || t.jobId !== jobId) return;
    var delta = diffDays(fromDay, toDay);
    if (!delta) return;
    t.start = addDays(t.start, delta); t.end = addDays(t.end, delta);
    t.name = renameForDate(t.name, t.start);
  }

  // Delete tries a real delete in JobTread. If JobTread refuses, the server instead renames the task
  // DELETE, moves it to this week's Sunday and clears the crew, so someone can delete it in JobTread.
  var DELETE_PREFIX = 'DELETE – ';
  function isMarked(t) { return /^DELETE\b/i.test(t.name); }
  function markForDeletion(t) {
    if (!t || isMarked(t)) return;
    var sunday = data.days[6];
    t.name = DELETE_PREFIX + t.name;
    t.start = sunday; t.end = sunday;
    t.crew = [];
  }

  function addDay(jobId, day) {
    var jobTasks = live().filter(function (t) { return t.jobId === jobId; });
    var model = jobTasks.filter(function (t) { return t.start <= day; }).sort(function (a, b) { return a.start < b.start ? 1 : -1; })[0] || jobTasks[0];
    tasks.push({
      key: 'k' + (nextKey++), id: null, jobId: jobId, isNew: true, deleted: false, dependents: 0,
      name: model ? renameForDate(model.name, day) : 'New day', start: day, end: day,
      startTime: model ? model.startTime : '07:00', endTime: model ? model.endTime : '15:30',
      crew: model ? model.crew.slice() : [], parentTaskId: model ? model.parentTaskId : null, taskTypeId: model ? model.taskTypeId : null
    });
  }

  // ---- Events ----
  var drag = null;
  root.addEventListener('dragstart', function (e) {
    var chip = e.target.closest('.pchip[data-person]');
    if (chip) { drag = { kind: 'person', id: chip.dataset.person, fromKey: chip.dataset.from || null, day: chip.dataset.day }; e.stopPropagation(); }
    else { var b = e.target.closest('.block.editable'); if (b) drag = { kind: 'task', key: b.dataset.key, day: b.dataset.day }; }
    if (drag) { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', 'x'); }
  });
  root.addEventListener('dragover', function (e) {
    if (!drag) return;
    var target = drag.kind === 'person' ? (e.target.closest('.block.editable') || e.target.closest('td.free')) : e.target.closest('td.drop:not(.free)');
    if (target) { e.preventDefault(); root.querySelectorAll('.over').forEach(function (x) { if (x !== target) x.classList.remove('over'); }); target.classList.add('over'); }
  });
  root.addEventListener('dragend', function () { root.querySelectorAll('.over').forEach(function (x) { x.classList.remove('over'); }); drag = null; });
  root.addEventListener('drop', function (e) {
    if (!drag) return;
    e.preventDefault();
    if (drag.kind === 'task') {
      var td = e.target.closest('td.drop:not(.free)');
      if (td) moveTask(drag.key, drag.day, td.dataset.day, td.dataset.job);
    } else {
      var block = e.target.closest('.block.editable'), free = e.target.closest('td.free');
      if (block && block.dataset.day === drag.day) movePerson(drag.id, drag.fromKey, drag.day, block.dataset.key);
      else if (free && free.dataset.day === drag.day) movePerson(drag.id, drag.fromKey, drag.day, 'free');
    }
    drag = null; render();
  });

  root.addEventListener('click', function (e) {
    var act = e.target.closest('[data-act]');
    var a = act && act.dataset.act;
    if (a === 'done') { stopEditing(); return; }
    if (a === 'new-task') { openPanel({ jobId: shownJobs[0], name: '', start: data.today >= data.days[0] && data.today <= data.days[6] ? data.today : data.days[0], end: '', startTime: '07:00', endTime: '15:30', crew: [] }, true); return; }
    if (a === 'add-day') { addDay(act.dataset.job, act.dataset.day); render(); return; }
    if (a === 'cancel-select') { selected = null; render(); return; }
    if (a === 'close-panel') { closePanel(); return; }
    if (a === 'delete-task') {
      // Only days added in this draft can be removed; the dashboard never deletes JobTread tasks.
      var t = byKey(act.dataset.key);
      if (t && t.isNew) tasks.splice(tasks.indexOf(t), 1);
      panelHtml = ''; render(); return;
    }
    if (a === 'mark-delete') { var dt = byKey(act.dataset.key); if (dt) dt.deleted = true; panelHtml = ''; render(); return; }
    if (a === 'undo-all') { load(); clearDraft(); selected = null; render(); return; }
    if (a === 'review') { openReview(); return; }
    if (a === 'close-review') { reviewHtml = ''; render(); return; }
    if (a === 'push') { push(act); return; }
    if (a === 'reload') { location.reload(); return; }

    // Tap-to-move painters (works with touch and mouse).
    var chip = e.target.closest('.pchip[data-person]');
    // With a painter selected, tapping anywhere in another box that day (even on someone's chip) moves them there.
    if (chip && selected) {
      var chipBlock = chip.closest('.block.editable'), chipFree = chip.closest('td.free');
      if (chipBlock && chipBlock.dataset.key !== selected.fromKey && chipBlock.dataset.day === selected.day) { movePerson(selected.id, selected.fromKey, selected.day, chipBlock.dataset.key); selected = null; render(); return; }
      if (chipFree && selected.fromKey && chipFree.dataset.day === selected.day) { movePerson(selected.id, selected.fromKey, selected.day, 'free'); selected = null; render(); return; }
    }
    if (chip) {
      var sameChip = selected && selected.id === chip.dataset.person && selected.fromKey === (chip.dataset.from || null) && selected.day === chip.dataset.day;
      selected = sameChip ? null : { id: chip.dataset.person, fromKey: chip.dataset.from || null, day: chip.dataset.day };
      render(); return;
    }
    var block = e.target.closest('.block.editable');
    if (selected) {
      var free = e.target.closest('td.free');
      if (block && block.dataset.day === selected.day) { movePerson(selected.id, selected.fromKey, selected.day, block.dataset.key); selected = null; render(); return; }
      if (free && free.dataset.day === selected.day) { movePerson(selected.id, selected.fromKey, selected.day, 'free'); selected = null; render(); return; }
    }
    if (block) { openPanel(byKey(block.dataset.key), false); }
  });

  root.addEventListener('change', function (e) {
    if (e.target.matches('select[data-act="add-job"]') && e.target.value) {
      shownJobs.push(e.target.value); render();
    }
  });
  root.addEventListener('submit', function (e) { if (e.target.id === 'task-form') { e.preventDefault(); savePanel(e.target); } });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && !root.hidden) { if (panelHtml) closePanel(); else if (reviewHtml) { reviewHtml = ''; render(); } else if (selected) { selected = null; render(); } } });

  function startEditing() {
    load();
    var draft = readDraft();
    if (draft && confirmRestore()) { tasks = draft.tasks; shownJobs = draft.shownJobs || shownJobs; nextKey = tasks.length + 1000; }
    view.hidden = true; root.hidden = false; editBtn.hidden = true;
    document.querySelectorAll('[data-view-only] .segmented').forEach(function (s) { s.hidden = true; });
    render();
  }
  function stopEditing() {
    if (changes().length && !window.__leaveOk) {
      root.querySelector('.pending-bar').classList.add('flash');
    }
    selected = null; panelHtml = ''; reviewHtml = '';
    root.hidden = true; view.hidden = false; editBtn.hidden = false;
    document.querySelectorAll('[data-view-only] .segmented').forEach(function (s) { s.hidden = false; });
    editBtn.textContent = changes().length ? 'Edit schedule (' + changes().length + ' unpushed)' : 'Edit schedule';
  }
  // A saved draft from earlier comes back automatically; "Undo all" clears it.
  function confirmRestore() { return true; }

  editBtn.addEventListener('click', startEditing);
  var pending = readDraft();
  if (pending) editBtn.textContent = 'Edit schedule (unpushed changes)';
})();
