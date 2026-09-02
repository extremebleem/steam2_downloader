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

// Decimal units: a MB is 10^6 bytes. The mirrors quote MiB in their listings and the parser still
// reads that, but nothing is shown to a reader under a unit name that does not mean what it says.
function bytes(n) {
  if (n == null || n < 0) return '—';
  const u = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  let v = n, i = 0;
  while (v >= 1000 && i < u.length - 1) { v /= 1000; i++; }
  return `${v < 10 && i > 0 ? v.toFixed(2) : v < 100 ? v.toFixed(1) : Math.round(v)} ${u[i]}`;
}

// Dates arrive as "2003-09-10" or a full timestamp and are rendered in whatever the reader's
// browser is set to, so a Russian locale gets 10.09.2003 and a US one 9/10/2003.
//
// Built from the parts rather than through Date.parse: an ISO date alone is read as UTC midnight,
// which in any negative-offset timezone formats as the day before.
function fmtDate(value) {
  if (!value) return '—';

  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(value));
  if (!m) return String(value);

  const d = new Date(+m[1], +m[2] - 1, +m[3]);
  if (Number.isNaN(d.getTime())) return String(value);

  // The month is spelled, not numbered. A purely numeric locale format is ambiguous — 01.02.2011
  // is January in the United States and February in Britain, and nothing in the string says which
  // — and year-first locales such as Korean and Hungarian render 2011. 12. 27., which readers of
  // other locales take for a mistake. A named month cannot be misread in any of them.
  return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
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
  // Activity panel: it follows the work by itself, and a click takes control until the work
  // state changes again.
  act: { jobs: false, extract: false, busy: false, manualOpen: null, jobList: [], extractList: [], installList: [], cancelling: new Set() },
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
  state.disk = s.disk;
  renderDisk(s.disk);
  $('#loadStatus').textContent = s.status.error ? `error: ${s.status.error}` : s.status.message || s.status.phase;
  applyLoading(s.status);
  maybeTellAboutSharing();

// Nothing on this page means anything until the index is in memory: the depot list is empty, the
// file search has nothing to match against and a blob range has no depot ids to resolve. So the
// controls that would only disappoint are held shut, and the bar says work is happening.
//
// A phase that reports no percentage is not the same as one that is 0% done — an index being
// fetched cannot say how far along it is. That case gets a moving bar rather than an empty one,
// which otherwise looks identical to nothing happening at all.
function applyLoading(status) {
  const bar = $('#loadBar');
  const fill = $('#loadBar i');
  const done = status.phase === 'ready';
  const known = status.percent > 0;

  bar.classList.toggle('indeterminate', !done && !known);
  fill.style.width = done ? '0%' : (known ? status.percent + '%' : '');

  // Idle, the name is just a wordmark. A status worth reading pushes it up and shrinks it, so the
  // header itself shows that something is running without needing a separate indicator.
  const brand = $('#brand');
  if (brand) brand.classList.toggle('busy', !done);

  const why = status.error
    ? `unavailable: ${status.error}`
    : 'available once the index has loaded';

  for (const sel of ['#blobRange', '#fileSearch', '#depotSearch', '#depotSort', '#depotDir']) {
    const elm = $(sel);
    if (!elm) continue;

    elm.disabled = !status.ready;
    // Keeping the real title back means it returns intact rather than being overwritten.
    if (!status.ready) {
      if (elm.dataset.title === undefined) elm.dataset.title = elm.title ?? '';
      elm.title = why;
    } else if (elm.dataset.title !== undefined) {
      elm.title = elm.dataset.title;
      delete elm.dataset.title;
    }
  }

  const chips = $('#depotFilters');
  if (chips) chips.classList.toggle('locked', !status.ready);
}

  const c = s.catalog;
  if (c) {
    // What the archive is, in two numbers. Dats and blobs, resets, incomplete depots, naming and
    // the Steam lookup all used to sit here too, and between them they took most of the header —
    // room the sharing button needs to name the stage it is on. The counts that were dropped are
    // either visible where they matter (a depot's own card says if it is incomplete or a reset) or
    // were never something to act on. The two background passes keep their progress in the tooltip.
    const passes = [
      s.names.running ? `naming depots — ${num(s.names.remaining)} left` : '',
      s.steam.running ? `asking Steam — ${num(s.steam.remaining)} left` : '',
    ].filter(Boolean).join('  ·  ');

    renderStats([
      ['depots', num(c.depots), 'depots', s.names.running || s.steam.running,
        passes || `${num(c.dats)} dats and ${num(c.blobs)} blobs, ${num(c.resetDepots)} depots `
                + `with a reset, ${num(c.incompleteDepots)} incomplete, `
                + `${num(s.names.named)} named`],
      ['size', c.sizesLoaded ? '~' + bytes(c.totalBytes) : '…', 'size', false,
        c.sizesLoaded ? 'Total across every version of every depot, from the mirror listings.' : ''],
    ]);

    maybeRefreshNames(s.names.named);
  }

  // The swarm is a mirror you can pick, but only while the engine that serves it is switched on —
  // and that is a separate setting. Greying it out says so where the choice is made, rather than
  // letting it be picked and quietly answered by HTTP.
  const sel = $('#mirrorSelect');
  const engineOff = s.settings && !s.settings.torrentEnabled;
  const want = s.mirrors.map((m) => m.id + (m.speedBps > 0 ? Math.round(m.speedBps) : '')).join('|')
             + (engineOff ? '|off' : '');
  if (sel.dataset.sig !== want) {
    sel.dataset.sig = want;
    sel.innerHTML = '';
    for (const m of s.mirrors) {
      const o = el('option');
      o.value = m.id;
      const dead = m.isTorrent && engineOff;
      const speed = dead ? ' — engine off'
        : m.speedBps > 0 ? ` — ${rate(m.speedBps)}`
        : m.reachable === false ? ' — unreachable' : '';
      o.textContent = `${m.name} (${m.id})${speed}`;
      o.selected = m.active;
      o.disabled = dead;
      // Browsers are inconsistent about hovering a disabled option, so the reason is in the label
      // as well as the tooltip.
      if (dead) o.title = 'The BitTorrent engine is switched off in Settings, so the swarm cannot '
                        + 'be used as a source. Turn it back on there to pick this.';
      sel.append(o);
    }
  }

  if (s.fileSearch) {
    state.fileIndex = s.fileSearch.running
      ? `indexing ${num(s.fileSearch.depotsIndexed)}/${num(s.fileSearch.depotsToIndex)} depots…`
      : (s.fileSearch.pathCount ? `${num(s.fileSearch.pathCount)} paths indexed` : '');
    if (!state.searching) $('#fileSearchNote').textContent = state.fileIndex;
  }

  renderUpdate(s.update);

  if (!state.ready && s.status.ready) {
    state.ready = true;
    resetDepots();

    const first = viewFromHash();
    history.replaceState(first, '', viewUrl(first));
    if (first.depot != null || first.q) applyView(first);
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
// Room left where downloads land. Kept current from the same poll as everything else, so a disk
// filling up during a long download is visible while it happens rather than after it fails.
function renderDisk(d) {
  const box = $('.diskbox');
  if (!box) return;

  const root = $('#diskRoot');
  const free = $('#diskFree');
  const fill = $('#diskFill');
  const text = $('#diskText');

  if (!d || d.error || !d.total) {
    box.classList.remove('low', 'full');
    root.textContent = d?.root || '';
    free.textContent = 'unknown';
    fill.style.width = '0%';
    text.textContent = d?.error
      ? `Free space could not be read: ${d.error}. Downloads are not blocked by this.`
      : '';
    return;
  }

  const usedPct = Math.min(100, Math.max(0, (d.used / d.total) * 100));
  const room = d.free - (d.headroom ?? 0);

  root.textContent = d.root;
  free.textContent = `${bytes(d.free)} free`;
  fill.style.width = `${usedPct.toFixed(1)}%`;
  text.textContent = `${bytes(d.used)} of ${bytes(d.total)} used  ·  `
    + `${bytes(Math.max(0, room))} usable for downloads, `
    + `with ${bytes(d.headroom ?? 0)} kept free`;

  box.classList.toggle('full', room <= 0);
  box.classList.toggle('low', room > 0 && usedPct >= 90);
}

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

  cells.forEach(([, value, label, busy, title], i) => {
    const cell = host.children[i];
    const b = cell.firstChild;
    const span = cell.lastChild;
    if (b.textContent !== value) b.textContent = value;
    if (span.textContent !== label) span.textContent = label;
    cell.classList.toggle('busy', !!busy);
    // The counts this block no longer shows are still worth having on hand.
    const want = title ?? '';
    if (cell.title !== want) cell.title = want;
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
  if (x.last) meta.append(el('span', null, fmtDate(x.last)));
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
  pushView({ depot: id });
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

  // SteamDB holds the human-facing record for a depot — which apps own it, its manifests and its
  // history — so that is where someone reading a bare depot id wants to go next. The id stays on
  // the link rather than in a badge of its own, which is all the badge ever said.
  const sdb = el('a', 'sdb', `SteamDB · ${s.id}`);
  sdb.href = `https://steamdb.info/depot/${s.id}`;
  sdb.target = '_blank';
  sdb.rel = 'noreferrer';
  sdb.title = `Open depot ${s.id} on steamdb.info`;
  head.append(sdb);
  d.append(head);

  const sub = el('div', 'dsub');
  for (const t of [
    `${s.versions} version${s.versions === 1 ? '' : 's'} (0–${s.maxVersion})`,
    `${s.dats} dats · ${s.blobs} blobs`,
    s.datBytes ? `~${bytes(s.datBytes + s.blobBytes)}` : 'sizes not loaded',
    s.first && s.last ? `${fmtDate(s.first)} → ${fmtDate(s.last)}` : '',
    s.roots?.length ? `top level: ${s.roots.slice(0, 6).join(', ')}` : '',
    s.hasReset ? 'has a reset' : '',
    !s.complete ? 'incomplete chain' : '',
    s.nameSource === 'steam' ? `named by steam${s.steamType ? ` · ${s.steamType}` : ''}` : '',
    s.nameSource === 'steam' && s.manifestName ? `manifest name: ${s.manifestName}` : '',
  ]) if (t) sub.append(el('span', null, t));
  d.append(sub);

  if (s.needsKey) {
    d.append(note('bad', 'Encrypted, and no key is known for it',
      'This depot really is AES-encrypted and it is not in the key table, so it cannot be unpacked ' +
      'unless you supply the key yourself. Downloading and browsing still work.'));
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
    if (date) label += `  ·  ${fmtDate(date)}`;

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
  // Where a depot was reset, the same version number exists on two branches. The choice is which
  // branch, not which checksum: a CRC identifies the blob exactly but tells the reader nothing
  // about what they would be downloading, while "the newest branch, v0–v56" does.
  const crcLabel = el('label', null, 'Branch');
  const crcSel = el('select');
  crcSel.id = 'planCrc';
  crcSel.append(new Option('auto', ''));

  const sizeInfo = el('span', 'plansize');

  const planBtn = el('button', 'ghost', 'Plan');
  const dlBtn = el('button', 'primary', 'Download chain');
  const exBtn = el('button', 'ghost', 'Extract');

  // Alternative to "Download chain": the browser saves the chain into a folder you pick, laid out
  // the way the extractor expects, instead of it going into the app's own archive folder.
  const webBtn = el('button', 'ghost', 'Download chain using browser');
  webBtn.title = 'Pick a folder; the chain is saved into blobs/ and dats/ inside it';

  // The optimiser leaves out the dats this version never reads, which is usually most of the
  // chain. Someone archiving a depot wants those too, so it can be turned off per download.
  const fullWrap = el('label', 'fullchain');
  const fullBox = el('input');
  fullBox.type = 'checkbox';
  fullBox.id = 'planFullChain';
  fullWrap.append(fullBox, el('span', null, 'All versions'));
  fullWrap.title = 'Download every dat in the chain, including the ones this version does not read';
  fullBox.onchange = () => updateSize();

  row.append(vLabel, vSel, crcLabel, crcSel, fullWrap, sizeInfo, planBtn, dlBtn, exBtn, webBtn);
  body.append(row);

  const out = el('div');
  out.id = 'planOut';
  body.append(out);
  p.append(body);

  const fillCrc = () => {
    const v = +vSel.value;
    const entry = state.detail.versions.find((x) => x.version === v);
    const choices = entry?.blobs ?? [];

    const branches = state.detail.branches ?? [];

    crcSel.innerHTML = '';
    // Newest branch first, matching the version history above, so "the first one" means the same
    // thing in both places.
    const sorted = [...choices].sort((a, b) => (a.branch ?? 99) - (b.branch ?? 99));
    crcSel.append(new Option(sorted.length > 1 ? 'newest branch' : 'auto', ''));

    for (const b of sorted) {
      const info = b.branch != null ? branches[b.branch] : null;
      // The date is what separates two branches at a glance — the same version number written
      // twice, years apart. The CRC stays in the tooltip for anyone who came for it.
      const label = info
        ? `Branch ${b.branch + 1} — v${info.minVersion}–v${info.maxVersion}`
          + (b.date ? `, ${fmtDate(b.date)}` : '')
        : `${b.crc}${b.date ? `  ·  ${fmtDate(b.date)}` : ''}`;

      const opt = new Option(label, b.crc);
      opt.title = info
        ? `${info.blobCount} versions, `
          + (info.forksFromVersion != null ? `forks from v${info.forksFromVersion}` : 'its own root')
          + `  ·  blob CRC ${b.crc}`
        : `blob CRC ${b.crc}`;
      crcSel.append(opt);
    }

    // Nothing to pick unless the version forked, so the control stays out of the way entirely.
    const choose = choices.length > 1;
    crcLabel.hidden = !choose;
    crcSel.hidden = !choose;
  };

  // A chain that does not fit fails part way through, after however many hours it took to get
  // there, and leaves a disk with nothing left on it. The button says so beforehand instead.
  //
  // A drive that could not be measured counts as having room: the server refuses the download
  // anyway if it turns out not to, and a reporting failure should not lock the app.
  const applySpaceGuard = (need) => {
    const d = state.disk;
    if (!d || d.error || typeof d.free !== 'number') {
      dlBtn.disabled = false;
      dlBtn.title = '';
      return;
    }

    const room = d.free - (d.headroom ?? 0);
    dlBtn.disabled = need > room;
    dlBtn.title = need > room
      ? `Not enough free space on ${d.root} — ${bytes(need)} to download and `
        + `${bytes(d.headroom ?? 0)} kept free, but only ${bytes(d.free)} is available. `
        + `Free some space, or point the download directory at another drive in Settings.`
      : '';
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
    applySpaceGuard(left);

    refineSize(s.id, target);
  };

  // The figure above counts the whole chain, which is what a delta format costs at worst. The real
  // cost is usually lower: a dat whose every written file was overwritten again before the target
  // holds nothing it reads. That is only answerable from the blobs, so it is asked for separately
  // and folded in when it arrives rather than holding up the first number.
  let sizeToken = 0;

  async function refineSize(depot, target) {
    const mine = ++sizeToken;

    // With the optimiser off the whole chain is fetched, so the first estimate is already the
    // right one and refining it down would understate what the download is about to do.
    if ($('#planFullChain')?.checked) return;

    let r;
    try { r = await api.get(`/api/depots/${depot}/needed?version=${target}`); }
    catch { return; }

    // The version changed while this was in flight, so the answer is to the wrong question.
    if (mine !== sizeToken || !r.resolved) return;

    const keep = new Set(r.needed);
    let total = 0, have = 0, files = 0, unknown = 0;

    for (const entry of state.detail.versions) {
      if (entry.version > target) continue;

      // Every blob is needed — the file id table is built from all of them — but only the dats
      // that some file actually resolves to.
      const wanted = [...entry.blobs, ...(keep.has(entry.version) ? entry.dats : [])];

      for (const f of wanted) {
        files++;
        if (f.size >= 0) {
          total += f.size;
          if (f.local) have += f.size;
        } else {
          unknown++;
        }
      }
    }

    const skipped = r.chainVersions - r.neededVersions;
    const left = Math.max(0, total - have);

    const parts = [`${unknown ? '≥' : '~'}${bytes(left)} to download`];
    if (have > 0) parts.push(`${bytes(have)} already here`);
    parts.push(`${num(files)} files`);
    if (skipped > 0) parts.push(`${num(skipped)} of ${num(r.chainVersions)} dats not needed`);

    sizeInfo.textContent = parts.join('  ·  ');
    sizeInfo.title = skipped > 0
      ? `only versions ${r.needed.join(', ')} carry bytes v${target} reads`
      : `chain v0…v${target}: every dat is needed`;

    // The refined figure is smaller than the first estimate, so a chain the guard had blocked can
    // become one that fits.
    applySpaceGuard(left);
  }

  const onVersion = () => { fillCrc(); updateSize(); };
  vSel.onchange = onVersion;
  onVersion();

  planBtn.onclick = () => doPlan(s.id, +vSel.value, crcSel.value, false);
  dlBtn.onclick = () => doPlan(s.id, +vSel.value, crcSel.value, true);
  exBtn.onclick = () => doExtract(s.id, +vSel.value, crcSel.value);
  webBtn.onclick = () => doBrowserChain(s.id, +vSel.value, crcSel.value);

  return p;
}

async function doPlan(depot, version, blobCrc, download) {
  const out = $('#planOut');
  out.innerHTML = '<div class="muted">resolving chain…</div>';

  try {
    const res = await api.post(download ? '/api/download' : '/api/plan', {
      depot, version, blobCrc: blobCrc || null,
      fullChain: !!$('#planFullChain')?.checked,
    });
    const plan = res.plan ?? res;
    state.plan = plan;
    renderPlan(plan, out);
    if (res.jobId) {
      state.act.manualOpen = true;
      applyActivity(true);
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
    out.append(note('info', 'Pick a branch',
      'This depot was reset, so this version number exists on more than one branch. Choose which '
      + 'one above, then plan again.'));
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

  // Saying what was left out matters as much as the total: a chain that looks suspiciously cheap
  // should explain itself rather than leave the reader wondering what went missing.
  if (plan.skippedDats > 0) {
    modeLine.append(el('span', null,
      `${num(plan.skippedDats)} of ${num(plan.chainDats)} dats skipped — nothing in v${plan.version} reads them, saving ${bytes(plan.skippedBytes)}`));
  } else if (plan.skippedDats === null || plan.skippedDats === undefined) {
    modeLine.append(el('span', null,
      'download the blobs to find out which dats this version actually reads'));
  }

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

  renderHistory(depotId, r);

  if (r.fetch.running || keepPolling) {
    setTimeout(() => pollHistory(depotId, r.fetch.running), 1000);
  }
}

function renderHistory(depotId, data) {
  const h = state.history;
  const list = h.list;
  const versions = data.versions;
  const branches = data.branches || [];

  // Rebuild only when the shape changed, so an open section is not collapsed under the user.
  const sig = versions.map((v) => `${v.branch}/${v.version}/${v.crc}/${v.local ? v.changedCount : 'x'}`).join('|');
  if (list.dataset.sig === sig) return;
  list.dataset.sig = sig;

  list.innerHTML = '';

  // A reset restarts the numbering, so "first release" is the earliest version of its own branch —
  // a depot can genuinely have several, one per line of descent.
  const earliestIn = new Map();
  for (const v of versions) {
    const cur = earliestIn.get(v.branch);
    if (cur === undefined || v.version < cur) earliestIn.set(v.branch, v.version);
  }

  let lastBranch = null;

  for (const v of versions) {
    if (branches.length > 1 && v.branch !== lastBranch) {
      lastBranch = v.branch;
      list.append(branchHeader(branches[v.branch], v.branch, branches.length));
    }

    const key = `${v.branch}/${v.version}/${v.crc}`;
    const d = el('details', 'vitem');
    if (h.open.has(key)) d.open = true;

    const sm = el('summary');
    sm.append(el('b', 'vver', 'v' + v.version));
    sm.append(el('span', 'vdate', fmtDate(v.date)));

    if (v.error) {
      sm.append(el('span', 'vwhat bad', v.error.slice(0, 60)));
    } else if (!v.local) {
      sm.append(el('span', 'vwhat dim', 'blob not downloaded'));
    } else if (v.version === earliestIn.get(v.branch)) {
      sm.append(el('span', 'vwhat', `${num(v.addedCount)} files · first release`));
      sm.append(el('span', 'vdelta up', signed(v.deltaBytes)));
      sm.append(el('span', 'vsize', bytes(v.payloadBytes)));
    } else if (v.unclassified) {
      // Without the predecessor's blob there is no telling a new file from a rewritten one.
      sm.append(el('span', 'vwhat dim',
        `${num(v.changedCount)} in this version · predecessor unknown`));
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

    // Straight-to-browser saves for a single file. Deliberately quiet, and they must not toggle
    // the section they sit in.
    for (const [label, url, size] of [['blob', v.blobUrl, v.blobBytes], ['dat', v.datUrl, v.datBytes]]) {
      if (!url) continue;
      const a = el('a', 'vget', label);
      a.href = url;
      a.setAttribute('download', '');
      a.title = `save the ${label}${size > 0 ? ' — ' + bytes(size) : ''}`;
      a.onclick = (e) => e.stopPropagation();
      sm.append(a);
    }

    d.append(sm);

    const inner = el('div', 'vbody');
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

// Depots that were reset carry several independent chains that all count from v0. Without a header
// per chain the history interleaves them and every second row claims to be a first release.
function branchHeader(b, index, total) {
  const head = el('div', 'vbranch');
  head.append(el('b', null, `Branch ${index + 1} of ${total}`));

  const span = b ? `v${b.minVersion}–v${b.maxVersion}` : '';
  const dates = b && b.firstDate ? `${fmtDate(b.firstDate)} → ${fmtDate(b.lastDate ?? b.firstDate)}` : '';
  const forks = b && b.forksFromVersion != null ? `continues from v${b.forksFromVersion}` : 'own root';

  for (const t of [span, dates, `${b ? b.blobCount : 0} versions`, forks]) {
    if (t) head.append(el('span', null, t));
  }
  return head;
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

// Saves a whole chain into a folder of the user's choosing, using the browser's own file access
// rather than the app's download queue. The layout matches what the extractor expects — blobs/ and
// dats/ side by side — so once it finishes, Extract has everything it needs.
//
// The bytes are relayed by this app rather than fetched from the mirror by the page: the mirrors
// send no Access-Control-Allow-Origin and refuse OPTIONS, so a cross-origin read is impossible.
async function doBrowserChain(depot, version, blobCrc) {
  const out = $('#planOut');

  if (!window.showDirectoryPicker) {
    out.innerHTML = '';
    out.append(note('warn', 'This browser cannot save into a folder',
      'Choosing a folder needs the File System Access API, which Chrome, Edge and Opera have and ' +
      'Firefox and Safari do not. Use "Download chain" instead — that one saves into the ' +
      'download directory the app already uses.'));
    return;
  }

  out.innerHTML = '<div class="muted">resolving chain…</div>';

  let plan;
  try {
    plan = await api.post('/api/plan', { depot, version, blobCrc: blobCrc || null });
    plan = plan.plan ?? plan;
  } catch (e) {
    out.innerHTML = '';
    out.append(note('bad', 'Cannot build the chain', e.message || String(e)));
    return;
  }

  if (plan.error || plan.needsChoice) { renderPlan(plan, out); return; }

  let root;
  try {
    root = await window.showDirectoryPicker({ mode: 'readwrite', id: 'steam2chain' });
  } catch {
    out.innerHTML = '';
    out.append(note('info', 'Cancelled', 'No folder was chosen, so nothing was downloaded.'));
    return;
  }

  // Both subfolders are made up front, so the folder is already in the shape the extractor wants
  // even if the download is interrupted half way.
  const dirs = {};
  for (const name of ['blobs', 'dats']) {
    dirs[name] = await root.getDirectoryHandle(name, { create: true });
  }

  out.innerHTML = '';
  const head = el('div', 'dsub');
  head.textContent = `Saving ${num(plan.files.length)} file(s) into ${root.name}/`;
  const bar = el('div', 'rangeprog');
  const track = el('div', 'bar');
  const fill = el('i');
  track.append(fill);
  const line = el('span', 'hint', 'starting…');
  bar.append(track, line);
  out.append(head, bar);

  let done = 0, skipped = 0, failed = 0, saved = 0;
  const total = plan.files.length;

  for (const f of plan.files) {
    const dir = dirs[f.dir] ?? dirs[f.kind === 'dat' ? 'dats' : 'blobs'];

    try {
      // A file already the right size is left alone, which makes the whole thing resumable:
      // point it at the same folder again and it picks up where it stopped.
      if (f.size > 0) {
        try {
          const existing = await (await dir.getFileHandle(f.name)).getFile();
          if (existing.size === f.size) { skipped++; done++; continue; }
        } catch { /* not there yet */ }
      }

      const res = await fetch(`/api/file/${f.kind === 'dat' ? 'dats' : 'blobs'}/${encodeURIComponent(f.name)}`);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);

      const handle = await dir.getFileHandle(f.name, { create: true });
      const writable = await handle.createWritable();
      await res.body.pipeTo(writable);

      saved += f.size > 0 ? f.size : 0;
      done++;
    } catch (e) {
      failed++;
      done++;
    }

    fill.style.width = `${Math.round((done / total) * 100)}%`;
    line.textContent = `${num(done)} / ${num(total)}`
      + (skipped ? `  ·  ${num(skipped)} already there` : '')
      + (failed ? `  ·  ${num(failed)} failed` : '');
  }

  fill.style.width = '100%';
  line.textContent = `${num(done - failed)} of ${num(total)} file(s) in ${root.name}/`
    + (saved ? `  ·  ${bytes(saved)} written` : '')
    + (failed ? `  ·  ${num(failed)} failed` : '');

  out.append(extractHint(root.name));
}

// Extract runs in the app, not in the page, and the page is never told where the folder it just
// wrote to actually lives — the picker hands out a handle, not a path. So when the chosen folder
// is not the one the app already reads from, it has to be pointed at it.
function extractHint(folderName) {
  const current = state.settings?.dataDir ?? '';
  const parts = current.replace(/[\\/]+$/, '').split(/[\\/]/);

  if (parts.length && parts[parts.length - 1].toLowerCase() === folderName.toLowerCase()) {
    return note('info', 'Ready to extract',
      `This is the folder the app already downloads into (${current}), so Extract will find the chain.`);
  }

  const box = note('info', 'One step before Extract',
    'Extract reads whichever download directory the app is set to, and a browser never reveals '
    + 'the real path of a '
    + `folder you pick. Give the full path of "${folderName}" to point the app at it.`);

  const row = el('div', 'dlgrow');
  const input = el('input');
  input.type = 'text';
  input.style.flex = '1';
  // A sibling of the current download directory is the likely spot, so it is worth guessing.
  input.value = parts.length > 1
    ? parts.slice(0, -1).concat(folderName).join('\\')
    : folderName;

  const use = el('button', 'primary', 'Use this folder');
  use.onclick = async () => {
    use.disabled = true;
    try {
      await api.post('/api/settings', { dataDir: input.value });
      box.replaceChildren(el('b', null, 'Ready to extract'),
        document.createTextNode(`The app now reads from ${input.value}. Press Extract.`));
      refreshState();
    } catch (e) {
      use.disabled = false;
      alert('Could not set the folder: ' + (e.message || e));
    }
  };

  row.append(input, use);
  box.append(row);
  return box;
}

async function doExtract(depot, version, blobCrc) {
  try {
    await api.post('/api/extract', { depot, version, blobCrc: blobCrc || null, filter: null });
    state.act.manualOpen = true;
    applyActivity(true);
    pollExtract();
  } catch (e) {
    alert('Extract failed to start: ' + (e.message || e));
  }
}

// ---------------- activity ----------------

// The backend writes plain sentences (Downloader.Say, Extractor.Say, Installs.Say), not levelled
// log records (error, cancelled), so this reads the same fixed phrasing those methods already use rather than
// inventing a protocol. Update this if those messages change.
function logLevel(line) {
  const msg = line.replace(/^\d{2}:\d{2}:\d{2}\s+/, ''); // drop the "HH:mm:ss  " timestamp

  if (/\bFAILED\b/.test(msg)) return 'err';               // "FAILED <file>: <error>"
  if (/\bfailed[:—]/i.test(msg)) return 'err';             // "failed: <error>" / "failed — <error>"
  if (/\bwith \d+ (failure|failed)/i.test(msg)) return 'err'; // "finished with N failure(s)/failed depot(s)"

  const doneCounts = /^done — .*?(\d+) failed/i.exec(msg); // "done — N files, M failed, ..."
  if (doneCounts) return +doneCounts[1] > 0 ? 'err' : 'ok';

  if (/^finished —/.test(msg) || /^installed into\b/.test(msg) || /: \d+ file\(s\)$/.test(msg)) return 'ok';

  if (/\bcancelled\b/.test(msg) || /helper stopped:/.test(msg) || /not in the torrent\b/.test(msg)
    || /off in Settings\b/.test(msg) || /\bno key\b/i.test(msg)) return 'warn';

  return '';
}

// One <pre class="log"> whose lines are colour-coded individually, instead of one flat grey block. Makes everything more readable and good-looking.
function renderLog(lines) {
  const pre = el('pre', 'log');
  for (const line of lines) {
    const level = logLevel(line);
    pre.append(el('span', 'logline' + (level ? ' ' + level : ''), line + '\n'));
  }
  return pre;
}

async function pollJobs() {
  let jobs;
  try { jobs = await api.get('/api/jobs'); } catch { return; }

  const running = jobs.filter((j) => j.status === 'running').length;
  setActivityBusy('jobs', running > 0);

  state.act.jobList = jobs;
  renderActivity();
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
    const pending = state.act.cancelling.has(j.id);
    const c = el('button', 'ghost', pending ? 'Cancelling…' : 'Cancel');
    c.disabled = pending;
    c.onclick = () => {
      state.act.cancelling.add(j.id);
      renderActivity();
      api.post(`/api/jobs/${j.id}/cancel`).then(pollJobs);
    };
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

  if (j.log?.length) card.append(renderLog(j.log.slice(-40)));

  return card;
}

async function pollExtract() {
  let runs;
  try { runs = await api.get('/api/extract'); } catch { return; }

  const running = runs.filter((r) => r.status === 'running').length;
  setActivityBusy('extract', running > 0);

  state.act.extractList = runs;
  renderActivity();
}

function extractCard(r) {
  {
    const card = el('div', 'job');

    const head = el('div', 'jobhead');
    head.append(el('span', 'title', `Depot ${r.depot} · version ${r.version}`));
    if (r.blobCrc) head.append(el('span', 'tag', 'crc ' + r.blobCrc));
    head.append(el('span', 'spacer'));
    head.append(el('span', 'st ' + r.status, r.status));

    if (r.status === 'running') {
      const pending = state.act.cancelling.has(r.id);
      const c = el('button', 'ghost', pending ? 'Cancelling…' : 'Cancel');
      c.disabled = pending;
      c.onclick = () => {
        state.act.cancelling.add(r.id);
        renderActivity();
        api.post(`/api/extract/${r.id}/cancel`).then(pollExtract);
      };
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

    card.append(renderLog((r.log ?? []).slice(-200)));
    return card;
  }
}

// Downloads and extractions share one list, newest first: they are two halves of the same job,
// and splitting them across tabs only hid whichever one you were not looking at.
function renderActivity() {
  const host = $('#actList');
  if (!host) return;

  const installs = state.act.installList ?? [];

  // The download and extract of a depot inside an install are its business, not separate rows:
  // the whole point is to see one Counter-Strike: Source rather than four depots.
  const owned = new Set();
  for (const i of installs) {
    for (const s of i.steps) {
      if (s.jobId) owned.add('j' + s.jobId);
      if (s.runId) owned.add('r' + s.runId);
    }
  }

  const jobs = (state.act.jobList ?? []).filter((j) => !owned.has('j' + j.id));
  const runs = (state.act.extractList ?? []).filter((r) => !owned.has('r' + r.id));

  const stillRunning = new Set([
    ...jobs.filter((j) => j.status === 'running').map((j) => j.id),
    ...runs.filter((r) => r.status === 'running').map((r) => r.id),
    ...installs.filter((i) => i.status === 'running').map((i) => i.id),
  ]);
  for (const id of state.act.cancelling) if (!stillRunning.has(id)) state.act.cancelling.delete(id);

  const running = jobs.filter((j) => j.status === 'running').length
                + runs.filter((r) => r.status === 'running').length
                + installs.filter((i) => i.status === 'running').length;
  const badge = $('#actBadge');
  if (badge) badge.textContent = running ? String(running) : '';

  if (!jobs.length && !runs.length && !installs.length) {
    host.innerHTML = '<div class="muted">Nothing yet. Pick a depot and start a download.</div>';
    return;
  }

  const entries = [
    ...installs.map((i) => ({ at: i.started, node: () => installCard(i) })),
    ...jobs.map((j) => ({ at: j.started, node: () => jobCard(j) })),
    ...runs.map((r) => ({ at: r.started, node: () => extractCard(r) })),
  ].sort((a, b) => String(b.at).localeCompare(String(a.at)));

  host.innerHTML = '';
  for (const e of entries) host.append(e.node());
}


// ---------------- activity panel ----------------

// Collapsed while nothing is running, so the depot page gets the screen. It opens on its own when
// a download or an extraction starts, and the arrow overrides that until the work state changes.
function applyActivity(open) {
  const a = $('#activity');
  a.classList.toggle('min', !open);

  const btn = $('#actToggle');
  btn.textContent = open ? '▾' : '▴';
  btn.title = open ? 'Collapse' : 'Expand';
}

function setActivityBusy(kind, value) {
  const a = state.act;
  a[kind] = value;

  const busy = !!(a.jobs || a.extract);
  if (busy && !a.busy) {
    // New work started: take the panel back from a manual collapse so it's visible again.
    a.manualOpen = null;
  } else if (!busy && a.busy) {
    // some users may think that the activity block closing INSTANTLY could mean something went bad, keeping the block up and making the user go down gives the user
    // a better view of what happend and if everything went correctly
    a.manualOpen = true;
  }
  a.busy = busy;

  applyActivity(a.manualOpen ?? busy);
}

// ---------------- global file search ----------------

// Searches paths inside blobs already on disk. Nothing is fetched to answer a query: a depot whose
// blobs have not been downloaded simply is not in the index, and the note under the box says so.
async function runFileSearch(q) {
  const note = $('#fileSearchNote');
  const detail = $('#detail');

  if (q.trim().length < 2) {
    note.textContent = state.fileIndex || '';
    if (state.searching) { state.searching = false; renderDepotOrEmpty(); }
    return;
  }

  state.searching = true;

  let r;
  try { r = await api.get('/api/files/search?' + new URLSearchParams({ q, limit: 300 })); }
  catch { return; }

  if (!state.searching) return;

  // Nothing indexed yet: offer to build it rather than pretending the file does not exist.
  if (!r.indexed && !r.running) {
    detail.innerHTML = '';
    const box = el('div', 'empty');
    box.append(el('h2', null, 'Nothing indexed yet'));
    box.append(el('p', null,
      'File search reads the blobs already on disk. Download some blobs for a depot, then build '
      + 'the index — it costs no network at all.'));
    const btn = el('button', 'primary', 'Index downloaded blobs');
    btn.onclick = async () => {
      btn.disabled = true;
      btn.textContent = 'Indexing…';
      try { await api.post('/api/files/index'); } catch { }
      setTimeout(() => runFileSearch($('#fileSearch').value), 1200);
    };
    box.append(btn);
    detail.append(box);
    return;
  }

  note.textContent = r.running
    ? 'indexing…'
    : `${num(r.hits.length)}${r.truncated ? '+' : ''} of ${num(r.indexed)} paths · ${num(r.depots)} depots`;

  detail.innerHTML = '';

  const head = el('div', 'dhead');
  head.append(el('h2', null, `Files matching “${q.trim()}”`));
  detail.append(head);

  const sub = el('div', 'dsub');
  sub.append(el('span', null,
    `${num(r.hits.length)}${r.truncated ? '+ (capped)' : ''} hit(s) across ${num(r.depots)} indexed depot(s)`));
  sub.append(el('span', null, 'only depots whose blobs are downloaded are searched'));
  detail.append(sub);

  if (!r.hits.length) {
    detail.append(note2('info', 'No match here',
      'The file may live in a depot whose blobs are not downloaded yet — those cannot be searched.'));
    return;
  }

  const open = (depot) => {
    $('#fileSearch').value = '';
    state.searching = false;
    $('#fileSearchNote').textContent = state.fileIndex || '';
    selectDepot(depot);
  };

  // Blobs downloaded after the index was built are simply not in it, so a search can look complete
  // while missing them. Say so where the results are, with the fix one click away.
  const behind = (r.blobsOnDisk ?? 0) - (r.blobsIndexed ?? 0);
  if (behind > 0 && !r.running) {
    const box = note2('warn', 'Index is behind',
      `${num(behind)} blob(s) have been downloaded since this index was built and are not searched yet.`);
    const btn = el('button', 'primary', 'Reindex');
    btn.onclick = async () => {
      btn.disabled = true;
      btn.textContent = 'Indexing…';
      try { await api.post('/api/files/index'); } catch { /* the note below reports the state */ }
      setTimeout(() => runFileSearch($('#fileSearch').value), 1200);
    };
    box.append(btn);
    detail.append(box);
  }

  // One depot, one entry. Hits come back depot by depot, but without grouping a depot with twenty
  // matches printed its id and name twenty times over and buried the paths that actually differ.
  const groups = new Map();
  for (const h of r.hits) {
    let g = groups.get(h.depot);
    if (!g) groups.set(h.depot, g = { name: h.name || '', paths: [] });
    g.paths.push(h.path);
  }

  const list = el('div', 'fhits');
  for (const [depot, g] of groups) {
    const group = el('div', 'fgroup');

    const gh = el('div', 'fghead');
    gh.append(el('b', 'fdepot', 'depot ' + depot));
    if (g.name) gh.append(el('span', 'fname', g.name));
    gh.append(el('span', 'fcount', `${num(g.paths.length)} file${g.paths.length === 1 ? '' : 's'}`));
    gh.onclick = () => open(depot);
    group.append(gh);

    for (const path of g.paths) {
      const row = el('div', 'fhit');
      row.append(el('span', 'fpath', path));
      row.onclick = () => open(depot);
      group.append(row);
    }

    list.append(group);
  }
  detail.append(list);
}

// note() builds a styled callout; aliased so the search code reads clearly next to its own note field.
const note2 = (kind, title, body) => note(kind, title, body);

function renderDepotOrEmpty() {
  if (state.detail) renderDepot();
  else {
    $('#detail').innerHTML =
      '<div class="empty"><h2>Pick a depot</h2><p>Files are stored as deltas: extracting version '
      + '<em>N</em> needs every version below it too.</p></div>';
  }
}

let fileSearchTimer;
$('#fileSearch').oninput = (e) => {
  clearTimeout(fileSearchTimer);
  const q = e.target.value;
  fileSearchTimer = setTimeout(() => {
    // Refining a query replaces its entry: going back should leave the search, not retrace every
    // letter that was typed to build it. Clearing the box likewise rewrites the entry rather than
    // adding one, so back still lands on whatever was open before the search started.
    const refining = !!history.state?.q;
    runFileSearch(q);

    if (q.trim().length >= 2) pushView({ q }, refining);
    else if (refining) pushView(state.selected != null ? { depot: state.selected } : {}, true);
  }, 220);
};

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

$('#actToggle').onclick = () => {
  const open = $('#activity').classList.contains('min');
  state.act.manualOpen = open;
  applyActivity(open);
};

$('#openSettings').onclick = () => {
  const s = state.settings ?? {};
  $('#setDataDir').value = s.dataDir ?? '';
  $('#setExtractOut').value = s.extractOutDir ?? '';
  $('#setConcurrency').value = s.concurrency ?? 8;
  $('#setTorrentPort').value = s.torrentPort ?? 0;
  $('#setUpKbps').value = s.torrentUploadKbps ?? 0;
  $('#setDownKbps').value = s.torrentDownloadKbps ?? 0;
  $('#setPhased').checked = s.phasedDownloads !== false;
  $('#setBlobStreams').value = s.blobConcurrency ?? 32;
  $('#setDatStreams').value = s.datConcurrency ?? 2;
  $('#setWarmAhead').value = s.warmupLookahead ?? 2;
  $('#setBigFileMb').value = Math.round((s.bigFileBytes ?? 30000000) / 1_000_000);
  $('#setTorrent').checked = !!s.torrentEnabled;
  $('#setSeed').checked = !!s.seedDownloaded;
  $('#setSwarm').checked = !!s.swarmAssist;
  $('#setVerify').checked = !!s.verifyHashes;
  $('#setFailover').checked = !!s.failover;
  $('#settingsDlg').showModal();
};
$('#saveSettings').onclick = async () => {
  // Sharing is its own endpoint because switching it has to start or stop the engine, not
  // just record a preference.
  try {
    await api.post('/api/settings', {
      torrentEnabled: $('#setTorrent').checked,
      swarmAssist: $('#setSwarm').checked,
    });
    await api.post('/api/seed', { enabled: $('#setSeed').checked });
  } catch { }
  pollSeed();

  await api.post('/api/settings', {
    dataDir: $('#setDataDir').value,
    extractOutDir: $('#setExtractOut').value,
    concurrency: +$('#setConcurrency').value,
    torrentPort: +$('#setTorrentPort').value,
    torrentUploadKbps: +$('#setUpKbps').value || 0,
    torrentDownloadKbps: +$('#setDownKbps').value || 0,
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

// ---------------- blobs by depot range ----------------

// Blobs are the cheap half of the archive — tens to a few hundred KB each against dats measured in
// gigabytes — and they carry the file lists that the version diffs and the global file search read.
// Pulling a span of depot ids at once is how you make either of those useful without committing to
// the dats. The dialog shows the real byte estimate for the range before anything starts.

let rangePoll = null;

function rangeBounds() {
  const from = parseInt($('#rangeFrom').value, 10);
  const to = parseInt($('#rangeTo').value, 10);
  return Number.isFinite(from) && Number.isFinite(to) ? { from, to } : null;
}

async function refreshRangeInfo() {
  const info = $('#rangeInfo');
  const b = rangeBounds();

  if (!b) { info.textContent = 'Enter a first and last depot id.'; return; }

  try {
    const r = await api.get(`/api/blobs/range?from=${b.from}&to=${b.to}`);
    const p = r.preview;

    if (!p || !p.depots) { info.textContent = 'No depots in that range.'; return; }

    const parts = [`${num(p.depots)} depot(s)`, `${num(p.blobs)} blob(s)`];
    parts.push(p.missing ? `${num(p.missing)} still to fetch` : 'all already on disk');
    if (p.bytes > 0) parts.push(`~${bytes(p.bytes)}`);
    info.textContent = parts.join('  ·  ');
  } catch {
    info.textContent = '';
  }
}

async function pollRange() {
  const r = await api.get('/api/blobs/range');
  const box = $('#rangeProg');
  const btn = $('#rangeStart');

  box.hidden = !(r.running || r.done || r.failed);
  $('#rangeBar').style.width = r.total ? `${Math.round((r.done / r.total) * 100)}%` : '0';
  $('#rangeProgText').textContent = r.running
    ? `${num(r.done)} / ${num(r.total)}${r.failed ? `  ·  ${num(r.failed)} failed` : ''}`
    : (r.message ?? '');

  btn.disabled = r.running;
  btn.textContent = r.running ? 'Downloading…' : 'Download blobs';

  // The search index only sees blobs that were on disk when it was built.
  if (!r.running && rangePoll) {
    clearInterval(rangePoll);
    rangePoll = null;
    refreshRangeInfo();
  }
}

$('#blobRange').onclick = () => {
  const list = state.depots.items ?? [];
  if (!$('#rangeFrom').value && list.length) {
    $('#rangeFrom').value = list[0].id;
    $('#rangeTo').value = list[Math.min(list.length - 1, 99)].id;
  }
  refreshRangeInfo();
  pollRange();
  $('#rangeDlg').showModal();
};

$('#rangeFrom').oninput = refreshRangeInfo;
$('#rangeTo').oninput = refreshRangeInfo;

$('#rangeStart').onclick = async () => {
  const b = rangeBounds();
  if (!b) return;

  await api.post('/api/blobs/range', b);
  if (!rangePoll) rangePoll = setInterval(pollRange, 700);
  pollRange();
};

// ---------------- depot packs ----------------
//
// A depot is not a game. Which depots at which versions add up to one is recorded nowhere in the
// archive — that lived on Steam's side and was never dumped — so it is written by hand in
// apps/*.json and validated against the real catalog before merging. This is the reading end of
// that: pick a build, and every depot it names is queued as its own download.

const store = { apps: [], selected: null, loaded: false, status: null };

function setMode(mode) {
  state.mode = mode;
  for (const b of $('#modes').children) b.classList.toggle('on', b.dataset.mode === mode);

  const browsing = mode === 'depots';
  $('#depotList').hidden = !browsing;
  $('#storeList').hidden = browsing;
  $('#storeHead').hidden = browsing;
  document.querySelector('.listhead').hidden = !browsing;
  document.querySelector('.toolbar').hidden = !browsing;
  document.querySelector('.sortrow').hidden = !browsing;
  $('#depotFilters').hidden = !browsing;

  if (!browsing) loadStore();
}

async function loadStore() {
  if (store.loaded) { renderStoreList(); return; }
  $('#storeCount').textContent = 'loading…';

  let r;
  try {
    r = await api.get('/api/apps');
  } catch {
    $('#storeCount').textContent = 'could not load the app list';
    return;
  }

  store.apps = r.items ?? [];
  store.status = r.status;
  store.loaded = true;
  renderStoreList();
}

function renderStoreList() {
  const host = $('#storeList');
  host.innerHTML = '';

  const st = store.status ?? {};
  $('#storeCount').textContent = store.apps.length
    ? `${num(store.apps.length)} app(s) · ${st.source ?? ''}`
    : (st.message || 'no apps defined yet');

  if (!store.apps.length) {
    const d = $('#detail');
    d.innerHTML = '';
    d.append(note('info', 'No packs defined yet',
      'Packs are contributed as JSON in the apps/ folder of the repository. One file per app; '
      + 'apps/README.md has the format, and a pull request is checked against the real archive.'));
    return;
  }

  for (const a of store.apps) {
    const card = el('div', 'appcard');
    if (store.selected === a.appid) card.classList.add('on');
    card.append(el('b', null, a.name));
    card.append(el('span', 'aid', String(a.appid)));
    card.append(el('span', 'abuilds',
      `${num(a.builds.length)} build${a.builds.length === 1 ? '' : 's'}`));
    card.onclick = () => selectApp(a.appid);
    host.append(card);
  }
}

function selectApp(appid) {
  pushView({ app: appid });
  store.selected = appid;
  renderStoreList();
  renderApp();
}

function renderApp() {
  const a = store.apps.find((x) => x.appid === store.selected);
  const d = $('#detail');
  d.innerHTML = '';
  d.scrollTop = 0;
  if (!a) return;

  const head = el('div', 'dhead');
  head.append(el('h2', null, a.name));

  const sdb = el('a', 'sdb', `SteamDB · ${a.appid}`);
  sdb.href = `https://steamdb.info/app/${a.appid}`;
  sdb.target = '_blank';
  sdb.rel = 'noreferrer';
  head.append(sdb);
  d.append(head);

  const sub = el('div', 'dsub');
  sub.append(el('span', null, `${num(a.builds.length)} build(s)`));
  sub.append(el('span', null, `defined in apps/${a.appid}.json`));
  d.append(sub);

  for (const b of a.builds) d.append(buildBox({ ...b, appid: a.appid }));
}

function buildBox(build) {
  const box = el('div', 'build');

  const head = el('div', 'buildhead');
  head.append(el('b', null, build.name || build.id));
  if (build.date) head.append(el('span', 'bdate', fmtDate(build.date)));

  const required = build.depots.filter((x) => !x.optional);
  head.append(el('span', 'bsum',
    `${num(required.length)} required · ${num(build.depots.length - required.length)} optional`));
  box.append(head);

  if (build.notes) box.append(el('div', 'buildnotes', build.notes));

  const boxes = [];

  for (const pin of build.depots) {
    const row = el('div', 'pin');
    // A pin the archive cannot satisfy is shown, not quietly dropped: the definition is wrong
    // and someone should fix it.
    if (!pin.known) row.classList.add('gone');

    const cb = el('input');
    cb.type = 'checkbox';
    cb.checked = !pin.optional && pin.known;
    cb.disabled = !pin.known;
    boxes.push({ cb, pin });

    row.append(cb);
    row.append(el('span', 'pname', pin.name || `depot ${pin.depot}`));
    row.append(el('span', 'pver', `depot ${pin.depot} · v${pin.version}`));
    if (pin.role) row.append(el('span', 'prole', pin.role));
    if (!pin.known) {
      row.append(el('span', 'prole',
        pin.maxVersion >= 0 ? `no v${pin.version} — goes to v${pin.maxVersion}` : 'not in archive'));
    }
    box.append(row);
  }

  const foot = el('div', 'buildfoot');
  const go = el('button', 'primary', 'Install this build');
  const said = el('span', 'hint', '');

  go.onclick = async () => {
    const picked = boxes.filter((x) => x.cb.checked).map((x) => x.pin);
    if (!picked.length) { said.textContent = 'nothing selected'; return; }

    go.disabled = true;
    go.textContent = 'Starting…';

    // One install rather than a download per depot. The depots of a game overlay into a single
    // tree, so they are unpacked into one folder and reported as one piece of work.
    try {
      await api.post('/api/installs', {
        appid: build.appid,
        build: build.id,
        depots: picked.map((p) => p.depot),
      });
      said.textContent = `installing ${picked.length} depot(s)`;
      state.act.manualOpen = true;
      applyActivity(true);
      pollInstalls();
    } catch (e) {
      said.textContent = 'could not start: ' + (e.message || e);
    }

    go.disabled = false;
    go.textContent = 'Install this build';
  };

  foot.append(go, said);
  box.append(foot);
  return box;
}

$('#modes').onclick = (e) => {
  const b = e.target.closest('button[data-mode]');
  if (b) setMode(b.dataset.mode);
};


// An install is one row, not one per depot: the depots of a game are an implementation detail of
// the thing being installed. The jobs underneath are hidden while it runs, and its own card lists
// the depots with their individual state.
function installCard(i) {
  const card = el('div', 'job');

  const head = el('div', 'jobhead');
  head.append(el('span', 'title', i.name || `app ${i.appid}`));
  head.append(el('span', 'tag mode', `build ${i.build}`));
  head.append(el('span', 'tag', `${num(i.doneSteps)} / ${num(i.steps.length)} depots`));
  head.append(el('span', 'spacer'));
  head.append(el('span', 'st ' + i.status, i.status));

  if (i.status === 'running') {
    const pending = state.act.cancelling.has(i.id);
    const c = el('button', 'ghost', pending ? 'Cancelling…' : 'Cancel');
    c.disabled = pending;
    c.onclick = () => {
      state.act.cancelling.add(i.id);
      renderActivity();
      api.post(`/api/installs/${i.id}/cancel`).then(pollInstalls);
    };
    head.append(c);
  }
  card.append(head);

  const pct = i.totalBytes > 0
    ? Math.min(100, (i.doneBytes / i.totalBytes) * 100)
    : (i.status === 'done' ? 100 : 0);
  const bar = el('div', 'bar' + (i.status === 'done' ? ' done' : i.status === 'failed' ? ' failed' : ''));
  const fill = el('i');
  fill.style.width = pct + '%';
  bar.append(fill);
  card.append(bar);

  const meta = el('div', 'jobmeta');
  for (const t of [
    `${bytes(i.doneBytes)} / ${bytes(i.totalBytes)}`,
    i.outDir,
    i.error ?? '',
  ]) if (t) meta.append(el('span', null, t));
  card.append(meta);

  // The depots are still listed, because when one fails you need to know which.
  for (const step of i.steps) {
    const row = el('div', 'istep');
    row.append(el('span', 'st ' + step.status, step.status));
    row.append(el('span', 'pname', step.name || `depot ${step.depot}`));
    row.append(el('span', 'pver', `${step.depot} · v${step.version}`));
    if (step.filesWritten) row.append(el('span', 'prole', `${num(step.filesWritten)} files`));
    if (step.error) row.append(el('span', 'prole', step.error));
    card.append(row);
  }

  return card;
}


async function pollInstalls() {
  try {
    state.act.installList = await api.get('/api/installs');
  } catch {
    return;
  }
  renderActivity();
}


// ---------------- sharing ----------------
//
// Taking from a swarm of three seeders and giving nothing back is how an archive dies, so the
// header carries the state plainly rather than burying it in settings: whether anything is being
// shared, to how many peers, and at what rate.

// Said once, on the first start that has not seen it. Sharing spends the reader's upload and
// begins on its own — including on everything downloaded before this version — so it is put in
// front of them rather than left to be found in a bandwidth graph.
//
// There is deliberately no attempt to tell an update from a relaunch. The flag is written when the
// dialog is dismissed, which makes the notice appear exactly once and never again; a relaunch of
// the same version finds the flag already there.
let sharingNoticeShown = false;

function maybeTellAboutSharing() {
  if (sharingNoticeShown) return;

  const s = state.settings;
  if (!s || s.sharingNoticeSeen !== false) return;
  if (!s.torrentEnabled || !s.seedDownloaded) return;

  const dlg = $('#shareDlg');
  if (!dlg || dlg.open) return;

  sharingNoticeShown = true;

  // What is actually on offer right now, so the number is theirs rather than an abstraction.
  const seed = state.seed;
  const now = $('#shareDlgNow');
  if (now) {
    now.textContent = seed?.files
      ? `Right now that is ${num(seed.files)} file(s), ${bytes(seed.bytes)}, already on your disk.`
      : 'Nothing is being shared yet — it starts as soon as you have downloaded something.';
  }

  dlg.showModal();
}

$('#shareDlgOn').onclick = () => {
  api.post('/api/settings', { sharingNoticeSeen: true }).catch(() => {});
};

$('#shareDlgOff').onclick = () => {
  // Turning it off is the whole point of asking, so it has to actually take effect, not merely
  // dismiss the box.
  api.post('/api/seed', { enabled: false }).catch(() => {});
  api.post('/api/settings', { sharingNoticeSeen: true }).catch(() => {});
  setTimeout(pollSeed, 300);
};

async function pollSeed() {
  const box = $('#seedBox');
  const text = $('#seedText');
  if (!box) return;

  let d;
  try { d = await api.get('/api/seed'); } catch { return; }

  state.seed = d;
  box.classList.toggle('on', d.state === 'sharing');
  box.classList.toggle('warn', d.state === 'error');

  if (!d.enabled) {
    text.textContent = 'Not sharing';
    box.title = d.engineEnabled
      ? 'Sharing is off. Turn it on in Settings to give back to the swarm.'
      : 'The BitTorrent engine is switched off in Settings, so nothing is shared.';
    return;
  }

  if (d.state === 'sharing') {
    const rate = d.uploadRate > 0 ? `  ·  ${bytes(d.uploadRate)}/s` : '';
    text.textContent = `Sharing ${num(d.files)}${d.peers ? `  ·  ${num(d.peers)} peers` : ''}${rate}`;
    box.title = `Sharing ${num(d.files)} file(s), ${bytes(d.bytes)} on offer. `
              + `${bytes(d.uploaded)} uploaded so far.`;
    return;
  }

  // starting / idle / error all have something worth saying.
  //
  // Starting is the slow one — reading the file list, linking what is already downloaded, then
  // hashing those links — and on a full archive it runs for minutes. A single "Preparing to share"
  // held for all of it looks indistinguishable from being stuck, so the stage is named instead.
  text.textContent = d.message || (d.state === 'starting' ? 'Preparing to share' : d.state);
  box.title = d.state === 'starting'
    ? 'Starting up: the file list is read, everything already downloaded is linked into the '
      + 'share, and those links are checked before anything is offered.'
    : (d.message || '');
}

$('#seedBox').onclick = () => $('#openSettings').click();

// ---------------- navigation history ----------------
//
// Mouse buttons 4 and 5 are the browser's own back and forward. A page cannot rely on receiving
// them as events — browsers act on them in the chrome, and whether anything reaches JavaScript
// varies by browser and by driver. Intercepting them is also the wrong shape: preventing the
// default and calling history.back() ourselves navigates twice wherever the event *is* delivered.
//
// So instead of catching the buttons, the app gives the browser real history entries to move
// through. Then the side buttons work by doing exactly what they already do, and Alt+Arrow, the
// toolbar buttons and swipe gestures come along for free.

// Set while a view is being restored, so replaying history does not record itself as new history.
let applyingHistory = false;

function viewUrl(view) {
  if (view.app != null) return '#app=' + view.app;
  if (view.depot != null) return '#depot=' + view.depot;
  if (view.q) return '#q=' + encodeURIComponent(view.q);
  return '#';
}

function viewFromHash() {
  const h = decodeURIComponent(location.hash.slice(1));
  const appId = /^app=(\d+)$/.exec(h);
  if (appId) return { app: +appId[1] };
  const depot = /^depot=(\d+)$/.exec(h);
  if (depot) return { depot: +depot[1] };
  if (h.startsWith('q=')) return { q: h.slice(2) };
  return {};
}

function pushView(view, replace = false) {
  if (applyingHistory) return;
  history[replace ? 'replaceState' : 'pushState'](view, '', viewUrl(view));
}

// Renders a view without recording it — this is what going back and forward runs.
async function applyView(view) {
  applyingHistory = true;
  try {
    if (view.q) {
      $('#fileSearch').value = view.q;
      await runFileSearch(view.q);
      return;
    }

    $('#fileSearch').value = '';
    state.searching = false;
    $('#fileSearchNote').textContent = state.fileIndex || '';

    if (view.app != null) {
      setMode('store');
      await loadStore();
      store.selected = view.app;
      renderStoreList();
      renderApp();
    } else if (view.depot != null) {
      setMode('depots');
      await selectDepot(view.depot);
    } else {
      state.selected = null;
      state.detail = null;
      for (const n of document.querySelectorAll('.depot.on')) n.classList.remove('on');
      renderDepotOrEmpty();
    }
  } finally {
    applyingHistory = false;
  }
}

window.addEventListener('popstate', (e) => applyView(e.state ?? viewFromHash()));

// ---------------- loop ----------------

refreshState();
pollJobs();
pollExtract();
setInterval(refreshState, 2000);
setInterval(pollJobs, 1000);
setInterval(pollExtract, 1500);
pollInstalls();
setInterval(pollInstalls, 1000);
pollSeed();
setInterval(pollSeed, 3000);
