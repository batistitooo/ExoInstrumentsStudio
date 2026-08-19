/* ExoInstruments Studio - browser client.
   Talks to the engine only through /api/*. Nothing here knows the backend is C#,
   which is the point: a WebAssembly or Python-backed engine would serve the same
   shapes and this file would not change. */

'use strict';

const $ = (id) => document.getElementById(id);

const state = {
  boot: null,
  target: null,      // selected catalogue entry
  campaign: null,    // live campaign snapshot
  points: [],        // [ut, value, sigma][]
  stream: null,
  startUt: null,
  filter: 'all',     // all | rv | transit, shared by the list and the chart
  capMode: 'single', // single | Mono | TrueColour | NarrowbandHoo | NarrowbandSho
  capObject: null,   // FITS OBJECT name for the next capture
  freePoint: null,   // {ra, dec} when the telescope is aimed off-catalogue via the chart
  fcStartUt: null,   // armed campaign start from the forecast, UT seconds
  fcStartIso: null,  // same instant as an ISO string for the API
  gaia: null,        // /api/gaia descriptor
  gaiaClasses: null, // spectral classes currently drawn
  gaiaPick: null,    // the catalogue star the telescope is aimed at
  sky: null,         // /api/sky payload
  skySel: null,      // selected host on the chart
  skyHover: null,    // hovered host
};

/* ------------------------------------------------------------------ format */

const fmt = {
  num(v, d = 2) {
    if (v === null || v === undefined || Number.isNaN(v)) return 'n/a';
    return v.toLocaleString('en-GB', { minimumFractionDigits: d, maximumFractionDigits: d });
  },
  int(v) {
    if (v === null || v === undefined) return 'n/a';
    return Math.round(v).toLocaleString('en-GB').replace(/,/g, ' ');
  },
  warp(r) {
    if (r < 1000) return '×' + Math.round(r);
    return '×' + Math.round(r).toLocaleString('en-GB').replace(/,/g, ' ');
  },
  days(d) {
    if (d === null || d === undefined) return 'n/a';
    if (d < 2) return fmt.num(d * 24, 1) + ' h';
    if (d < 400) return fmt.num(d, 1) + ' d';
    return fmt.num(d / 365.25, 2) + ' yr';
  },
  clock(s) {
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = Math.floor(s % 60);
    return (h ? h + 'h ' : '') + (h || m ? String(m).padStart(h ? 2 : 1, '0') + 'm ' : '') +
           String(sec).padStart(2, '0') + 's';
  },
  date(iso) {
    return iso ? iso.replace('T', ' ').replace('Z', '') : 'n/a';
  },
};

/* --------------------------------------------------------------- bootstrap */

async function boot() {
  const b = await (await fetch('/api/bootstrap')).json();
  state.boot = b;

  $('catalogueLine').textContent =
    `${fmt.int(b.catalogue.planets)} planets · ${fmt.int(b.catalogue.rvDetectable)} with a reflex signal · ` +
    `${fmt.int(b.catalogue.transiting)} transiting · source ${b.catalogue.source}`;

  const notes = [...b.simplifications];
  if (b.catalogue.minimumMassCorrections) {
    notes.unshift(
      `${b.catalogue.minimumMassCorrections} catalogue entries carry a true mass that differs from ` +
      `M sin i. The injected reflex amplitude uses the minimum mass, as the mass function requires; ` +
      `the true mass only enters the total-mass term.`);
  }
  $('notesList').innerHTML = notes.map((n) => `<li>${n}</li>`).join('');

  // Exoplanet instruments, then the visual astrograph roster from the same catalogue the
  // mod's own capture pipeline reads (VisualTelescopeCatalog via /api/telescopes).
  const scopes = await (await fetch('/api/telescopes')).json();
  state.telescopes = scopes;
  const inst = $('instrument');
  inst.innerHTML =
    `<optgroup label="Exoplanet detection">` +
    b.instruments.map((i) => `<option value="${i.name}">${i.displayName}</option>`).join('') +
    `</optgroup><optgroup label="Astrographs, deep-sky imaging">` +
    scopes.map((t) => `<option value="visual:${t.name}">${t.displayName}</option>`).join('') +
    `</optgroup>`;
  inst.value = 'HARPS';
  inst.onchange = onInstrumentChange;

  const site = $('site');
  site.innerHTML = b.sites.map((s) => `<option value="${s.id}">${s.name} · ${s.country}</option>`).join('');
  site.onchange = onSiteChange;

  onInstrumentChange();
  onSiteChange();

  // The chart data loads in parallel with the opening search; neither waits on the other.
  loadSky();

  // Open on the demo the whole thing was built around.
  $('search').value = '51 Peg b';
  await search('51 Peg b');
  const first = document.querySelector('#results li');
  if (first) first.click();
}

function instrumentByName(name) {
  return state.boot.instruments.find((i) => i.name === name);
}

function selectedScope() {
  const v = $('instrument').value;
  return v.startsWith('visual:')
    ? state.telescopes.find((t) => t.name === v.slice(7))
    : null;
}

/**
 * Show only what the selected instrument produces. Without this, a frame captured on the
 * RC20 stayed on screen under the chart after switching to HARPS, which read as though the
 * spectrograph had taken it.
 */
function showPanelsFor(isAstrograph) {
  for (const id of ['capturePanel']) $(id).hidden = !isAstrograph;
  for (const id of ['clockbar', 'skyPanel', 'seriesPanel', 'foldPanel', 'resultPanel']) {
    if (isAstrograph) $(id).hidden = true;
  }
  // Leaving astrograph mode drops the frame itself, not just its panel: the next capture
  // starts from nothing rather than replacing a picture of a different telescope.
  if (!isAstrograph) {
    $('captureImg').removeAttribute('src');
    $('captureLinks').innerHTML = '';
    $('captureReport').textContent = '';
  }
}

function onInstrumentChange() {
  const scope = selectedScope();
  state.captureMode = !!scope;
  $('captureSetup').hidden = !scope;
  $('observe').hidden = !!scope;
  showPanelsFor(!!scope);

  if (scope) {
    $('instrumentHint').textContent =
      `${scope.telescope} + ${scope.camera} at ${scope.site} · ${(scope.apertureMeters * 1000).toFixed(0)} mm ` +
      `f/${(scope.focalLengthMeters / scope.apertureMeters).toFixed(1)}` +
      (scope.barlow > 1 ? ` ×${scope.barlow} Barlow` : '') +
      ` · ${scope.sensor} px · ` +
      (scope.isSpaceBased
        ? 'above the atmosphere: no seeing, no airmass, no airglow'
        : `seeing ${scope.zenithSeeingArcsec}″ at zenith`);

    // A space telescope has no site and no mount to fail to track with, so neither control is
    // offered. Hiding them rather than leaving them inert is the point: a tracking checkbox the
    // server ignores is a claim that it does something.
    $('siteBlock').hidden = !!scope.isSpaceBased;
    $('trackWrap').hidden = !!scope.isSpaceBased;
    $('spacecraftBlock').hidden = !scope.isSpaceBased;

    $('capFilter').innerHTML = scope.filters.map((f) => `<option>${f}</option>`).join('');
    setupCooler(scope);
    setupZoom(scope);
    $('targetChips').hidden = true;
    $('search').placeholder = 'M 42, Horsehead, type:nebula in:Ori, Vega…';
    search($('search').value);
    // refreshModeChips() used to be called here and HAS NEVER EXISTED, so selecting any
    // astrograph threw a ReferenceError on this line and everything after it in this branch
    // silently did not run: the chart was never redrawn for the new instrument and the
    // forecast was never loaded. The chips it named are the target-mode chips hidden on the
    // line above, so there is nothing to refresh; the call is gone rather than stubbed.
    drawSkyStatic(); drawSkyOverlay();
    if (scope.isSpaceBased) loadPlatform(scope.platform); else scheduleForecast();
    return;
  }

  $('spacecraftBlock').hidden = true;
  $('trackWrap').hidden = false;
  $('orbitPanel').hidden = true;
  $('targetChips').hidden = false;
  $('search').placeholder = '51 Peg b';
  search($('search').value);

  const i = instrumentByName($('instrument').value);
  if (!i) return;
  const cad = i.cadenceSeconds >= 3600
    ? (i.cadenceSeconds / 3600) + ' h'
    : i.cadenceSeconds + ' s';
  $('instrumentHint').textContent =
    `${i.referencePrecision} ${i.unit} at V=${i.referenceMagnitude}, one epoch every ${cad}. ` +
    (i.isSpaceBased ? 'In orbit: no night, no airmass.' : '');
  $('siteBlock').hidden = i.isSpaceBased;
  refreshStartButton();
  scheduleForecast();
}

function onSiteChange() {
  drawSkyStatic();     // the never-visible declination band belongs to the site
  drawSkyOverlay();
  // So does the cooler's reachable range: the TEC's published figure is a delta below ambient,
  // and ambient is a property of the mountain, not of the camera. Rebuilding the control here is
  // the whole fix; before it, the range was fixed at the instrument's home site for ever.
  const scope = selectedScope();
  if (scope) setupCooler(scope);
  scheduleForecast();
}

/* ------------------------------------------------------------------ search */

let searchTimer = null;
$('search').addEventListener('input', (e) => {
  clearTimeout(searchTimer);
  const q = e.target.value;
  searchTimer = setTimeout(() => search(q), 180);
});

const SEARCH_LIMIT = 200;

async function search(q) {
  if (selectedScope()) return pointingSearch(q);

  const qs = new URLSearchParams({ q: q || '', limit: SEARCH_LIMIT });
  if (state.filter === 'rv') qs.set('rv', 'true');
  if (state.filter === 'transit') qs.set('transiting', 'true');

  const r = await fetch(`/api/targets?${qs}`);
  const hits = await r.json();
  const ul = $('results');
  ul.innerHTML = hits.map((t) => `
    <li data-name="${encodeURIComponent(t.name)}">
      <span class="rname">${t.name}</span>
      <span class="rmeta">V ${fmt.num(t.magnitude, 1)} · P ${fmt.num(t.periodDays, 2)} d</span>
    </li>`).join('');

  $('resultCount').textContent = !hits.length
    ? 'nothing in the catalogue matches'
    : hits.length >= SEARCH_LIMIT
      ? `first ${SEARCH_LIMIT} of many, type to narrow, or pick off the chart`
      : `${hits.length} match${hits.length > 1 ? 'es' : ''}`;

  [...ul.children].forEach((li) => {
    li.onclick = () => {
      [...ul.children].forEach((x) => x.classList.remove('on'));
      li.classList.add('on');
      selectTarget(decodeURIComponent(li.dataset.name));
    };
  });
}

/* Astrograph mode: the same box searches the mod's whole pointing index, Messier,
   NGC/IC, the BSC, IAU names, galaxies, with its query language (type:nebula, in:Ori,
   mag:<9, alt:>30). Clicking a row aims the telescope. */
async function pointingSearch(q) {
  const qs = new URLSearchParams({ q: q || 'type:nebula', site: $('site').value, limit: 60 });
  const r = await fetch(`/api/pointing-search?${qs}`);
  if (!r.ok) return;
  const d = await r.json();
  const ul = $('results');

  ul.innerHTML = d.rows.map((t, i) => `
    <li data-i="${i}">
      <span class="rname">${t.displayName}</span>
      <span class="rmeta">${t.magnitude !== null ? 'V ' + fmt.num(t.magnitude, 1) : (t.typeLabel || '').split(' ')[0]}${
        t.altitudeDeg !== null ? ' · alt ' + Math.round(t.altitudeDeg) + '°' : ''}</span>
    </li>`).join('');

  $('resultCount').textContent =
    `${d.total} of ${fmt.int(d.indexed)} pointable targets · try type:nebula, in:Ori, mag:<9, alt:>30`;

  [...ul.children].forEach((li) => {
    li.onclick = () => {
      const t = d.rows[Number(li.dataset.i)];
      if (t.raDeg === null) return;
      [...ul.children].forEach((x) => x.classList.remove('on'));
      li.classList.add('on');
      $('capRa').value = t.raDeg.toFixed(4);
      $('capDec').value = t.decDeg.toFixed(4);
      state.capObject = t.displayName.replace(/\s*\(.*\)$/, '');
      state.freePoint = { ra: t.raDeg, dec: t.decDeg };
      state.skySel = null;
      drawSkyOverlay();
      scheduleForecast();
      $('tName').textContent = t.displayName;
      $('tStatus').textContent = t.kind.replace(/([A-Z])/g, ' $1').trim();
      $('tSub').textContent = `${t.typeLabel || ''}${t.constellation ? ' · ' + t.constellation : ''} · ${t.provenance}`;
      $('tFacts').innerHTML = [
        t.magnitude !== null ? `<dt>V</dt><dd>${fmt.num(t.magnitude, 2)}</dd>` : '',
        t.majorArcmin !== null ? `<dt>Size</dt><dd>${fmt.num(t.majorArcmin, 1)}′</dd>` : '',
        t.altitudeDeg !== null ? `<dt>Altitude now</dt><dd>${fmt.num(t.altitudeDeg, 1)}°</dd>` : '',
        `<dt>α, δ</dt><dd>${fmt.num(t.raDeg, 3)}, ${fmt.num(t.decDeg, 3)}</dd>`,
      ].join('');
      $('targetCard').hidden = false;
    };
  });
}

document.querySelectorAll('#targetChips .chip').forEach((chip) => {
  chip.onclick = () => {
    document.querySelectorAll('#targetChips .chip').forEach((x) => x.classList.remove('on'));
    chip.classList.add('on');
    state.filter = chip.dataset.filter;
    search($('search').value);
    drawSkyOverlay();   // the chart dims hosts the filter excludes, same rules as the list
  };
});

async function selectTarget(name) {
  const r = await fetch(`/api/targets/${encodeURIComponent(name)}`);
  if (!r.ok) return;
  const { target, system } = await r.json();
  state.target = target;
  state.system = system;
  renderTargetCard(target, system);
  refreshStartButton();
  scheduleForecast();
  skySelectHost(target.host);   // ring follows the selection whichever way it was made
  // The astrograph aims where the selection is, too.
  if (target.raDeg !== null && target.raDeg !== undefined) {
    $('capRa').value = target.raDeg.toFixed(4);
    $('capDec').value = target.decDeg.toFixed(4);
    state.capObject = target.host;
    state.freePoint = null;
  }
}

function renderTargetCard(t, system) {
  $('targetCard').hidden = false;
  $('tName').textContent = t.name;
  $('tStatus').textContent = t.status;
  $('tSub').textContent =
    `${t.detectionType || 'unknown method'}${t.discoveryYear ? ', ' + t.discoveryYear : ''}` +
    ` · ${fmt.num(t.distanceParsec, 1)} pc` +
    (system.length > 1 ? ` · ${system.length} known planets` : '');

  const rows = [
    ['V', fmt.num(t.magnitude, 2)],
    ['Period', fmt.num(t.periodDays, 4) + ' d'],
  ];
  // Two distinct masses since the mod's M sin i fix: label each for what it is.
  if (t.minimumMassJupiter) rows.push(['M sin i', fmt.num(t.minimumMassJupiter, 3) + ' M<sub>J</sub>']);
  if (t.massJupiter && (!t.minimumMassJupiter || Math.abs(t.massJupiter - t.minimumMassJupiter) > 1e-9)) {
    rows.push(['True mass', fmt.num(t.massJupiter, 3) + ' M<sub>J</sub>']);
  }
  rows.push(
    ['a', fmt.num(t.semiMajorAxisAu, 3) + ' au'],
    ['e', fmt.num(t.eccentricity, 3)],
  );
  if (t.publishedSemiAmplitudeMps) {
    rows.push(['K published',
      `<span class="hl">${fmt.num(t.publishedSemiAmplitudeMps, 2)}` +
      (t.publishedSemiAmplitudeErrorMps ? ` ±${fmt.num(t.publishedSemiAmplitudeErrorMps, 2)}` : '') +
      ' m/s</span>']);
  }
  if (t.isTransiting) rows.push(['Transit depth', fmt.int(t.expectedDepthPpm) + ' ppm']);

  $('tFacts').innerHTML = rows
    .map(([k, v]) => `<dt>${k}</dt><dd${v.includes('hl') ? ' class="hl"' : ''}>${v}</dd>`).join('');
}

function refreshStartButton() {
  const i = instrumentByName($('instrument').value);
  const t = state.target;
  let ok = !!(t && i);
  if (ok && i.method === 'RadialVelocity') ok = t.isRvDetectable;
  if (ok && i.method === 'Transit') ok = (state.system || []).some((p) => p.isTransiting);
  $('observe').disabled = !ok;
  $('startError').hidden = true;
}

/* --------------------------------------------------------------- campaigns */

$('observe').onclick = async () => {
  const body = {
    target: state.target.name,
    instrument: $('instrument').value,
    site: $('site').value,
    warp: warpFromSlider(),
    startUtc: state.fcStartIso || undefined,
  };
  const r = await fetch('/api/campaigns', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  const data = await r.json();
  if (!r.ok) {
    $('startError').hidden = false;
    $('startError').textContent = data.error || 'Could not start.';
    return;
  }

  state.campaign = data;
  state.points = [];
  state.startUt = data.ut;
  state.fcStartUt = null;
  state.fcStartIso = null;
  $('fcStartChip').hidden = true;

  $('clockbar').hidden = false;
  $('skyPanel').hidden = data.instrument.isSpaceBased;
  $('seriesPanel').hidden = false;
  $('foldPanel').hidden = true;
  $('resultPanel').hidden = true;
  $('warp').disabled = false;
  $('pause').disabled = false;
  $('analyse').disabled = false;
  $('seriesTitle').textContent = data.method === 'RadialVelocity'
    ? 'Radial velocity' : 'Relative flux';

  openStream(data.id);
};

$('pause').onclick = async () => {
  const running = state.campaign.state === 'Running';
  const r = await fetch(`/api/campaigns/${state.campaign.id}/${running ? 'pause' : 'resume'}`, { method: 'POST' });
  state.campaign = await r.json();
  syncRunControls();
};

$('analyse').onclick = async () => {
  $('analyse').disabled = true;
  $('analyse').textContent = 'Analysing…';
  await fetch(`/api/campaigns/${state.campaign.id}/analyse`, { method: 'POST' });
};

function openStream(id) {
  if (state.stream) state.stream.close();
  const es = new EventSource(`/api/campaigns/${id}/stream`);
  state.stream = es;
  es.onmessage = (ev) => {
    const msg = JSON.parse(ev.data);
    state.campaign = msg.campaign;
    if (msg.points && msg.points.length) state.points.push(...msg.points);
    render();
  };
  es.onerror = () => { /* the browser retries on its own */ };
}

/* ------------------------------------------------------------------- warp */

function warpFromSlider() {
  const max = state.boot ? state.boot.limits.maxWarpRate : 2e7;
  const t = $('warp').valueAsNumber / 1000;
  return Math.round(Math.pow(10, t * Math.log10(max)));
}

$('warp').addEventListener('input', () => {
  $('warpVal').textContent = fmt.warp(warpFromSlider());
});

$('warp').addEventListener('change', async () => {
  if (!state.campaign) return;
  await fetch(`/api/campaigns/${state.campaign.id}/warp`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ rate: warpFromSlider() }),
  });
});

function syncRunControls() {
  const c = state.campaign;
  $('pause').textContent = c.state === 'Running' ? 'Pause' : 'Resume';
  $('pause').disabled = c.state === 'Finished';
  $('warp').disabled = c.state === 'Finished';
}

/* ----------------------------------------------------------------- render */

function render() {
  const c = state.campaign;
  if (!c) return;

  $('clockDate').textContent = fmt.date(c.utc);
  $('baseline').textContent = fmt.days(c.baselineDays);
  $('epochs').textContent = fmt.int(c.sampleCount);
  $('wall').textContent = fmt.clock(c.elapsedWallSeconds);
  $('warpVal').textContent = fmt.warp(c.warpRate);
  syncRunControls();

  renderSky(c);
  drawSeries();

  if (c.analysis) {
    $('analyse').disabled = false;
    $('analyse').textContent = 'Analyse again';
    renderAnalysis(c);
  }
}

function renderSky(c) {
  const k = c.conditions;
  if (c.instrument.isSpaceBased) return;

  const open = k.observable;
  $('skyLight').className = 'skylight ' + (open ? 'open' : 'shut');
  $('skyText').className = 'skytext ' + (open ? 'open' : '');
  $('skyText').textContent = open
    ? (c.inTransitBurst ? 'On sky, high-cadence transit sequence' : 'On sky, collecting')
    : (!k.isNight ? 'Daylight, shutter closed'
      : k.occultedByMoon ? `Occulted by the ${k.occultingMoon}`
      : 'Target below the altitude limit');

  $('skyAlt').textContent = k.targetAltitudeDeg === null ? 'n/a' : fmt.num(k.targetAltitudeDeg, 1) + '°';
  $('skyX').textContent = k.airmass === null ? 'n/a' : fmt.num(k.airmass, 2);
  $('skySun').textContent = k.sunAltitudeDeg === null ? 'n/a' : fmt.num(k.sunAltitudeDeg, 1) + '°';
  $('skyMoon').textContent = k.moonSkyFactor ? fmt.num(k.moonSkyFactor, 2) : 'none';
}

/* ------------------------------------------------------------------ charts */

function setupCanvas(cv) {
  // The logical height lives in data-h, NOT in the height attribute: assigning
  // cv.height below rewrites that attribute, so reading it back would multiply the
  // canvas by the device pixel ratio on every redraw.
  const dpr = window.devicePixelRatio || 1;
  const w = cv.clientWidth, h = Number(cv.dataset.h);
  cv.width = w * dpr; cv.height = h * dpr;
  cv.style.height = h + 'px';
  const g = cv.getContext('2d');
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, w, h);
  return { g, w, h };
}

const PAD = { l: 64, r: 16, t: 14, b: 44 };

function axes(g, w, h, xlo, xhi, ylo, yhi, xlabel, ylabel) {
  const X = (v) => PAD.l + (v - xlo) / (xhi - xlo || 1) * (w - PAD.l - PAD.r);
  const Y = (v) => h - PAD.b - (v - ylo) / (yhi - ylo || 1) * (h - PAD.t - PAD.b);

  g.font = '10px ui-monospace, Menlo, monospace';
  g.textBaseline = 'middle';

  // horizontal grid + y labels
  g.textAlign = 'right';
  for (let i = 0; i <= 4; i++) {
    const v = ylo + (yhi - ylo) * i / 4;
    const y = Y(v);
    g.strokeStyle = i === 0 ? '#1c232d' : '#141a22';
    g.beginPath(); g.moveTo(PAD.l, y); g.lineTo(w - PAD.r, y); g.stroke();
    g.fillStyle = '#4d5867';
    g.fillText(niceNum(v, yhi - ylo), PAD.l - 8, y);
  }

  // vertical grid + x labels
  g.textAlign = 'center'; g.textBaseline = 'top';
  for (let i = 0; i <= 5; i++) {
    const v = xlo + (xhi - xlo) * i / 5;
    const x = X(v);
    g.strokeStyle = '#131920';
    g.beginPath(); g.moveTo(x, PAD.t); g.lineTo(x, h - PAD.b); g.stroke();
    g.fillStyle = '#4d5867';
    g.fillText(niceNum(v, xhi - xlo), x, h - PAD.b + 7);
  }

  g.fillStyle = '#3d4757'; g.textAlign = 'right'; g.textBaseline = 'top';
  g.fillText(xlabel, w - PAD.r, h - PAD.b + 24);
  g.save();
  g.translate(12, PAD.t + (h - PAD.t - PAD.b) / 2);
  g.rotate(-Math.PI / 2);
  g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(ylabel, 0, 0);
  g.restore();

  return { X, Y };
}

function niceNum(v, span) {
  const d = span >= 100 ? 0 : span >= 10 ? 1 : span >= 1 ? 2 : 3;
  return v.toFixed(d);
}

function extent(vals) {
  let lo = Infinity, hi = -Infinity;
  for (const v of vals) { if (v < lo) lo = v; if (v > hi) hi = v; }
  if (!Number.isFinite(lo)) return [0, 1];
  if (lo === hi) { lo -= 1; hi += 1; }
  const m = (hi - lo) * 0.09;
  return [lo - m, hi + m];
}

/** Draw at most `cap` points, evenly strided. Canvas copes with more; eyes do not. */
function decimate(points, cap) {
  if (points.length <= cap) return points;
  const stride = Math.ceil(points.length / cap);
  return points.filter((_, i) => i % stride === 0);
}

function drawSeries() {
  const c = state.campaign;
  if (!c || !state.points.length) return;

  const rv = c.method === 'RadialVelocity';
  const pts = decimate(state.points, 9000);
  const t0 = state.startUt;

  const xs = pts.map((p) => (p[0] - t0) / 86400);
  const ys = pts.map((p) => rv ? p[1] : p[1]);
  const [xlo, xhi] = [0, Math.max(1, xs[xs.length - 1])];
  const [ylo, yhi] = extent(ys);

  const { g, w, h } = setupCanvas($('series'));
  const { X, Y } = axes(g, w, h, xlo, xhi, ylo, yhi,
    'days since first night', rv ? 'v_r  (m/s)' : 'relative flux');

  // error bars first, so points sit on top
  g.strokeStyle = 'rgba(94,207,255,.20)';
  g.lineWidth = 1;
  if (pts.length < 3000) {
    g.beginPath();
    for (let i = 0; i < pts.length; i++) {
      const x = X(xs[i]), s = pts[i][2];
      g.moveTo(x, Y(ys[i] - s)); g.lineTo(x, Y(ys[i] + s));
    }
    g.stroke();
  }

  g.fillStyle = 'rgba(94,207,255,.85)';
  const r = pts.length > 4000 ? 0.9 : pts.length > 1200 ? 1.4 : 2.1;
  for (let i = 0; i < pts.length; i++) {
    g.beginPath(); g.arc(X(xs[i]), Y(ys[i]), r, 0, 6.2832); g.fill();
  }

  $('seriesNote').textContent =
    `${fmt.int(c.sampleCount)} epochs` + (pts.length < state.points.length
      ? ` · showing ${fmt.int(pts.length)}` : '');
}

function drawFold(signal) {
  const c = state.campaign;
  const rv = c.method === 'RadialVelocity';
  const P = signal.periodDays * 86400;
  if (!(P > 0) || !state.points.length) return;

  const pts = decimate(state.points, 9000);
  const xs = pts.map((p) => {
    let ph = (p[0] / P) % 1;
    return ph < 0 ? ph + 1 : ph;
  });
  const ys = pts.map((p) => p[1]);
  const [ylo, yhi] = extent(ys);

  const { g, w, h } = setupCanvas($('fold'));
  const { X, Y } = axes(g, w, h, 0, 1, ylo, yhi, 'phase', rv ? 'v_r  (m/s)' : 'relative flux');

  g.fillStyle = 'rgba(94,207,255,.55)';
  const r = pts.length > 4000 ? 0.9 : 1.7;
  for (let i = 0; i < pts.length; i++) {
    g.beginPath(); g.arc(X(xs[i]), Y(ys[i]), r, 0, 6.2832); g.fill();
  }

  // The fitted model. RvDetector fits v = A cos(wt) + B sin(wt) + C and reports
  // K = hypot(A,B) with phase = atan2(-B,A)/2pi, so the curve is K cos(2pi(phase+p0)).
  if (rv) {
    const mean = ys.reduce((a, b) => a + b, 0) / ys.length;
    g.strokeStyle = 'rgba(255,180,84,.95)';
    g.lineWidth = 1.8;
    g.beginPath();
    for (let i = 0; i <= 240; i++) {
      const ph = i / 240;
      const v = mean + signal.amplitude * Math.cos(2 * Math.PI * (ph + signal.phase01));
      const x = X(ph), y = Y(v);
      i ? g.lineTo(x, y) : g.moveTo(x, y);
    }
    g.stroke();
  }

  $('foldNote').textContent = `P = ${fmt.num(signal.periodDays, 6)} d`;
}

/* ---------------------------------------------------------------- analysis */

function renderAnalysis(c) {
  const a = c.analysis;
  const rv = a.method === 'RadialVelocity';
  const t = c.target;
  const best = a.signals.find((s) => s.detected);

  $('resultPanel').hidden = false;
  $('ampHead').textContent = rv ? 'Semi-amplitude' : 'Depth';
  $('resultNote').textContent = `${fmt.days(a.baselineDays)} of baseline`;

  if (!best) {
    $('verdict').innerHTML =
      `<div class="verdict-box miss"><div class="vcell">
         <span class="vlbl">No signal above threshold</span>
         <span class="vsub">Keep observing: the search needs at least two full cycles of baseline.</span>
       </div></div>`;
    $('foldPanel').hidden = true;
  } else {
    const dP = 100 * Math.abs(best.periodDays - t.periodDays) / t.periodDays;
    const pubK = t.publishedSemiAmplitudeMps;
    const dK = pubK ? 100 * Math.abs(best.amplitude - pubK) / pubK : null;

    const cells = [
      ['Recovered period', `${fmt.num(best.periodDays, 6)} d`,
        `catalogue ${fmt.num(t.periodDays, 6)} d · ${dP < 0.01 ? '<0.01' : fmt.num(dP, 3)}% off`, dP < 0.5],
    ];
    if (rv) {
      cells.push(['Recovered K',
        `${fmt.num(best.amplitude, 2)} ± ${fmt.num(best.amplitudeUncertainty, 2)} m/s`,
        pubK ? `published ${fmt.num(pubK, 2)} m/s · ${fmt.num(dK, 2)}% off` : 'no published value',
        dK !== null && dK < 5]);
    } else {
      cells.push(['Recovered depth', `${fmt.int(best.amplitude)} ppm`,
        `catalogue ${fmt.int(t.expectedDepthPpm)} ppm`, true]);
    }
    cells.push(['Significance', `S/N ${fmt.num(best.snr, 0)}`,
      `${fmt.int(best.sampleCount)} epochs searched`, true]);

    $('verdict').innerHTML = `<div class="verdict-box">${cells.map(([l, v, s, good]) => `
      <div class="vcell">
        <span class="vlbl">${l}</span>
        <span class="vval${good ? ' good' : ''}">${v}</span>
        <span class="vsub">${s}</span>
      </div>`).join('')}</div>`;

    $('foldPanel').hidden = false;
    drawFold(best);
  }

  $('signalRows').innerHTML = a.signals.map((s) => {
    if (s.insufficientData) {
      return `<tr class="miss"><td class="idx">${s.index}</td><td colspan="4">
        not enough epochs yet (${fmt.int(s.sampleCount)} collected)</td></tr>`;
    }
    const alias = s.detected && isAlias(s, c);
    const tag = !s.detected ? '<span class="tag below">below threshold</span>'
      : alias ? '<span class="tag alias">window alias</span>'
      : '<span class="tag detected">detected</span>';
    return `<tr class="${s.detected ? 'hit' : 'miss'}">
      <td class="idx">${s.index}</td>
      <td>${fmt.num(s.periodDays, 5)} d</td>
      <td>${rv ? fmt.num(s.amplitude, 2) + ' m/s' : fmt.int(s.amplitude) + ' ppm'}</td>
      <td>${fmt.num(s.snr, 1)}</td>
      <td>${tag}</td>
    </tr>`;
  }).join('');
}

/**
 * Flag the phantoms rather than hide them. Two well-understood kinds show up in a
 * ground-based programme, and RvDetector's own source documents the second:
 *  - a period pinned at a low multiple of the epoch cadence,
 *  - a period the detector itself marked as a harmonic of a stronger signal.
 * Real surveys argue about exactly these, so a demo is better for showing them.
 */
function isAlias(signal, c) {
  if (signal.likelyHarmonicOfPeriodDays) return true;
  const cadenceDays = c.instrument.cadenceSeconds / 86400;
  for (let m = 1; m <= 6; m++) {
    if (Math.abs(signal.periodDays - m * cadenceDays) / (m * cadenceDays) < 0.03) return true;
  }
  return Math.abs(signal.periodDays - 1) < 0.02;   // the one-day observing window
}

/* ----------------------------------------------------------------- capture */
/* The visual telescopes. The frame is the mod's own deep-sky pipeline (Gaia stars,
   measured galaxy maps, H-alpha emission, chromatic PSF, real detector) computed
   server-side; what arrives here is a finished PNG and its metadata. */

/**
 * The cooler, where the instrument has one. The setpoint is not a label: the server scales
 * the published dark current from the temperature it was measured at to this one through
 * DarkCurrentModel, so a warmer sensor really does put more dark charge under the exposure.
 */
/** The site currently selected, with its ambient air temperature. Null for a space telescope. */
function currentSite() {
  return state.boot.sites.find((s) => s.id === $('site').value) || null;
}

/**
 * The cooler's reachable range, which belongs to the instrument AND the site together.
 *
 * The published TEC figure is a DELTA below ambient, not an absolute floor, so the same camera
 * reaches a genuinely different temperature on a cold mountain than in Provence. This used to be
 * baked server-side from the instrument's own home site and never moved, so taking the RC20 to
 * Mauna Kea still offered it Provence's range.
 */
function coolerRange(scope) {
  const site = currentSite();
  const ambient = site && site.ambientTemperatureC !== null && site.ambientTemperatureC !== undefined
    ? site.ambientTemperatureC : null;
  if (ambient === null || scope.coolerDeltaC === null || scope.coolerDeltaC === undefined) return null;
  return { ambient, min: ambient - scope.coolerDeltaC, max: ambient, site };
}

function setupCooler(scope) {
  const row = $('coolRow');
  const range = scope.hasAdjustableCooler ? coolerRange(scope) : null;
  if (!range) {
    row.hidden = true;
    $('coolHint').textContent = '';
    return;
  }
  row.hidden = false;
  const el = $('capTemp');
  el.min = Math.round(range.min);
  el.max = Math.round(range.max);
  // The published setpoint, but only if this site can actually hold it. At Mauna Kea the range
  // runs far colder and at a warm site it may not reach -20 at all; clamping here rather than
  // letting the slider sit outside its own bounds keeps the readout and the request in step.
  el.value = Math.round(Math.min(Math.max(scope.detectorTemperatureC, range.min), range.max));
  el.oninput = () => { updateCoolerOut(scope); };
  updateCoolerOut(scope);
}

function updateCoolerOut(scope) {
  const t = $('capTemp').valueAsNumber;
  $('capTempOut').textContent = `${t > 0 ? '+' : ''}${t} °C`;
  const range = coolerRange(scope);
  if (!range) { $('coolHint').textContent = ''; return; }

  // What the choice costs, in the units the exposure actually pays: the published rate is
  // quoted at the instrument's own setpoint, and the model scales from there. The ambient is
  // the SITE's, and it says whether that figure is a night statistic or a round-the-clock mean,
  // because only one of the five is the former.
  const dt = t - scope.detectorTemperatureC;
  const air = `air at ${range.site.name} is ${fmt.num(range.ambient, 1)} °C` +
              (range.site.ambientIsNightTime ? ' at night' : ' (24 h mean)') +
              ` · this cooler holds ${scope.coolerDeltaC} °C under it, so ${fmt.num(range.min, 1)} °C`;
  $('coolHint').textContent = Math.abs(dt) < 0.5
    ? `at the published setpoint (${scope.detectorTemperatureC} °C), ${scope.darkCurrentAtSpecC} e⁻/s/px dark · ${air}`
    : `${dt > 0 ? '+' : ''}${dt.toFixed(0)} °C from the published ${scope.detectorTemperatureC} °C · ${air}`;
}

/**
 * The Barlow. Not a crop: it is the optical element the instrument physically carries, so
 * the field narrows by the factor dialled and the plate scale follows. An instrument that
 * flies what it launched with has no range to offer and the control disappears.
 */
function setupZoom(scope) {
  const row = $('zoomRow');
  if (!scope.hasZoomRange) {
    row.hidden = true;
    return;
  }
  row.hidden = false;
  const el = $('capZoom');
  el.min = 1;
  el.max = scope.barlowFactor;
  el.value = 1;
  el.oninput = () => updateZoomOut(scope);
  updateZoomOut(scope);
}

function updateZoomOut(scope) {
  const z = $('capZoom').valueAsNumber;
  const fov = scope.maxFovDeg / z;
  $('capZoomOut').textContent = `×${z.toFixed(2)}`;
  $('zoomHint').textContent =
    `field ${(fov * 60).toFixed(1)}′ across · range ${(scope.minFovDeg * 60).toFixed(1)}′ to ${(scope.maxFovDeg * 60).toFixed(1)}′`;
}

function currentObjectName() {
  if (state.capObject) return state.capObject;
  const ra = parseFloat($('capRa').value), dec = parseFloat($('capDec').value);
  return `field ${ra.toFixed(2)} ${dec >= 0 ? '+' : ''}${dec.toFixed(2)}`;
}

['capRa', 'capDec'].forEach((id) => $(id).addEventListener('change', scheduleForecast));

$('capture').onclick = async () => {
  const scope = selectedScope();
  if (!scope) return;
  const btn = $('capture');
  btn.disabled = true;
  btn.textContent = 'Exposing…';
  $('captureError').hidden = true;

  try {
    const r = await fetch('/api/capture', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        telescope: scope.name,
        site: $('site').value,
        raDeg: parseFloat($('capRa').value),
        decDeg: parseFloat($('capDec').value),
        filter: $('capFilter').value,
        exposureSeconds: parseFloat($('capExp').value),
        binning: parseInt($('capBin').value, 10),
        tracking: $('capTrack').checked,
        objectName: currentObjectName(),
        detectorTemperatureCelsius: $('coolRow').hidden ? undefined : $('capTemp').valueAsNumber,
        zoomFactor: $('zoomRow').hidden ? undefined : $('capZoom').valueAsNumber,
        // A cell picked on the calendar books that slot; otherwise the server schedules
        // the coming night's best moment for the field. Never carried over to a space
        // telescope: that slot was chosen off a GROUND site's night, and up there it
        // means nothing but would be honoured as a hard booking and probably refused.
        atUtc: (scope.isSpaceBased ? undefined : state.fcStartIso) || undefined,
      }),
    });
    const data = await r.json();
    if (!r.ok) {
      $('captureError').hidden = false;
      $('captureError').textContent = data.error || 'Capture failed.';
      return;
    }

    $('capturePanel').hidden = false;
    $('captureImg').src = 'data:image/png;base64,' + data.png;
    $('captureTitle').textContent =
      `${scope.displayName}, ${currentObjectName()}, ${$('capFilter').value}, ${$('capExp').value} s`;
    $('captureNote').textContent =
      `${data.width}×${data.height} px · ${fmt.num(data.fovArcmin[0], 1)}′×${fmt.num(data.fovArcmin[1], 1)}′ · ` +
      `${fmt.num(data.plateScaleArcsec, 2)}″/px`;

    const bits = [
      data.observedUtc ? `${state.fcStartIso ? 'booked' : 'scheduled'} ${data.observedUtc}` : null,
      `${fmt.int(data.starsDrawn)} Gaia stars`,
      data.galaxiesDrawn ? `${data.galaxiesDrawn} galaxies${data.galaxiesFromImages.length ? ' (' + data.galaxiesFromImages.join(', ') + ' from measured maps)' : ''}` : null,
      data.emissionLines ? `emission: ${data.emissionLines}` : null,

      // The atmospheric line, or the orbital one in its place. Not both, and not a "seeing 0″
      // at X 1" line for a telescope that is above the weather: those two numbers have no
      // referent up there, and printing them would imply they were measured.
      data.platform
        ? `${data.platform.name} at ${fmt.int(data.platform.altitudeKm)} km · ` +
          `pointing ${fmt.num(data.platform.pointingRmsArcsec, 3)}″ rms ` +
          `(${fmt.num(data.platform.pointingFwhmArcsec, 3)}″ into the PSF)`
        : `seeing ${fmt.num(data.seeingArcsec, 2)}″ at X ${fmt.num(data.airmass, 2)}`,

      data.platform
        ? `sky ${fmt.num(data.platform.skyVMagPerArcsec2, 2)} V mag/arcsec² ` +
          `(zodiacal ${fmt.num(data.platform.zodiacalVMagPerArcsec2, 2)}` +
          (data.platform.earthshineVMagPerArcsec2 !== null
            ? `, earthshine ${fmt.num(data.platform.earthshineVMagPerArcsec2, 2)}` : '') + ')'
        : null,
      data.platform && data.platform.conditions
        ? `Sun ${fmt.num(data.platform.conditions.sunAngleDeg, 0)}°, ` +
          `Earth limb ${fmt.num(data.platform.conditions.earthLimbAngleDeg, 0)}° ` +
          `${data.platform.conditions.limbIsSunlit ? 'sunlit' : 'dark'}, ` +
          `${(data.platform.occultedOrbitFraction * 100).toFixed(0)}% of the orbit occulted`
        : null,

      `sky ${fmt.num(data.skyElectronsPerPixel, 1)} e⁻/px`,
      data.saturatedFraction > 0 ? `${(data.saturatedFraction * 100).toFixed(2)}% saturated` : null,
      data.detectorTemperatureC !== null && data.detectorTemperatureC !== undefined
        ? `sensor ${fmt.num(data.detectorTemperatureC, 0)} °C, dark ${fmt.num(data.darkElectronsPerPixel, 1)} e⁻/px` : null,
      `${fmt.int(data.computeMs)} ms`,
    ].filter(Boolean);
    $('captureMeta').textContent = bits.join(' · ');

    $('captureLinks').innerHTML = data.fitsUrl
      ? `<a href="${data.fitsUrl}" download>Download FITS</a> <span class="dim">16-bit, WCS and MAGZERO in the header; stack in Siril</span>`
      : '';
    // What the orbital path leaves out, shown with the frame it applies to rather than filed
    // away under the header's general list: these five are true of THIS picture and of no
    // ground frame, so they belong next to it.
    $('captureReport').textContent = data.platform && state.spaceSimplifications
      ? 'Not modelled from orbit: ' + state.spaceSimplifications.join(' ')
      : '';
  } finally {
    btn.disabled = false;
    btn.textContent = 'Capture';
  }
};

// What data is actually behind the frames, stated in the panel rather than implied.
(async () => {
  try {
    const d = await (await fetch('/api/capture/data')).json();

    // WARNINGS COME OUT OF THE LIST. They used to be concatenated into it with ' . ', which is
    // how a real one went unnoticed: the Gaia catalogue's declination index was broken, the
    // server detected it and said so in as many words, and the sentence sat in the middle of six
    // file paths in dim grey. Every star field rendered empty for as long as that took to spot.
    state.spaceSimplifications = d.spaceSimplifications || null;

    const warnings = d.files.filter(f => /^WARNING/i.test(f));
    const paths = d.files.filter(f => !/^WARNING/i.test(f));

    $('captureData').textContent = paths.join(' · ');
    const box = $('captureDataWarnings');
    if (box) {
      box.innerHTML = '';
      for (const w of warnings) {
        const p = document.createElement('p');
        p.className = 'dataWarning';
        p.textContent = w.replace(/^WARNING,?\s*/i, '');
        box.appendChild(p);
      }
      box.style.display = warnings.length ? '' : 'none';
    }
  } catch { /* endpoint optional */ }
})();

/* ---------------------------------------------------------------- forecast */
/* The observing calendar. Rows are nights, columns run through one sidereal day, so
   the night block stands still while the date slides and the twilight edge drifts by
   about four minutes a row. Clicking a cell arms the campaign to start there. */

let forecastTimer = null;
let lastForecast = null;

function scheduleForecast() {
  clearTimeout(forecastTimer);
  // A space telescope has no night to forecast. The equivalent question is which parts of the
  // coming revolution the pointing is legal in, and that has its own endpoint and its own panel.
  const scope = selectedScope();
  if (scope && scope.isSpaceBased) {
    $('forecastPanel').hidden = true;
    forecastTimer = setTimeout(loadOrbitVisibility, 250);
    return;
  }
  $('orbitPanel').hidden = true;
  forecastTimer = setTimeout(loadForecast, 250);
}

async function loadForecast() {
  const scope = selectedScope();
  const qs = new URLSearchParams({ site: $('site').value, nights: 30, cols: 96 });

  if (scope) {
    qs.set('ra', $('capRa').value);
    qs.set('dec', $('capDec').value);
  } else if (state.target && state.target.raDeg !== null && state.target.raDeg !== undefined) {
    qs.set('target', state.target.name);
    qs.set('instrument', $('instrument').value);
  } else {
    $('forecastPanel').hidden = true;
    return;
  }

  const r = await fetch(`/api/forecast?${qs}`);
  if (!r.ok) { $('forecastPanel').hidden = true; return; }
  const f = await r.json();
  if (f.spaceBased) { $('forecastPanel').hidden = true; return; }

  lastForecast = f;
  $('forecastPanel').hidden = false;

  const name = scope ? currentObjectName() : (state.target ? state.target.name : 'field');
  $('forecastNote').textContent = f.bestUtc
    ? `${name} from ${siteName()} · best ${f.bestUtc}`
    : `${name} never clears ${f.altitudeLimitDeg}° at night from ${siteName()}`;
  $('forecastHint').innerHTML =
    `culminates at ${fmt.num(f.maxAltitudeDeg, 1)}° · graded by ${f.graded} · ` +
    `${f.rows} nights × ${f.columns} slots`;
  $('fcScale').innerHTML = `<span>worse</span><i id="fcRamp"></i><span>better</span>`;
  paintRamp();
  $('fcBest').hidden = !f.bestUtc;
  drawForecast();
}

function siteName() {
  const s = state.boot.sites.find((x) => x.id === $('site').value);
  return s ? s.name : $('site').value;
}

/**
 * Porkchop ramp. Unusable sky is not a colour on the scale, it is off it: near-black, so
 * the eye reads the observable windows as shapes rather than hunting for them inside a
 * continuous gradient.
 */
function fcColour(q) {
  if (!(q > 0)) return '#120a0c';
  // deep red at the horizon, through amber and green, to cyan at the zenith
  const stops = [
    [0.00, [88, 22, 28]],
    [0.25, [150, 62, 32]],
    [0.50, [196, 140, 44]],
    [0.72, [96, 172, 92]],
    [0.88, [58, 168, 178]],
    [1.00, [126, 214, 255]],
  ];
  let i = 0;
  while (i < stops.length - 2 && q > stops[i + 1][0]) i++;
  const [q0, c0] = stops[i], [q1, c1] = stops[i + 1];
  const t = (q - q0) / (q1 - q0 || 1);
  const c = c0.map((v, k) => Math.round(v + (c1[k] - v) * Math.min(1, Math.max(0, t))));
  return `rgb(${c[0]},${c[1]},${c[2]})`;
}

function paintRamp() {
  const el = $('fcRamp');
  if (!el) return;
  const stops = [];
  for (let i = 0; i <= 10; i++) stops.push(`${fcColour(i / 10)} ${i * 10}%`);
  el.style.background = `linear-gradient(90deg, ${stops.join(',')})`;
}

const FC = { l: 66, r: 12, t: 22, b: 8 };

function drawForecast() {
  const f = lastForecast;
  if (!f) return;
  const cv = $('forecast');

  // The canvas grows with the run rather than squeezing thirty nights into a strip.
  const rowH = 10;
  const h = FC.t + FC.b + f.rows * rowH;
  cv.dataset.h = h;

  const dpr = window.devicePixelRatio || 1;
  const w = cv.clientWidth;
  cv.width = w * dpr; cv.height = h * dpr;
  cv.style.height = h + 'px';
  const g = cv.getContext('2d');
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, w, h);

  const plotW = w - FC.l - FC.r;
  const cw = plotW / f.columns;

  const t0 = Date.parse(f.startUtc.replace(' ', 'T'));

  for (let row = 0; row < f.rows; row++) {
    for (let col = 0; col < f.columns; col++) {
      const q = f.quality[row * f.columns + col];
      g.fillStyle = fcColour(q);
      g.fillRect(FC.l + col * cw, FC.t + row * rowH, Math.ceil(cw) + 0.5, rowH);
    }
  }

  // Hour axis. Columns are one sidereal day wide, so the label is the UT hour of the
  // FIRST row; later rows drift, which is what the slant in the night block shows.
  g.font = '9px ui-monospace, Menlo, monospace';
  g.fillStyle = '#4d5867';
  g.textAlign = 'center'; g.textBaseline = 'bottom';
  for (let hr = 0; hr <= 24; hr += 3) {
    const col = (hr / 24) * f.columns;
    const x = FC.l + col * cw;
    if (x > w - FC.r) continue;
    g.fillText(String(hr % 24).padStart(2, '0') + 'h', x, FC.t - 5);
    g.strokeStyle = 'rgba(70,84,102,.35)';
    g.beginPath(); g.moveTo(x, FC.t); g.lineTo(x, FC.t + f.rows * rowH); g.stroke();
  }

  // Date axis, every fifth night so the column stays readable.
  g.textAlign = 'right'; g.textBaseline = 'middle';
  for (let row = 0; row < f.rows; row += 5) {
    const d = new Date(t0 + row * f.columns * f.cellSeconds * 1000);
    g.fillStyle = '#4d5867';
    g.fillText(d.toISOString().slice(5, 10), FC.l - 8, FC.t + row * rowH + rowH / 2);
  }

  // The armed start.
  if (state.fcStartUt && state.fcStartUt >= f.startUt) {
    const idx = Math.floor((state.fcStartUt - f.startUt) / f.cellSeconds);
    const row = Math.floor(idx / f.columns), col = idx % f.columns;
    if (row < f.rows) {
      g.strokeStyle = 'rgba(255,180,84,.95)';
      g.lineWidth = 1.6;
      g.strokeRect(FC.l + col * cw - 1, FC.t + row * rowH - 1, Math.max(3, cw) + 2, rowH + 2);
    }
  }
}

function forecastCellAt(e) {
  const f = lastForecast;
  if (!f) return null;
  const cv = $('forecast');
  const box = cv.getBoundingClientRect();
  const plotW = box.width - FC.l - FC.r;
  const rowH = (box.height - FC.t - FC.b) / f.rows;
  const col = Math.floor((e.clientX - box.left - FC.l) / plotW * f.columns);
  const row = Math.floor((e.clientY - box.top - FC.t) / rowH);
  if (col < 0 || col >= f.columns || row < 0 || row >= f.rows) return null;
  const i = row * f.columns + col;
  return {
    row, col,
    q: f.quality[i],
    alt: f.altitude[i],
    night: f.night[i],
    ut: f.startUt + (i + 0.5) * f.cellSeconds,
  };
}

function armStart(ut) {
  state.fcStartUt = ut;
  const d = new Date(Date.parse(lastForecast.startUtc.replace(' ', 'T')) + (ut - lastForecast.startUt) * 1000);
  state.fcStartIso = d.toISOString();
  $('fcStartChip').hidden = false;
  $('fcStartChip').textContent =
    `${selectedScope() ? 'shoots' : 'starts'} ${d.toISOString().slice(0, 16).replace('T', ' ')}Z (click to clear)`;
  drawForecast();
}

$('forecast').addEventListener('click', (e) => {
  const cell = forecastCellAt(e);
  if (!cell || cell.q <= 0) return;
  // One meaning in both modes: the observer picked the slot. A campaign starts there, and
  // an astrograph exposure is booked there instead of the server choosing the night.
  armStart(cell.ut);
});

$('forecast').addEventListener('mousemove', (e) => {
  const cell = forecastCellAt(e);
  const tip = $('forecastTip');
  if (!cell) { tip.hidden = true; return; }
  const when = new Date(Date.parse(lastForecast.startUtc.replace(' ', 'T'))
    + (cell.ut - lastForecast.startUt) * 1000);
  const why = cell.q > 0 ? `quality ${(cell.q * 100).toFixed(0)}%`
    : !cell.night ? 'daylight'
    : `below the ${lastForecast.altitudeLimitDeg}° limit`;
  tip.innerHTML = `<b>${when.toISOString().slice(0, 16).replace('T', ' ')}Z</b> ` +
    `<span class="tipsub">alt ${fmt.num(cell.alt, 0)}° · ${why}</span>`;
  tip.hidden = false;
  const box = $('forecast').getBoundingClientRect();
  tip.style.left = Math.min(e.clientX - box.left + 12, box.width - 210) + 'px';
  tip.style.top = Math.max(2, e.clientY - box.top - 32) + 'px';
});
$('forecast').addEventListener('mouseleave', () => { $('forecastTip').hidden = true; });

$('fcBest').onclick = () => { if (lastForecast && lastForecast.bestUt) armStart(lastForecast.bestUt); };
$('fcStartChip').onclick = () => {
  state.fcStartUt = null;
  state.fcStartIso = null;
  $('fcStartChip').hidden = true;
  drawForecast();
};

/* -------------------------------------------------------------------- misc */

$('notesToggle').onclick = () => { $('notes').hidden = !$('notes').hidden; };

let resizeTimer = null;
window.addEventListener('resize', () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => {
    redrawChart();
    drawForecast();
    if (state.campaign) {
      drawSeries();
      const best = state.campaign.analysis?.signals.find((s) => s.detected);
      if (best) drawFold(best);
    }
  }, 120);
});

/* ---------------------------------------------------------------- sky chart */
/* The mod draws its chart in Core/SkyChartTexture.cs, the single Unity file in
   Core and so the one piece this build excludes. The pixels were the only Unity
   part though: the data ships with the mod, so the chart is redrawn here from
   the same sources, the Yale BSC as the background sky, exoplanet hosts as one
   marker per star, the first-magnitude IAU names as labels.

   Projection is Hammer-Aitoff (equal-area, whole sky in a 2:1 ellipse), centred
   on RA 12h, north up and east LEFT, the mod's own frame convention (PA 0). */

function hammer(raDeg, decDeg) {
  const lam = (raDeg - 180) * Math.PI / 180;
  const phi = decDeg * Math.PI / 180;
  const z = Math.sqrt(1 + Math.cos(phi) * Math.cos(lam / 2));
  return {
    x: -2 * Math.SQRT2 * Math.cos(phi) * Math.sin(lam / 2) / z,  // minus: east on the left
    y: Math.SQRT2 * Math.sin(phi) / z,
  };
}

/**
 * The chart viewport: a zoom about a point, shared by everything drawn on the chart.
 *
 * The Gaia layer is an image and the graticule and markers are canvases, so all three have
 * to agree exactly or the stars slide out from under their labels. They agree because they
 * all go through skyGeom: the image gets the same scale and offset as a CSS transform, the
 * canvases apply it in their own arithmetic, and skyInverse undoes it for pointing.
 */
const view = { zoom: 1, panX: 0, panY: 0 };

function skyGeom() {
  const el = $('skychart');
  const w = el.clientWidth, h = el.clientHeight;
  const base = Math.min(w / (4 * Math.SQRT2 * 1.02), h / (2 * Math.SQRT2 * 1.08));
  return {
    w, h,
    cx: w / 2 + view.panX,
    cy: h / 2 + view.panY,
    s: base * view.zoom,
  };
}

/** Keep the map from being dragged off its own panel. */
function clampView() {
  const el = $('skychart');
  const w = el.clientWidth, h = el.clientHeight;
  const base = Math.min(w / (4 * Math.SQRT2 * 1.02), h / (2 * Math.SQRT2 * 1.08));
  // Half the projected ellipse, at the current zoom, minus half the panel: how far the
  // centre may travel before an edge comes inside the frame.
  const spanX = Math.max(0, 2 * Math.SQRT2 * base * view.zoom - w / 2);
  const spanY = Math.max(0, Math.SQRT2 * base * view.zoom - h / 2);
  view.panX = Math.max(-spanX, Math.min(spanX, view.panX));
  view.panY = Math.max(-spanY, Math.min(spanY, view.panY));
}

/** The layer is a picture of the whole sky, so the viewport is a CSS transform on it. */
function applyLayerTransform() {
  const img = $('gaiaLayer');
  if (!img) return;
  img.style.transformOrigin = '50% 50%';
  img.style.transform = `translate(${view.panX}px, ${view.panY}px) scale(${view.zoom})`;
  // Ask for a sharper render once magnified; the server caps at 4000 and caches per width.
  const wanted = view.zoom > 1.6 ? 4000 : 2000;
  if (state.gaia && state.gaia.loaded && state.gaiaWidth !== wanted) {
    state.gaiaWidth = wanted;
    scheduleGaiaLayer();
  }
}

function redrawChart() {
  clampView();
  applyLayerTransform();
  drawSkyStatic();
  drawSkyOverlay();
}

function skyXY(raDeg, decDeg, geo) {
  const p = hammer(raDeg, decDeg);
  return { x: geo.cx + p.x * geo.s, y: geo.cy - p.y * geo.s };
}

/** Screen point back to RA/Dec: the closed-form Hammer inverse, minus sign undoing
    the east-left convention. Null outside the projection's ellipse. */
function skyInverse(mx, my, geo) {
  const X = -(mx - geo.cx) / geo.s;
  const Y = (geo.cy - my) / geo.s;
  const t = 1 - (X / 4) * (X / 4) - (Y / 2) * (Y / 2);
  if (t < 0) return null;
  const z = Math.sqrt(t);
  const lam = 2 * Math.atan2(z * X, 2 * (2 * z * z - 1));
  const sinPhi = z * Y;
  if (sinPhi < -1 || sinPhi > 1) return null;
  let ra = lam * 180 / Math.PI + 180;
  if (ra < 0) ra += 360;
  if (ra >= 360) ra -= 360;
  return { ra, dec: Math.asin(sinPhi) * 180 / Math.PI };
}

function setupSkyCanvas(cv) {
  const dpr = window.devicePixelRatio || 1;
  const w = cv.clientWidth, h = cv.clientHeight;
  cv.width = w * dpr; cv.height = h * dpr;
  const g = cv.getContext('2d');
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, w, h);
  return g;
}

/* ------------------------------------------------------------- the Gaia layer */
/* 7.4 million stars, rendered server-side into a Hammer projection that registers
   pixel for pixel with the overlay drawn over it. The browser never receives the
   catalogue; it receives a picture of it, and points through the cone search. */

const GAIA_CLASS_TINT = {
  O: '#9bb0ff', B: '#aabfff', A: '#cad7ff', F: '#f8f7ff',
  G: '#fff4ea', K: '#ffd2a1', M: '#ffb56c', '?': '#7d8a9c',
};

async function loadGaia() {
  const g = await (await fetch('/api/gaia')).json();
  state.gaia = g;
  if (!g.loaded) {
    $('gaiaBar').hidden = true;
    return;
  }

  $('gaiaBar').hidden = false;
  $('starLayerLabel').textContent = 'Gaia DR3';
  state.gaiaClasses = new Set(g.classes);

  $('classChips').innerHTML = g.classes.map((c) =>
    `<button class="chip on gaiaclass" data-class="${c}" style="--tint:${GAIA_CLASS_TINT[c]}">${c}</button>`).join('');
  document.querySelectorAll('#classChips .chip').forEach((chip) => {
    chip.onclick = () => {
      const c = chip.dataset.class;
      if (state.gaiaClasses.has(c)) state.gaiaClasses.delete(c);
      else state.gaiaClasses.add(c);
      // Never let the filter empty out; the map would simply go black.
      if (!state.gaiaClasses.size) state.gaiaClasses.add(c);
      chip.classList.toggle('on', state.gaiaClasses.has(c));
      scheduleGaiaLayer();
    };
  });

  ['magMin', 'magMax'].forEach((id) => $(id).addEventListener('input', () => {
    // Keep the thumbs from crossing: whichever moved wins, the other follows.
    const lo = $('magMin').valueAsNumber, hi = $('magMax').valueAsNumber;
    if (lo > hi) { if (id === 'magMin') $('magMax').value = lo; else $('magMin').value = hi; }
    updateMagOut();
    scheduleGaiaLayer();
  }));

  updateMagOut();
  loadGaiaLayer();
}

function updateMagOut() {
  const lo = $('magMin').valueAsNumber, hi = $('magMax').valueAsNumber;
  $('magOut').textContent = `V ${lo.toFixed(1)} to ${hi.toFixed(1)}`;
}

let gaiaTimer = null;
function scheduleGaiaLayer() {
  clearTimeout(gaiaTimer);
  gaiaTimer = setTimeout(loadGaiaLayer, 220);
}

function loadGaiaLayer() {
  if (!state.gaia || !state.gaia.loaded) return;
  const qs = new URLSearchParams({
    magMin: $('magMin').value,
    magMax: $('magMax').value,
    classes: [...state.gaiaClasses].join(','),
    width: state.gaiaWidth || 2000,
  });
  const img = $('gaiaLayer');
  $('gaiaStat').textContent = 'rendering…';
  const t0 = performance.now();
  img.onload = () => {
    img.hidden = false;
    $('gaiaStat').textContent =
      `${fmt.int(state.gaia.stars)} catalogued · layer in ${Math.round(performance.now() - t0)} ms`;
    drawSkyStatic();   // graticule and labels ride on top of the new layer
  };
  img.src = `/api/gaia/layer.png?${qs}`;
}

async function loadSky() {
  const r = await fetch('/api/sky');
  state.sky = await r.json();
  $('chartNote').textContent =
    `${fmt.int(state.sky.hosts.length)} planet hosts · north up, east left · click any star to point at it`;
  drawSkyStatic();
  drawSkyOverlay();
  wireSkyEvents();
  loadGaia();
  // The opening search can win the race against this fetch; catch the ring up.
  if (state.target) skySelectHost(state.target.host);
}

/** Sampled polyline through the projection; gaps where consecutive points jump edges. */
function skyPath(g, geo, points) {
  let started = false, prev = null;
  for (const [ra, dec] of points) {
    const p = skyXY(ra, dec, geo);
    if (started && prev && Math.abs(p.x - prev.x) > geo.w / 3) started = false;
    started ? g.lineTo(p.x, p.y) : (g.moveTo(p.x, p.y), started = true);
    prev = p;
  }
}

function currentSiteLat() {
  const s = state.boot?.sites.find((x) => x.id === $('site').value);
  return s ? s.latitudeDeg : null;
}

function drawSkyStatic() {
  const sky = state.sky;
  const cv = $('chartStars');
  if (!sky || !cv.clientWidth) return;

  const g = setupSkyCanvas(cv);
  const geo = skyGeom();

  // -- graticule -------------------------------------------------------------
  g.lineWidth = 1;
  g.strokeStyle = 'rgba(46,58,74,.55)';
  for (const dec of [-60, -30, 0, 30, 60]) {
    g.beginPath();
    const pts = []; for (let ra = 0; ra <= 360; ra += 3) pts.push([ra, dec]);
    skyPath(g, geo, pts);
    g.stroke();
  }
  for (let ra = 0; ra < 360; ra += 30) {
    g.beginPath();
    const pts = []; for (let dec = -88; dec <= 88; dec += 4) pts.push([ra, dec]);
    skyPath(g, geo, pts);
    g.stroke();
  }
  // Outer boundary: the map is centred on RA 12h, so the ellipse edge is the
  // RA 0h/24h meridian. Up one side, down the other; the two meet at the poles,
  // where the projection collapses them to the same point.
  g.strokeStyle = 'rgba(58,72,92,.8)';
  g.beginPath();
  const edge = [];
  for (let dec = -90; dec <= 90; dec += 2) edge.push([0.001, dec]);
  for (let dec = 90; dec >= -90; dec -= 2) edge.push([359.999, dec]);
  skyPath(g, geo, edge);
  g.stroke();

  // RA hour labels along the equator
  g.font = '9px ui-monospace, Menlo, monospace';
  g.fillStyle = 'rgba(77,88,103,.9)';
  g.textAlign = 'center'; g.textBaseline = 'top';
  for (const hr of [0, 4, 8, 12, 16, 20]) {
    const p = skyXY(hr * 15 + 0.001, 0, geo);
    g.fillText(hr + 'h', p.x + 9, p.y + 3);
  }

  // -- the declination band this site never sees above 20° --------------------
  // Culmination altitude is 90 - |dec - lat|; below the 20° telescope floor means
  // |dec - lat| > 70. Ground truth of the same rule the sessions gate epochs on.
  const lat = currentSiteLat();
  const scope = selectedScope();
  // selectedScope() as well as instrumentByName(): the latter only searches the exoplanet
  // roster, so it answers undefined for every astrograph and the band was still being drawn
  // under a space telescope, which has no horizon for anything to be below.
  const spaceBased = !!(instrumentByName($('instrument').value)?.isSpaceBased || scope?.isSpaceBased);

  if ($('shadeKey')) $('shadeKey').hidden = spaceBased;
  if ($('cvzKey')) $('cvzKey').hidden = !spaceBased;
  if (spaceBased && state.platform) drawContinuousViewingZone(g, geo);

  if (lat !== null && !spaceBased) {
    g.fillStyle = 'rgba(255,110,100,.05)';
    g.strokeStyle = 'rgba(255,110,100,.22)';
    for (const [lo, hi] of [[lat + 70, 90], [-90, lat - 70]]) {
      if (hi <= -90 || lo >= 90 || hi <= lo) continue;
      g.beginPath();
      const poly = [];
      for (let ra = 0; ra <= 360; ra += 3) poly.push([ra === 360 ? 359.999 : ra, Math.max(-90, Math.min(90, lo))]);
      for (let dec = Math.max(-90, lo); dec <= Math.min(90, hi); dec += 2) poly.push([359.999, dec]);
      for (let ra = 360; ra >= 0; ra -= 3) poly.push([ra === 360 ? 359.999 : ra === 0 ? 0.001 : ra, Math.max(-90, Math.min(90, hi))]);
      for (let dec = Math.min(90, hi); dec >= Math.max(-90, lo); dec -= 2) poly.push([0.001, dec]);
      let first = true;
      for (const [ra, dec] of poly) {
        const p = skyXY(ra, dec, geo);
        first ? g.moveTo(p.x, p.y) : g.lineTo(p.x, p.y);
        first = false;
      }
      g.closePath();
      g.fill();
    }
    // stroke just the limiting parallels, cleaner than the whole polygon edge
    for (const dec of [lat + 70, lat - 70]) {
      if (dec <= -90 || dec >= 90) continue;
      g.beginPath();
      const pts = []; for (let ra = 0; ra <= 360; ra += 3) pts.push([ra, dec]);
      skyPath(g, geo, pts);
      g.stroke();
    }
  }

  // -- the background sky -------------------------------------------------------
  // Only when there is no Gaia layer underneath. With one, drawing the Bright Star
  // Catalogue again would paint 9 000 stars a second time, half a pixel off the
  // 7.4 million already rendered beneath them.
  if (!state.gaia || !state.gaia.loaded) {
    for (const [ra, dec, v] of sky.stars) {
      const p = skyXY(ra, dec, geo);
      const r = Math.max(0.4, 1.9 - 0.21 * v);
      g.globalAlpha = Math.max(0.16, Math.min(0.95, 1.05 - 0.125 * v));
      g.fillStyle = '#cfd8e6';
      g.beginPath(); g.arc(p.x, p.y, r, 0, 6.2832); g.fill();
    }
    g.globalAlpha = 1;
  }

  // -- first-magnitude IAU names ------------------------------------------------
  g.font = '9.5px -apple-system, system-ui, sans-serif';
  g.fillStyle = 'rgba(125,138,156,.85)';
  g.textAlign = 'left'; g.textBaseline = 'middle';
  for (const l of sky.labels) {
    const p = skyXY(l.ra, l.dec, geo);
    g.fillText(l.name, p.x + 5, p.y - 4);
  }
}

/** Hosts, selection and hover live on their own canvas so pointer motion never
    pays for the 9000-star background. */
function drawSkyOverlay() {
  const sky = state.sky;
  const cv = $('chartOverlay');
  if (!sky || !cv.clientWidth) return;

  const g = setupSkyCanvas(cv);
  const geo = skyGeom();

  for (const hst of sky.hosts) {
    const p = skyXY(hst.ra, hst.dec, geo);
    hst._x = p.x; hst._y = p.y;      // cached for hit-testing

    const matches = state.filter === 'all' || (state.filter === 'rv' ? hst.rv : hst.tr);
    g.globalAlpha = matches ? 0.8 : 0.14;
    g.fillStyle = '#5ecfff';
    g.beginPath(); g.arc(p.x, p.y, hst.n > 1 ? 2.1 : 1.5, 0, 6.2832); g.fill();
  }
  g.globalAlpha = 1;

  const ring = (hst, color, r) => {
    g.strokeStyle = color; g.lineWidth = 1.4;
    g.beginPath(); g.arc(hst._x, hst._y, r, 0, 6.2832); g.stroke();
  };
  if (state.freePoint && selectedScope()) {
    const p = skyXY(state.freePoint.ra, state.freePoint.dec, geo);
    g.strokeStyle = 'rgba(185,138,255,.95)';
    g.lineWidth = 1.4;
    g.beginPath(); g.arc(p.x, p.y, 7, 0, 6.2832); g.stroke();
    g.beginPath();
    g.moveTo(p.x - 13, p.y); g.lineTo(p.x - 4, p.y);
    g.moveTo(p.x + 4, p.y); g.lineTo(p.x + 13, p.y);
    g.moveTo(p.x, p.y - 13); g.lineTo(p.x, p.y - 4);
    g.moveTo(p.x, p.y + 4); g.lineTo(p.x, p.y + 13);
    g.stroke();
  }
  if (state.skyHover && state.skyHover !== state.skySel) ring(state.skyHover, 'rgba(94,207,255,.9)', 6);
  if (state.skySel) {
    ring(state.skySel, 'rgba(255,180,84,.95)', 6.5);
    g.strokeStyle = 'rgba(255,180,84,.5)';
    g.beginPath();
    g.moveTo(state.skySel._x - 12, state.skySel._y); g.lineTo(state.skySel._x - 7, state.skySel._y);
    g.moveTo(state.skySel._x + 7, state.skySel._y); g.lineTo(state.skySel._x + 12, state.skySel._y);
    g.stroke();
  }
}

/**
 * Point the telescope at a sky position, snapping onto a real catalogue star when one
 * is close enough to be what the click meant.
 *
 * The rendered layer cannot be hit-tested (it is pixels), so the position goes back to
 * the catalogue's own cone search, and the brightest star inside one chart pixel's worth
 * of sky wins. That is what keeps all 7.4 million individually pointable.
 */
async function aimAt(pos) {
  // One chart pixel is about 0.18 deg of sky at this width; a 12 arcmin cone is a few
  // pixels, which is the tolerance a click actually has.
  const geo = skyGeom();
  state.freePoint = pos;
  state.gaiaPick = null;
  state.skySel = null;
  state.capObject = null;

  let picked = null;
  if (state.gaia && state.gaia.loaded) {
    try {
      const r = await fetch(`/api/gaia/at?ra=${pos.ra.toFixed(5)}&dec=${pos.dec.toFixed(5)}&radiusArcmin=12&max=12`);
      const hits = await r.json();
      if (hits.length) {
        // Brightest inside the pick radius, not nearest: a click on a visible dot means
        // the dot, and the dot is whichever star is drawn brightest there.
        picked = hits.reduce((a, b) => (b.vMag < a.vMag ? b : a));
      }
    } catch { /* the layer still points, just without a name */ }
  }

  if (picked) {
    state.gaiaPick = picked;
    state.freePoint = { ra: picked.raDeg, dec: picked.decDeg };
    state.capObject = `Gaia V${picked.vMag.toFixed(1)} ${picked.spectralClass}`;
    $('capRa').value = picked.raDeg.toFixed(4);
    $('capDec').value = picked.decDeg.toFixed(4);

    $('targetCard').hidden = false;
    $('tName').textContent = state.capObject;
    $('tStatus').textContent = picked.spectralClass === '?' ? 'no colour' : `class ${picked.spectralClass}`;
    $('tSub').textContent = 'Gaia DR3 catalogue star, picked off the chart';
    $('tFacts').innerHTML = [
      `<dt>V</dt><dd>${fmt.num(picked.vMag, 2)}</dd>`,
      picked.colourBv !== null ? `<dt>B−V</dt><dd>${fmt.num(picked.colourBv, 3)}</dd>` : '',
      picked.teffK !== null ? `<dt>T<sub>eff</sub></dt><dd>${fmt.int(picked.teffK)} K</dd>` : '',
      `<dt>α, δ</dt><dd>${fmt.num(picked.raDeg, 4)}, ${fmt.num(picked.decDeg, 4)}</dd>`,
      `<dt>from click</dt><dd>${fmt.num(picked.separationArcsec / 60, 1)}′</dd>`,
    ].join('');
  } else {
    $('capRa').value = pos.ra.toFixed(4);
    $('capDec').value = pos.dec.toFixed(4);
  }

  drawSkyOverlay();
  scheduleForecast();
}

function skySelectHost(hostName) {
  if (!state.sky || !hostName) return;
  const hst = state.sky.hosts.find((x) => x.name.toLowerCase() === hostName.toLowerCase());
  if (hst) { state.skySel = hst; drawSkyOverlay(); }
}

function skyHitTest(mx, my) {
  if (!state.sky) return null;
  let best = null, bestD = 81;   // 9 px pick radius, squared
  for (const hst of state.sky.hosts) {
    if (hst._x === undefined) continue;
    const dx = hst._x - mx, dy = hst._y - my;
    const d = dx * dx + dy * dy;
    if (d < bestD) { bestD = d; best = hst; }
  }
  return best;
}

function wireSkyEvents() {
  const cv = $('chartOverlay');
  const tip = $('chartTip');
  let raf = null;

  // --- panning and zoom ------------------------------------------------------
  // A drag moves the sky; a click that never moved is still a click, so the two do not
  // fight over the same gesture.
  let drag = null;

  cv.addEventListener('mousedown', (e) => {
    drag = { x: e.clientX, y: e.clientY, panX: view.panX, panY: view.panY, moved: 0 };
    cv.classList.add('grabbing');
  });

  window.addEventListener('mousemove', (e) => {
    if (!drag) return;
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    drag.moved = Math.max(drag.moved, Math.abs(dx) + Math.abs(dy));
    view.panX = drag.panX + dx;
    view.panY = drag.panY + dy;
    redrawChart();
  });

  window.addEventListener('mouseup', () => {
    if (drag) cv.classList.remove('grabbing');
    // Swallow the click that ends a real drag; release it for a plain click.
    setTimeout(() => { drag = null; }, 0);
  });

  cv.addEventListener('wheel', (e) => {
    e.preventDefault();
    const box = cv.getBoundingClientRect();
    const mx = e.clientX - box.left, my = e.clientY - box.top;
    const before = skyInverse(mx, my, skyGeom());

    const next = Math.max(1, Math.min(8, view.zoom * (e.deltaY < 0 ? 1.18 : 1 / 1.18)));
    if (next === view.zoom) return;
    view.zoom = next;

    // Zoom about the cursor: whatever sky was under it stays under it.
    if (before) {
      clampView();
      const after = skyXY(before.ra, before.dec, skyGeom());
      view.panX += mx - after.x;
      view.panY += my - after.y;
    }
    redrawChart();
    $('zoomBadge').hidden = view.zoom <= 1.001;
    $('zoomBadge').textContent = `×${view.zoom.toFixed(1)}, double-click to reset`;
  }, { passive: false });

  cv.addEventListener('dblclick', () => {
    view.zoom = 1; view.panX = 0; view.panY = 0;
    redrawChart();
    $('zoomBadge').hidden = true;
  });

  cv.addEventListener('mousemove', (e) => {
    const box = cv.getBoundingClientRect();
    const mx = e.clientX - box.left, my = e.clientY - box.top;
    if (raf) return;
    raf = requestAnimationFrame(() => {
      raf = null;
      const hit = skyHitTest(mx, my);
      if (hit !== state.skyHover) {
        state.skyHover = hit;
        cv.classList.toggle('hover', !!hit);
        drawSkyOverlay();
      }
      if (hit) {
        const kinds = [hit.rv ? 'RV' : null, hit.tr ? 'transit' : null].filter(Boolean).join(' + ') || 'catalogued';
        tip.innerHTML = `<b>${hit.name}</b> <span class="tipsub">V ${fmt.num(hit.v, 1)} · ` +
          `${hit.n} planet${hit.n > 1 ? 's' : ''} · ${kinds}</span>`;
        tip.hidden = false;
        const tw = tip.offsetWidth, box2 = $('skychart');
        tip.style.left = Math.min(mx + 14, box2.clientWidth - tw - 6) + 'px';
        tip.style.top = Math.max(4, my - 30) + 'px';
      } else {
        tip.hidden = true;
      }
    });
  });

  cv.addEventListener('mouseleave', () => {
    state.skyHover = null;
    tip.hidden = true;
    cv.classList.remove('hover');
    drawSkyOverlay();
  });

  cv.addEventListener('click', (e) => {
    if (drag && drag.moved > 4) return;    // that gesture was a pan
    const hit = state.skyHover;
    if (hit) {
      state.skySel = hit;
      // Selecting on the chart drives the list: search on the host name so every
      // planet of that system is right there, and select the system's best entry.
      $('search').value = hit.name;
      search(hit.name);
      selectTarget(hit.planet);
      return;
    }

    // Bare sky. With an astrograph selected, the click IS the pointing: the mod's
    // manual-pointing entry, done by touching the map instead of typing coordinates.
    if (!selectedScope()) return;
    const box = cv.getBoundingClientRect();
    const pos = skyInverse(e.clientX - box.left, e.clientY - box.top, skyGeom());
    if (!pos) return;
    aimAt(pos);
  });
}

/**
 * The continuous-viewing zone, the orbital counterpart of the ground map's never-visible
 * declination band, and the opposite sign of the same idea: the ground band is where a site
 * can NEVER point, this is where the spacecraft is NEVER occulted.
 *
 * It is a small circle rather than a parallel because it is centred on the ORBIT pole, not on
 * the celestial one. The pole sits at declination 90 - inclination, at the right ascension of
 * the ascending node minus 90 degrees, and it drifts westward with the node (about -6.6 deg per
 * day for Hubble), which is exactly why the panel offers the node as a control: it is what puts
 * a given target inside the zone or outside it.
 */
function drawContinuousViewingZone(g, geo) {
  const p = state.platform;
  const rDeg = p.derived.continuousViewingHalfWidthDeg;
  if (!(rDeg > 0)) return;

  const rad = Math.PI / 180;
  const poleDec = 90 - p.orbit.inclinationDeg;
  const poleRa = p.orbit.raanDeg - 90;

  // Orthonormal frame about the pole, so the circle can be swept as one rotation in it.
  const n = [Math.cos(poleDec * rad) * Math.cos(poleRa * rad),
             Math.cos(poleDec * rad) * Math.sin(poleRa * rad),
             Math.sin(poleDec * rad)];
  // Any vector not parallel to n; the celestial pole unless n IS the celestial pole.
  const seed = Math.abs(n[2]) > 0.9 ? [1, 0, 0] : [0, 0, 1];
  const e1 = norm3(cross3(seed, n));
  const e2 = cross3(n, e1);

  const pts = [];
  for (let t = 0; t <= 360; t += 4) {
    const c = Math.cos(rDeg * rad), s = Math.sin(rDeg * rad);
    const ct = Math.cos(t * rad), st = Math.sin(t * rad);
    const v = [c * n[0] + s * (ct * e1[0] + st * e2[0]),
               c * n[1] + s * (ct * e1[1] + st * e2[1]),
               c * n[2] + s * (ct * e1[2] + st * e2[2])];
    let ra = Math.atan2(v[1], v[0]) / rad; if (ra < 0) ra += 360;
    pts.push([Math.min(359.999, Math.max(0.001, ra)), Math.asin(Math.max(-1, Math.min(1, v[2]))) / rad]);
  }

  // Stroked, not filled: skyPath breaks the run where the circle crosses the RA 0h seam, so a
  // fill would close across the whole map. The outline is what carries the information anyway.
  g.save();
  g.strokeStyle = 'rgba(126,231,135,.55)';
  g.setLineDash([4, 3]);
  g.lineWidth = 1.2;
  g.beginPath();
  skyPath(g, geo, pts);
  g.stroke();
  g.restore();
}

function cross3(a, b) {
  return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
}
function norm3(v) {
  const m = Math.hypot(v[0], v[1], v[2]) || 1;
  return [v[0] / m, v[1] / m, v[2] / m];
}

/* -------------------------------------------------------------- spacecraft */
/* Flying the orbital half. The four elements are controls rather than a datasheet: each
   one changes something you can see in the next frame, and the derived line under them
   is the server's arithmetic rather than a second copy of it here. */

let orbitTimer = null;

async function loadPlatform(id) {
  if (!id) { $('spacecraftBlock').hidden = true; return; }
  const p = await (await fetch(`/api/platforms/${encodeURIComponent(id)}`)).json();
  state.platform = p;
  fillPlatform(p);
  drawSkyStatic();     // the continuous-viewing zone belongs to the orbit
  scheduleForecast();
}

function fillPlatform(p) {
  $('scName').textContent = p.name;
  $('scAlt').value = p.orbit.altitudeKm.toFixed(0);
  $('scInc').value = p.orbit.inclinationDeg.toFixed(2);
  $('scRaan').value = p.orbit.raanDeg.toFixed(0);
  $('scPhase').value = p.orbit.phaseDeg.toFixed(0);

  const d = p.derived;
  $('scDerived').textContent =
    `${fmt.num(d.periodMinutes, 1)} min orbit · Earth ${fmt.num(d.earthAngularRadiusDeg, 1)}° radius · ` +
    `continuous-viewing zone ±${fmt.num(d.continuousViewingHalfWidthDeg, 1)}° about the orbit pole · ` +
    `node ${fmt.num(d.nodalRegressionDegPerDay, 2)}°/day`;

  const c = p.constraints;
  $('scConstraints').textContent = c
    ? `Avoidance: Sun ${c.sunAvoidanceDeg}°, sunlit limb ${c.brightLimbAvoidanceDeg}°, ` +
      `dark limb ${c.darkLimbAvoidanceDeg}°, Moon ${c.moonAvoidanceDeg}°. ` +
      `Pointing held to ${c.pointingJitterArcsecRms}″ rms on ` +
      (c.controlMode === 'MomentumExchange' ? 'reaction wheels' : c.controlMode) + '.'
    : '';
  $('scNote').textContent = p.note || '';
}

for (const [id, field] of [['scAlt', 'altitudeKm'], ['scInc', 'inclinationDeg'],
                           ['scRaan', 'raanDeg'], ['scPhase', 'phaseDeg']]) {
  $(id).addEventListener('change', async () => {
    if (!state.platform) return;
    const body = {}; body[field] = $(id).valueAsNumber;
    const p = await (await fetch(`/api/platforms/${encodeURIComponent(state.platform.id)}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    })).json();
    state.platform = p;
    fillPlatform(p);      // the server clamps, so the field is re-read rather than trusted
    drawSkyStatic();
    scheduleForecast();
  });
}

/** One revolution of yes/no for the current aim, with the reason for each no. */
async function loadOrbitVisibility() {
  const scope = selectedScope();
  if (!scope || !scope.isSpaceBased) { $('orbitPanel').hidden = true; return; }

  const qs = new URLSearchParams({ ra: $('capRa').value, dec: $('capDec').value, samples: 120 });
  const d = await (await fetch(`/api/platforms/${encodeURIComponent(scope.platform)}/conditions?${qs}`)).json();
  if (d.error) { $('orbitPanel').hidden = true; return; }

  $('orbitPanel').hidden = false;
  fillPlatform(d.platform);

  const c = d.conditions;
  $('orbitNote').textContent =
    `${d.platform.name}, one ${fmt.num(d.platform.derived.periodMinutes, 1)}-minute revolution from now`;

  const strip = $('orbitStrip');
  strip.innerHTML = '';
  for (const p of d.orbitTrack) {
    const s = document.createElement('span');
    s.className = p.observable ? 'ok' : classForBlock(p.blockedBy);
    s.title = `+${p.minutes.toFixed(1)} min · ` + (p.observable ? 'observable' : p.blockedBy);
    strip.appendChild(s);
  }

  const open = d.orbitTrack.filter((p) => p.observable).length / d.orbitTrack.length;
  $('orbitHint').textContent = c.observable
    ? `Observable now. ${(open * 100).toFixed(0)}% of the orbit is open on this field; ` +
      `longest single exposure ${fmt.int(c.maxContiguousExposureSeconds)} s. ` +
      `Sun ${fmt.num(c.sunAngleDeg, 0)}° away, Earth limb ${fmt.num(c.earthLimbAngleDeg, 0)}° ` +
      `(${c.limbIsSunlit ? 'sunlit' : 'dark'}), sky ${fmt.num(c.skyVMagPerArcsec2, 1)} V mag/arcsec².`
    : d.nextWindowUtc
      ? `${c.blockedBy} right now. Next window ${d.nextWindowUtc}; the capture will be scheduled there.`
      : `${d.blockedBy} for the whole of the next 24 hours. ` +
        (String(d.blockedBy || '').includes('solar')
          ? 'The solar avoidance cone moves with the Earth’s own orbit, so it clears in weeks rather than orbits: this field is out of season.'
          : 'Try a field nearer the orbit pole.');
}

function classForBlock(reason) {
  const r = String(reason || '');
  if (r.includes('occulted')) return 'occ';
  if (r.includes('solar')) return 'sun';
  if (r.includes('Moon') || r.includes('moon')) return 'moon';
  return 'limb';
}

boot();
