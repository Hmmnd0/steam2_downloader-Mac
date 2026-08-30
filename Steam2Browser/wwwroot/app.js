'use strict';

const $ = (s) => document.querySelector(s);
const el = (tag, cls, text) => {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text != null) n.textContent = text;
  return n;
};

const api = {
  async get(path) {
    const r = await fetch(path);
    if (!r.ok) throw new Error(await r.text());
    return r.json();
  },
  async post(path, body) {
    const r = await fetch(path, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body ?? {}),
    });
    if (!r.ok) throw new Error(await r.text());
    return r.json();
  },
};

function bytes(n) {
  if (n == null || n < 0) return '—';
  const u = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
  let v = n, i = 0;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v < 10 && i > 0 ? v.toFixed(2) : v < 100 ? v.toFixed(1) : Math.round(v)} ${u[i]}`;
}
const rate = (bps) => (!bps || bps <= 0 ? '—' : bytes(bps) + '/s');
// Size change, with the sign kept: a diff is only readable if growth and shrinkage look different.
const signed = (n) => (!n ? '±0' : (n > 0 ? '+' : '−') + bytes(Math.abs(n)));
const num = (n) => (n ?? 0).toLocaleString('en-US');

// ---------------- state ----------------

const state = {
  depots: {
    q: '', sort: 'id', dir: 'asc', filter: '', skip: 0, take: 120, total: 0,
    items: [], loading: false, done: false,
    rows: new Map(),      // depot id -> its row elements, so names can be filled in later
    lastNameCount: null,  // how many were named at the last in-place refresh
  },
  selected: null,
  detail: null,
  plan: null,
  settings: null,
  ready: false,
};

// ---------------- top bar ----------------

async function refreshState() {
  let s;
  try { s = await api.get('/api/state'); } catch { return; }

  state.settings = s.settings;
  $('#loadStatus').textContent = s.status.error ? `error: ${s.status.error}` : s.status.message || s.status.phase;
  $('#loadBar i').style.width = (s.status.phase === 'ready' ? 0 : s.status.percent || 0) + '%';

  const c = s.catalog;
  if (c) {
    renderStats([
      ['depots', num(c.depots), 'depots', false],
      ['dats', num(c.dats), 'dats', false],
      ['blobs', num(c.blobs), 'blobs', false],
      ['size', c.sizesLoaded ? '~' + bytes(c.totalBytes) : '…', 'archive size', false],
      ['resets', num(c.resetDepots), 'with resets', false],
      ['incomplete', num(c.incompleteDepots), 'incomplete', false],
      // Named counts every source, so it is a share of the whole archive. While a pass is
      // running the label says how much is left rather than restating the same fraction.
      ['named', `${num(s.names.named)} / ${num(c.depots)}`,
        s.names.running ? `naming — ${num(s.names.remaining)} left` : 'named', s.names.running],
      // Value is what Steam actually recognised. "checked" accumulates across runs while
      // "remaining" is this run's queue, so adding them made a denominator that meant nothing.
      ['steam', num(s.steam.found),
        s.steam.running ? `asking steam — ${num(s.steam.remaining)} left` : 'named by steam',
        s.steam.running],
    ]);

    maybeRefreshNames(s.names.named);
  }

  const sel = $('#mirrorSelect');
  const want = s.mirrors.map((m) => m.id + (m.speedBps > 0 ? Math.round(m.speedBps) : '')).join('|');
  if (sel.dataset.sig !== want) {
    sel.dataset.sig = want;
    sel.innerHTML = '';
    for (const m of s.mirrors) {
      const o = el('option');
      o.value = m.id;
      const speed = m.speedBps > 0 ? ` — ${rate(m.speedBps)}` : m.reachable === false ? ' — unreachable' : '';
      o.textContent = `${m.name} (${m.id})${speed}`;
      o.selected = m.active;
      sel.append(o);
    }
  }

  renderUpdate(s.update);

  if (!state.ready && s.status.ready) {
    state.ready = true;
    resetDepots();
  }
}

const UPDATE_LABEL = {
  unknown: 'update: not checked',
  checking: 'checking for updates…',
  empty: 'no release published yet',
  current: 'up to date',
  available: 'update available',
  error: 'update check failed',
};

function renderUpdate(u) {
  state.update = u;
  if (!u) return;

  // Only worth a spot in the header when there is something to act on.
  const chip = $('#updateChip');
  const show = u.state === 'available' || u.state === 'error';
  chip.hidden = !show;
  if (show) {
    chip.className = 'chip ' + u.state;
    chip.textContent = (u.state === 'available' ? '↑ ' : '! ') + UPDATE_LABEL[u.state];
    chip.title = u.message || '';
    chip.href = 'https://github.com/extremebleem/steam2_downloader/releases';
  }

  const text = $('#updText');
  if (text) text.textContent = u.message || UPDATE_LABEL[u.state] || u.state;

  const built = $('#updBuilt');
  if (built) {
    const parts = [];
    if (u.builtUtc) parts.push('this build: ' + new Date(u.builtUtc).toLocaleString());
    if (u.latestCommitUtc) parts.push('newest commit: ' + new Date(u.latestCommitUtc).toLocaleString());
    if (u.commitShort) parts.push(u.commitShort + (u.commitMessage ? ` — ${u.commitMessage}` : ''));
    built.textContent = parts.join('  ·  ');
  }
}

// Stat cells are built once and then only their text changes: rebuilding the row every poll
// would restart the "naming in progress" animation twice a second and make it stutter.
function renderStats(cells) {
  const host = $('#stats');

  if (host.childElementCount !== cells.length) {
    host.innerHTML = '';
    for (const [key] of cells) {
      const d = el('div');
      d.dataset.key = key;
      d.append(el('b'), el('span'));
      host.append(d);
    }
  }

  cells.forEach(([, value, label, busy], i) => {
    const cell = host.children[i];
    const b = cell.firstChild;
    const span = cell.lastChild;
    if (b.textContent !== value) b.textContent = value;
    if (span.textContent !== label) span.textContent = label;
    cell.classList.toggle('busy', !!busy);
  });
}

/// Names arrive in the background, so the visible rows are refreshed once every 100 of them.
function maybeRefreshNames(named) {
  const d = state.depots;

  if (d.lastNameCount === null) { d.lastNameCount = named; return; }
  if (named - d.lastNameCount < 100) return;

  d.lastNameCount = named;

  // A name search returns a different set as names land, so that case needs a real reload.
  if (d.q) resetDepots();
  else refreshNamesInPlace();
}

async function refreshNamesInPlace() {
  const d = state.depots;
  if (!d.skip) return;

  try {
    const q = new URLSearchParams({
      q: d.q, sort: d.sort, dir: d.dir, filter: d.filter,
      skip: 0, take: Math.min(d.skip, 2000),
    });
    const res = await api.get('/api/depots?' + q);

    for (const item of res.items) {
      const row = d.rows.get(item.id);
      if (!row) continue;
      row.nameEl.textContent = item.name || '';
      row.nameEl.classList.toggle('fromsteam', item.nameSource === 'steam');
    }
  } catch {
    /* the next tick will try again */
  }
}

// ---------------- depot list ----------------

function resetDepots() {
  Object.assign(state.depots, { skip: 0, items: [], total: 0, done: false });
  state.depots.rows.clear();
  $('#depotList').innerHTML = '';
  loadDepots();
}

async function loadDepots() {
  const d = state.depots;
  if (d.loading || d.done) return;
  d.loading = true;

  try {
    const q = new URLSearchParams({
      q: d.q, sort: d.sort, dir: d.dir, filter: d.filter,
      skip: d.skip, take: d.take,
    });
    const res = await api.get('/api/depots?' + q);

    d.total = res.total;
    d.skip += res.items.length;
    if (res.items.length === 0 || d.skip >= d.total) d.done = true;

    $('#depotCount').textContent = `${num(d.total)} depot${d.total === 1 ? '' : 's'}`;
    for (const item of res.items) $('#depotList').append(depotRow(item));
  } catch {
    /* keep whatever is already on screen */
  } finally {
    d.loading = false;
  }
}

function depotRow(x) {
  const row = el('div', 'depot');
  row.dataset.id = x.id;
  if (state.selected === x.id) row.classList.add('on');

  const idCell = el('span', 'id');
  idCell.append(el('b', null, String(x.id)));

  // Always present, even when still empty: names land in the background and get filled in here.
  const nameEl = el('span', 'dname', x.name || '');
  if (x.nameSource === 'steam') nameEl.classList.add('fromsteam');
  idCell.append(nameEl);

  row.append(idCell);
  state.depots.rows.set(x.id, { row, nameEl });
  row.append(el('span', 'sz', x.datBytes ? bytes(x.datBytes + x.blobBytes) : '—'));

  const meta = el('div', 'meta');
  meta.append(el('span', null, `${x.versions} ver`));
  meta.append(el('span', null, `${x.dats + x.blobs} files`));
  if (x.last) meta.append(el('span', null, x.last));
  if (x.hasReset) meta.append(el('span', 'tag reset', 'reset'));
  if (!x.complete) meta.append(el('span', 'tag gap', 'gaps'));
  // Only a warning when the depot is genuinely encrypted and no key is known for it —
  // most depots outside the key table are simply not encrypted.
  if (x.needsKey) meta.append(el('span', 'tag gap', 'no key'));
  row.append(meta);

  row.onclick = () => selectDepot(x.id);
  return row;
}

// ---------------- depot page ----------------

async function selectDepot(id) {
  state.selected = id;
  state.plan = null;
  for (const n of document.querySelectorAll('.depot')) n.classList.toggle('on', +n.dataset.id === id);

  const detail = $('#detail');
  detail.innerHTML = '<div class="muted">loading…</div>';

  try {
    state.detail = await api.get('/api/depots/' + id);
    renderDepot();
  } catch (e) {
    detail.innerHTML = '';
    detail.append(note('bad', 'Could not load depot', String(e.message || e)));
  }
}

function note(kind, title, body) {
  const n = el('div', 'note ' + kind);
  n.append(el('b', null, title));
  if (Array.isArray(body)) {
    const ul = el('ul');
    for (const line of body) ul.append(el('li', null, line));
    n.append(ul);
  } else if (body) {
    n.append(document.createTextNode(body));
  }
  return n;
}

function renderDepot() {
  const { summary: s, versions } = state.detail;
  const d = $('#detail');
  d.innerHTML = '';
  d.scrollTop = 0;

  const head = el('div', 'dhead');
  head.append(el('h2', null, s.name ? s.name : 'Depot ' + s.id));
  if (s.name) head.append(el('span', 'tag mode', 'depot ' + s.id));
  if (s.nameSource === 'steam') {
    head.append(el('span', 'tag steam', 'steam' + (s.steamType ? ' · ' + s.steamType : '')));
  }
  if (s.hasReset) head.append(el('span', 'tag reset', 'reset'));
  if (!s.complete) head.append(el('span', 'tag gap', 'incomplete'));
  if (s.complete && !s.hasReset) head.append(el('span', 'tag ok', 'clean chain'));
  d.append(head);

  const sub = el('div', 'dsub');
  for (const t of [
    `${s.versions} version${s.versions === 1 ? '' : 's'} (0–${s.maxVersion})`,
    `${s.dats} dats · ${s.blobs} blobs`,
    s.datBytes ? `~${bytes(s.datBytes + s.blobBytes)}` : 'sizes not loaded',
    s.first && s.last ? `${s.first} → ${s.last}` : '',
    s.roots?.length ? `top level: ${s.roots.slice(0, 6).join(', ')}` : '',
    s.nameSource === 'steam' && s.manifestName ? `manifest name: ${s.manifestName}` : '',
  ]) if (t) sub.append(el('span', null, t));
  d.append(sub);

  if (s.needsKey) {
    d.append(note('bad', 'Encrypted, and no key is known for it',
      'This depot really is AES-encrypted and it is not in the key table, so it cannot be unpacked ' +
      'unless you supply the key yourself. Downloading and browsing still work.'));
  } else if (s.encrypted === false && !s.hasKey) {
    d.append(note('info', 'Not encrypted — no key needed',
      'This depot is absent from the key table, but its files are plain zlib rather than AES, so it ' +
      'unpacks without one.'));
  }
  if (s.hasReset) {
    d.append(note('warn', 'This depot was reset',
      `Version(s) ${s.forkedVersions.join(', ')} exist more than once, so the chain forks there. ` +
      `Pick which blob you want below — the planner then follows the parent links recorded inside ` +
      `each blob to fetch only the files that version actually needs.`));
  }
  if (!s.complete) {
    const lines = [];
    if (s.missingDats.length) lines.push(`no dat for version(s): ${s.missingDats.join(', ')}`);
    if (s.missingBlobs.length) lines.push(`no blob for version(s): ${s.missingBlobs.join(', ')}`);
    d.append(note('bad', 'Chain is incomplete', lines));
  }

  d.append(planPanel(s));
  d.append(changesPanel(s.id));
}

function planPanel(s) {
  const p = el('div', 'panel');
  p.append(el('h3', null, 'Download chain'));
  const body = el('div', 'body');

  const row = el('div', 'planrow');

  const vLabel = el('label', null, 'Version');

  // Only versions the archive actually holds, newest first — gaps are common, so a plain
  // number box would happily accept a version that is not there.
  const vSel = el('select');
  vSel.id = 'planVersion';

  const ordered = [...state.detail.versions].sort((a, b) => b.version - a.version);
  const newest = ordered[0]?.version;
  const oldest = ordered[ordered.length - 1]?.version;

  for (const entry of ordered) {
    // The blob's timestamp, not the newest of the two. Every dat in the archive sits on an exact
    // second while blobs keep sub-second precision, so dat times record when the dump was built —
    // taking the max made both versions here show the same June date.
    const dated = (entry.blobs.length ? entry.blobs : entry.dats);
    const date = dated.map((f) => f.date).filter(Boolean).sort()[0];
    const forked = entry.blobs.length > 1 || entry.dats.length > 1;

    let label = `v${entry.version}`;
    if (date) label += `  ·  ${date.slice(0, 10)}`;

    // Numbering starts at v0, so spell out which end is which rather than leaving it to be guessed.
    if (entry.version === newest) label += '  ·  latest';
    if (entry.version === oldest) label += '  ·  first release';
    if (forked) label += `  ·  fork ×${Math.max(entry.blobs.length, entry.dats.length)}`;

    vSel.append(new Option(label, String(entry.version)));
  }
  vSel.value = String(s.maxVersion);
  if (!vSel.value && vSel.options.length) vSel.selectedIndex = 0;

  // Only shown when a version really forked. Everywhere else it sat there greyed out on "auto",
  // taking the space that the download size deserves.
  const crcLabel = el('label', null, 'Blob CRC');
  const crcSel = el('select');
  crcSel.id = 'planCrc';
  crcSel.append(new Option('auto', ''));

  const sizeInfo = el('span', 'plansize');

  const planBtn = el('button', 'ghost', 'Plan');
  const dlBtn = el('button', 'primary', 'Download chain');
  const exBtn = el('button', 'ghost', 'Extract');

  row.append(vLabel, vSel, crcLabel, crcSel, sizeInfo, planBtn, dlBtn, exBtn);
  body.append(row);

  const out = el('div');
  out.id = 'planOut';
  body.append(out);
  p.append(body);

  const fillCrc = () => {
    const v = +vSel.value;
    const entry = state.detail.versions.find((x) => x.version === v);
    const choices = entry?.blobs ?? [];

    crcSel.innerHTML = '';
    crcSel.append(new Option('auto', ''));
    for (const b of choices) crcSel.append(new Option(`${b.crc}  ·  ${b.date ?? ''}`, b.crc));

    // Nothing to pick unless the version forked, so the control stays out of the way entirely.
    const choose = choices.length > 1;
    crcLabel.hidden = !choose;
    crcSel.hidden = !choose;
  };

  // Deltas mean a version costs everything below it too, so the figure is for the whole chain.
  const updateSize = () => {
    const target = +vSel.value;
    let total = 0, have = 0, files = 0, unknown = 0, forked = false;

    for (const entry of state.detail.versions) {
      if (entry.version > target) continue;
      if (entry.blobs.length > 1 || entry.dats.length > 1) forked = true;

      for (const f of [...entry.blobs, ...entry.dats]) {
        files++;
        if (f.size >= 0) {
          total += f.size;
          if (f.local) have += f.size;
        } else {
          unknown++;
        }
      }
    }

    const left = Math.max(0, total - have);
    const parts = [`${unknown ? '≥' : '~'}${bytes(left)} to download`];
    if (have > 0) parts.push(`${bytes(have)} already here`);
    parts.push(`${num(files)} files`);
    // A fork below the target means both branches are counted; the planner picks one.
    if (forked) parts.push('fork below — planner may need less');

    sizeInfo.textContent = parts.join('  ·  ');
    sizeInfo.title = `chain v0…v${target}: ${bytes(total)} total`;
  };

  const onVersion = () => { fillCrc(); updateSize(); };
  vSel.onchange = onVersion;
  onVersion();

  planBtn.onclick = () => doPlan(s.id, +vSel.value, crcSel.value, false);
  dlBtn.onclick = () => doPlan(s.id, +vSel.value, crcSel.value, true);
  exBtn.onclick = () => doExtract(s.id, +vSel.value, crcSel.value);

  return p;
}

async function doPlan(depot, version, blobCrc, download) {
  const out = $('#planOut');
  out.innerHTML = '<div class="muted">resolving chain…</div>';

  try {
    const res = await api.post(download ? '/api/download' : '/api/plan', { depot, version, blobCrc: blobCrc || null });
    const plan = res.plan ?? res;
    state.plan = plan;
    renderPlan(plan, out);
    if (res.jobId) {
      showTab('downloads');
      $('#activity').classList.remove('min');
      pollJobs();
    }
  } catch (e) {
    out.innerHTML = '';
    out.append(note('bad', 'Planning failed', String(e.message || e)));
  }
}

function renderPlan(plan, out) {
  out.innerHTML = '';

  if (plan.error) {
    out.append(note('bad', 'Cannot build the chain', plan.error));
  }
  if (plan.warnings?.length) {
    out.append(note('warn', 'Heads up', plan.warnings));
  }
  if (plan.needsChoice) {
    out.append(note('info', 'Pick a blob',
      'This version exists more than once. Choose a CRC above, then plan again.'));
    return;
  }
  if (plan.error) return;

  const modeText = {
    direct: 'no fork below this version, so the whole run 0…N is needed',
    smart: 'fork resolved by following the parent links inside the blobs',
    superset: 'fork could not be followed, so every candidate is included',
  }[plan.mode] ?? plan.mode;

  const sum = el('div', 'summary');
  const cells = [
    [(plan.totalExact ? '' : '~') + bytes(plan.totalBytes), 'to download'],
    [num(plan.fileCount), 'files'],
    [num(plan.datCount), 'dats'],
    [num(plan.blobCount), 'blobs'],
    [num(plan.alreadyLocal), 'already local'],
  ];
  for (const [v, k] of cells) {
    const c = el('div');
    c.append(el('b', null, v), el('span', null, k));
    sum.append(c);
  }
  out.append(sum);

  const modeLine = el('div', 'dsub');
  modeLine.style.marginTop = '12px';
  modeLine.append(el('span', 'tag mode', plan.mode));
  modeLine.append(el('span', null, modeText));
  out.append(modeLine);

  if (plan.blobCrc) {
    out.append(el('pre', 'log',
      `chain pinned to blob crc ${plan.blobCrc} — extraction follows the parent links from there`));
  }

  const wrap = el('div', 'vtable');
  const t = el('table');
  t.innerHTML = '<thead><tr><th>File</th><th>Kind</th><th class="num">Version</th><th class="num">Size</th><th>Local</th></tr></thead>';
  const tb = el('tbody');
  for (const f of plan.files) {
    const tr = el('tr');
    tr.append(el('td', 'mono', f.name));
    tr.append(el('td', null, f.kind));
    tr.append(el('td', 'num', String(f.version)));
    tr.append(el('td', 'num', (f.exact ? '' : '~') + bytes(f.size)));
    const local = el('td');
    local.innerHTML = `<span class="dot${f.local ? ' have' : ''}"></span>${f.local ? 'yes' : ''}`;
    tr.append(local);
    tb.append(tr);
  }
  t.append(tb);
  wrap.append(t);
  out.append(wrap);
}

// The centre of a depot page: the whole version history, each version expandable to the files it
// changed. Everything comes from the blobs — a version's blob holds both the manifest and the list
// of files whose data sits in that version's dat — so no .dat is ever touched to build this.
function changesPanel(depotId) {
  const p = el('div', 'panel');
  p.append(el('h3', null, 'Version history'));

  const body = el('div', 'body');

  const bar = el('div', 'planrow');
  const btn = el('button', 'primary', 'Download all blobs');
  const note = el('span', 'hint');
  bar.append(btn, note);
  body.append(bar);

  const list = el('div', 'vhist');
  body.append(list);
  p.append(body);

  btn.onclick = async () => {
    btn.disabled = true;
    try { await api.post(`/api/depots/${depotId}/blobs`); } catch { /* status line shows it */ }
    pollHistory(depotId, true);
  };

  state.history = { depotId, list, btn, note, open: new Set() };
  pollHistory(depotId, false);
  return p;
}

async function pollHistory(depotId, keepPolling) {
  const h = state.history;
  if (!h || h.depotId !== depotId) return;

  let r;
  try { r = await api.get(`/api/depots/${depotId}/versions`); } catch { return; }
  if (!state.history || state.history.depotId !== depotId) return;

  const missing = r.versions.filter((v) => !v.local).length;
  h.btn.disabled = r.fetch.running || missing === 0;
  h.btn.textContent = missing === 0 ? 'All blobs downloaded' : `Download all blobs (${num(missing)})`;

  h.note.textContent = r.fetch.running
    ? `${num(r.fetch.done)} / ${num(r.fetch.total)} blobs…`
    : (r.fetch.message || 'blobs are kilobytes — the whole history costs a few MB');

  renderHistory(depotId, r.versions);

  if (r.fetch.running || keepPolling) {
    setTimeout(() => pollHistory(depotId, r.fetch.running), 1000);
  }
}

function renderHistory(depotId, versions) {
  const h = state.history;
  const list = h.list;

  // Rebuild only when the shape changed, so an open section is not collapsed under the user.
  const sig = versions.map((v) => `${v.version}/${v.crc}/${v.local ? v.changedCount : 'x'}`).join('|');
  if (list.dataset.sig === sig) return;
  list.dataset.sig = sig;

  list.innerHTML = '';

  // "First release" means the earliest version and nothing else. It is not the same as a version
  // whose dat happens to hold every file it has — that is true whenever everything was rewritten,
  // and it was labelling late versions of small depots as the first one.
  const earliest = Math.min(...versions.map((v) => v.version));

  for (const v of versions) {
    const key = `${v.version}/${v.crc}`;
    const d = el('details', 'vitem');
    if (h.open.has(key)) d.open = true;

    const sm = el('summary');
    sm.append(el('b', 'vver', 'v' + v.version));
    sm.append(el('span', 'vdate', v.date ? v.date.slice(0, 10) : '—'));

    if (v.error) {
      sm.append(el('span', 'vwhat bad', v.error.slice(0, 60)));
    } else if (!v.local) {
      sm.append(el('span', 'vwhat dim', 'blob not downloaded'));
    } else if (v.version === earliest) {
      sm.append(el('span', 'vwhat', `${num(v.addedCount)} files · first release`));
      sm.append(el('span', 'vdelta up', signed(v.deltaBytes)));
      sm.append(el('span', 'vsize', bytes(v.payloadBytes)));
    } else if (v.unclassified) {
      // Without the previous version's blob there is no way to tell a new file from a rewritten one.
      sm.append(el('span', 'vwhat dim',
        `${num(v.changedCount)} in this version · fetch v${v.version - 1} to split new from changed`));
      sm.append(el('span', 'vsize', bytes(v.payloadBytes)));
    } else {
      const bits = [];
      if (v.addedCount) bits.push(`${num(v.addedCount)} new`);
      if (v.changedCount) bits.push(`${num(v.changedCount)} changed`);
      if (v.removedCount) bits.push(`${num(v.removedCount)} removed`);

      sm.append(el('span', 'vwhat', bits.join(' · ') || 'nothing changed'));
      sm.append(el('span', 'vdelta ' + (v.deltaBytes > 0 ? 'up' : v.deltaBytes < 0 ? 'down' : ''),
        signed(v.deltaBytes)));
      sm.append(el('span', 'vsize', bytes(v.payloadBytes)));
    }

    sm.append(el('span', 'vcrc', v.crc));
    d.append(sm);

    const inner = el('div', 'vbody');
    inner.textContent = '';
    d.append(inner);

    d.ontoggle = () => {
      if (!d.open) { h.open.delete(key); return; }
      h.open.add(key);
      if (inner.dataset.loaded) return;
      loadVersionFiles(depotId, v, inner);
    };

    if (d.open && !inner.dataset.loaded) loadVersionFiles(depotId, v, inner);

    list.append(d);
  }
}

async function loadVersionFiles(depotId, v, host) {
  if (!v.local) {
    host.innerHTML = '<div class="muted">Download the blobs to see what this version changed.</div>';
    return;
  }

  host.innerHTML = '<div class="muted">reading the blob…</div>';

  let r;
  try {
    r = await api.get(`/api/depots/${depotId}/versions/${v.version}/files?crc=${encodeURIComponent(v.crc)}`);
  } catch (e) {
    host.innerHTML = '';
    host.append(note('bad', 'Could not read the file list', String(e.message || e)));
    return;
  }

  host.innerHTML = '';
  if (r.error) { host.append(note('bad', 'Cannot list the files', r.error)); return; }
  if (r.needsFetch) { host.innerHTML = '<div class="muted">blob is not downloaded yet</div>'; return; }

  host.dataset.loaded = '1';

  const filter = el('input');
  filter.type = 'search';
  filter.placeholder = 'Filter by path…';
  filter.className = 'vfilter';
  host.append(filter);

  const wrap = el('div', 'vtable');
  const t = el('table');
  t.innerHTML = '<thead><tr><th></th><th>Path</th><th class="num">Size</th>'
              + '<th class="num">Change</th><th>Packing</th></tr></thead>';
  const tb = el('tbody');

  const MODE = { 0: 'stored', 1: 'zlib', 2: 'zlib + AES', 3: 'AES' };
  for (const f of r.files) {
    const tr = el('tr');
    tr.dataset.path = f.path.toLowerCase();

    const badge = el('td');
    badge.append(el('span', 'chg ' + f.change, f.change));
    tr.append(badge);

    tr.append(el('td', 'mono', f.path));
    tr.append(el('td', 'num', f.change === 'removed' ? '—' : bytes(f.size)));

    const d = el('td', 'num delta ' + (f.delta > 0 ? 'up' : f.delta < 0 ? 'down' : ''));
    d.textContent = f.change === 'changed' && f.delta === 0 ? 'same size' : signed(f.delta);
    tr.append(d);

    tr.append(el('td', null, f.change === 'removed' ? '—' : (MODE[f.mode] ?? String(f.mode))));
    tb.append(tr);
  }
  t.append(tb);
  wrap.append(t);
  host.append(wrap);

  const shown = el('div', 'hint');
  shown.textContent = `${num(r.files.length)} shown`;
  host.append(shown);

  let timer;
  filter.oninput = () => {
    clearTimeout(timer);
    timer = setTimeout(() => {
      const q = filter.value.trim().toLowerCase();
      let visible = 0;
      for (const tr of tb.children) {
        const hit = !q || tr.dataset.path.includes(q);
        tr.hidden = !hit;
        if (hit) visible++;
      }
      shown.textContent = `${num(visible)} of ${num(r.files.length)} shown`;
    }, 120);
  };
}

// ---------------- extract ----------------

async function doExtract(depot, version, blobCrc) {
  try {
    await api.post('/api/extract', { depot, version, blobCrc: blobCrc || null, filter: null });
    showTab('extract');
    $('#activity').classList.remove('min');
    pollExtract();
  } catch (e) {
    alert('Extract failed to start: ' + (e.message || e));
  }
}

// ---------------- activity ----------------

async function pollJobs() {
  let jobs;
  try { jobs = await api.get('/api/jobs'); } catch { return; }

  const running = jobs.filter((j) => j.status === 'running').length;
  $('#dlBadge').textContent = running ? running : '';

  const pane = $('#tab-downloads');
  if (!jobs.length) {
    pane.innerHTML = '<div class="muted">No downloads yet. Pick a depot and press “Download chain”.</div>';
    return;
  }

  pane.innerHTML = '';
  for (const j of jobs) pane.append(jobCard(j));
}

function jobCard(j) {
  const card = el('div', 'job');

  const head = el('div', 'jobhead');
  head.append(el('span', 'title', `Depot ${j.depot} · version ${j.version}`));
  head.append(el('span', 'tag mode', j.mode));
  if (j.blobCrc) head.append(el('span', 'tag', 'crc ' + j.blobCrc));
  head.append(el('span', 'spacer'));
  head.append(el('span', 'st ' + j.status, j.status));

  if (j.status === 'running') {
    const c = el('button', 'ghost', 'Cancel');
    c.onclick = () => api.post(`/api/jobs/${j.id}/cancel`).then(pollJobs);
    head.append(c);
  }
  if (j.status === 'done') {
    const x = el('button', 'ghost', 'Extract now');
    x.onclick = () => doExtract(j.depot, j.version, j.blobCrc);
    head.append(x);
  }
  card.append(head);

  const pct = j.totalBytes > 0 ? Math.min(100, (j.doneBytes / j.totalBytes) * 100) : (j.status === 'done' ? 100 : 0);
  const bar = el('div', 'bar' + (j.status === 'done' ? ' done' : j.status === 'failed' ? ' failed' : ''));
  const fill = el('i');
  fill.style.width = pct + '%';
  bar.append(fill);
  card.append(bar);

  const meta = el('div', 'jobmeta');
  for (const t of [
    `${bytes(j.doneBytes)} / ${bytes(j.totalBytes)}`,
    `${j.doneFiles + j.skippedFiles} / ${j.totalFiles} files`,
    j.skippedFiles ? `${j.skippedFiles} already had` : '',
    j.failedFiles ? `${j.failedFiles} failed` : '',
    j.status === 'running' ? rate(j.speedBps) : '',
  ]) if (t) meta.append(el('span', null, t));
  card.append(meta);

  if (j.active?.length) {
    const files = el('div', 'files');
    for (const f of j.active) {
      const line = el('div', 'fline');
      line.append(el('span', 'nm', f.name));
      const b = el('div', 'bar');
      const i = el('i');
      i.style.width = (f.total > 0 ? (f.done / f.total) * 100 : 0) + '%';
      b.append(i);
      line.append(b);
      line.append(el('span', null, bytes(f.done)));
      files.append(line);
    }
    card.append(files);
  }

  if (j.log?.length) {
    const log = el('pre', 'log', j.log.slice(-40).join('\n'));
    card.append(log);
  }

  return card;
}

async function pollExtract() {
  let runs;
  try { runs = await api.get('/api/extract'); } catch { return; }

  const running = runs.filter((r) => r.status === 'running').length;
  $('#exBadge').textContent = running ? running : '';

  const pane = $('#tab-extract');
  if (!runs.length) {
    pane.innerHTML = '<div class="muted">Nothing extracted yet. extract.exe is fetched from the mirror on first use.</div>';
    return;
  }

  pane.innerHTML = '';
  for (const r of runs) {
    const card = el('div', 'job');

    const head = el('div', 'jobhead');
    head.append(el('span', 'title', `Depot ${r.depot} · version ${r.version}`));
    if (r.blobCrc) head.append(el('span', 'tag', 'crc ' + r.blobCrc));
    head.append(el('span', 'spacer'));
    head.append(el('span', 'st ' + r.status, r.status));

    if (r.status === 'running') {
      const c = el('button', 'ghost', 'Cancel');
      c.onclick = () => api.post(`/api/extract/${r.id}/cancel`).then(pollExtract);
      head.append(c);
    } else {
      const o = el('button', 'ghost', 'Open folder');
      o.onclick = () => api.post('/api/reveal', { path: r.outDir });
      head.append(o);
    }
    card.append(head);

    const p = r.progress ?? {};
    const pct = p.totalFiles > 0 ? ((p.doneFiles + p.failedFiles) / p.totalFiles) * 100 : 0;
    const bar = el('div', 'bar' + (r.status === 'done' ? ' done' : r.status === 'failed' ? ' failed' : ''));
    const fill = el('i');
    fill.style.width = pct + '%';
    bar.append(fill);
    card.append(bar);

    const meta = el('div', 'jobmeta');
    for (const t of [
      `${num(p.doneFiles)} / ${num(p.totalFiles)} files`,
      p.failedFiles ? `${p.failedFiles} failed` : '',
      bytes(p.bytesWritten) + ' written',
      p.current || '',
    ]) if (t) meta.append(el('span', null, t));
    card.append(meta);

    card.append(el('pre', 'log', (r.log ?? []).slice(-200).join('\n')));
    pane.append(card);
  }
}

function showTab(name) {
  for (const b of document.querySelectorAll('.acttabs button[data-tab]')) b.classList.toggle('on', b.dataset.tab === name);
  for (const p of document.querySelectorAll('.tabpane')) p.classList.toggle('on', p.id === 'tab-' + name);
}

// ---------------- wiring ----------------

let searchTimer;
$('#depotSearch').oninput = (e) => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => { state.depots.q = e.target.value.trim(); resetDepots(); }, 180);
};
$('#depotSort').onchange = (e) => { state.depots.sort = e.target.value; resetDepots(); };
$('#depotDir').onclick = (e) => {
  state.depots.dir = state.depots.dir === 'asc' ? 'desc' : 'asc';
  e.target.textContent = state.depots.dir === 'asc' ? '↑' : '↓';
  resetDepots();
};
for (const b of document.querySelectorAll('#depotFilters button')) {
  b.onclick = () => {
    for (const o of document.querySelectorAll('#depotFilters button')) o.classList.remove('on');
    b.classList.add('on');
    state.depots.filter = b.dataset.filter;
    resetDepots();
  };
}
$('#depotList').onscroll = (e) => {
  const n = e.target;
  if (n.scrollTop + n.clientHeight > n.scrollHeight - 400) loadDepots();
};

$('#mirrorSelect').onchange = async (e) => {
  await api.post('/api/settings', { mirrorId: e.target.value });
  refreshState();
};
$('#testMirrors').onclick = async (e) => {
  e.target.disabled = true;
  e.target.textContent = 'Testing…';
  try { await api.post('/api/mirrors/test'); } catch { /* results show as unreachable */ }
  e.target.disabled = false;
  e.target.textContent = 'Test speed';
  refreshState();
};

for (const b of document.querySelectorAll('.acttabs button[data-tab]')) b.onclick = () => showTab(b.dataset.tab);
$('#actToggle').onclick = () => {
  const a = $('#activity');
  a.classList.toggle('min');
  $('#actToggle').textContent = a.classList.contains('min') ? '▴' : '▾';
};

$('#openSettings').onclick = () => {
  const s = state.settings ?? {};
  $('#setDataDir').value = s.dataDir ?? '';
  $('#setExtractOut').value = s.extractOutDir ?? '';
  $('#setConcurrency').value = s.concurrency ?? 8;
  $('#setTorrentPort').value = s.torrentPort ?? 0;
  $('#setPhased').checked = s.phasedDownloads !== false;
  $('#setBlobStreams').value = s.blobConcurrency ?? 32;
  $('#setDatStreams').value = s.datConcurrency ?? 2;
  $('#setWarmAhead').value = s.warmupLookahead ?? 2;
  $('#setBigFileMb').value = Math.round((s.bigFileBytes ?? 31457280) / 1048576);
  $('#setVerify').checked = !!s.verifyHashes;
  $('#setFailover').checked = !!s.failover;
  $('#settingsDlg').showModal();
};
$('#saveSettings').onclick = async () => {
  await api.post('/api/settings', {
    dataDir: $('#setDataDir').value,
    extractOutDir: $('#setExtractOut').value,
    concurrency: +$('#setConcurrency').value,
    torrentPort: +$('#setTorrentPort').value,
    phasedDownloads: $('#setPhased').checked,
    blobConcurrency: +$('#setBlobStreams').value,
    datConcurrency: +$('#setDatStreams').value,
    warmupLookahead: +$('#setWarmAhead').value,
    bigFileMb: +$('#setBigFileMb').value,
    verifyHashes: $('#setVerify').checked,
    failover: $('#setFailover').checked,
  });
  refreshState();
};
$('#checkUpdate').onclick = async (e) => {
  e.target.disabled = true;
  e.target.textContent = 'Checking…';
  try { await api.post('/api/update/check'); } catch { /* the status line carries the reason */ }
  e.target.disabled = false;
  e.target.textContent = 'Check now';
  refreshState();
};
$('#reloadIndex').onclick = () => api.post('/api/index/reload', { refresh: true, sizes: true });
$('#reloadSizes').onclick = () => api.post('/api/index/sizes');

// ---------------- loop ----------------

refreshState();
pollJobs();
pollExtract();
setInterval(refreshState, 2000);
setInterval(pollJobs, 1000);
setInterval(pollExtract, 1500);
