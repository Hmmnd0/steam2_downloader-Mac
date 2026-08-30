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
  d.append(versionsPanel(versions));
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
    const files = [...entry.blobs, ...entry.dats];
    const date = files.map((f) => f.date).filter(Boolean).sort().at(-1);
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

  const crcLabel = el('label', null, 'Blob CRC');
  const crcSel = el('select');
  crcSel.id = 'planCrc';
  crcSel.append(new Option('auto', ''));

  const planBtn = el('button', 'ghost', 'Plan');
  const dlBtn = el('button', 'primary', 'Download chain');
  const exBtn = el('button', 'ghost', 'Extract');

  row.append(vLabel, vSel, crcLabel, crcSel, planBtn, dlBtn, exBtn);
  body.append(row);

  const out = el('div');
  out.id = 'planOut';
  body.append(out);
  p.append(body);

  const fillCrc = () => {
    const v = +vSel.value;
    const entry = state.detail.versions.find((x) => x.version === v);
    crcSel.innerHTML = '';
    crcSel.append(new Option('auto', ''));
    for (const b of entry?.blobs ?? []) {
      crcSel.append(new Option(`${b.crc}  ·  ${b.date ?? ''}`, b.crc));
    }
    // Only a forked version leaves a real choice to make.
    crcSel.disabled = (entry?.blobs?.length ?? 0) < 2;
  };
  vSel.onchange = fillCrc;
  fillCrc();

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

function versionsPanel(versions) {
  const p = el('div', 'panel');
  p.append(el('h3', null, `Versions and dates — ${versions.length} present`));

  const wrap = el('div', 'vtable');
  const t = el('table');
  t.innerHTML = '<thead><tr><th class="num">Ver</th><th>Kind</th><th>CRC</th><th>Date</th><th class="num">Size</th><th>Local</th><th>sha256</th></tr></thead>';
  const tb = el('tbody');

  for (const v of versions) {
    const rows = [...v.blobs.map((b) => ['blob', b]), ...v.dats.map((d) => ['dat', d])];
    for (const [kind, f] of rows) {
      const tr = el('tr');
      tr.append(el('td', 'num', String(v.version)));
      tr.append(el('td', null, kind));
      tr.append(el('td', 'mono', f.crc));
      tr.append(el('td', null, f.date ?? '—'));
      tr.append(el('td', 'num', bytes(f.size)));
      const local = el('td');
      local.innerHTML = `<span class="dot${f.local ? ' have' : ''}"></span>`;
      tr.append(local);
      tr.append(el('td', 'mono', f.sha.slice(0, 16) + '…'));
      tb.append(tr);
    }
  }

  t.append(tb);
  wrap.append(t);
  p.append(wrap);
  return p;
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
  $('#setVerify').checked = !!s.verifyHashes;
  $('#setFailover').checked = !!s.failover;
  $('#settingsDlg').showModal();
};
$('#saveSettings').onclick = async () => {
  await api.post('/api/settings', {
    dataDir: $('#setDataDir').value,
    extractOutDir: $('#setExtractOut').value,
    concurrency: +$('#setConcurrency').value,
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
