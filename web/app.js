/* ExoInstruments Studio - browser client.
   Talks to the engine only through /api/*. Nothing here knows the backend is C#,
   which is the point: a WebAssembly or Python-backed engine would serve the same
   shapes and this file would not change. */

'use strict';

const $ = (id) => document.getElementById(id);

const state = {
  boot: null,
  mode: 'astro',     // astro | exo. The landing mode is astrophotography; see setMode.
  modeSeq: 0,        // bumped on every mode change; the receipt a late await checks. See ofThisMode.
  capture: null,     // the stored frame the calibration panel is working against
  masters: {},       // {Bias|Dark|Flat: {id, ...}} chosen for the next reduction
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
  $('instrument').onchange = onInstrumentChange;

  const site = $('site');
  site.innerHTML = b.sites.map((s) => `<option value="${s.id}">${s.name} · ${s.country}</option>`).join('');
  site.onchange = onSiteChange;

  for (const btn of document.querySelectorAll('#modeBar .mode')) {
    btn.onclick = () => setMode(btn.dataset.mode);
  }

  // ASTROPHOTOGRAPHY IS WHERE THE PAGE LANDS. setMode fills the instrument list, so nothing
  // before this point may assume a selection exists.
  setMode('astro', { initial: true });
  onSiteChange();

  // The chart data loads in parallel with the opening search; neither waits on the other.
  loadSky();

  await openingTarget();
}

/**
 * The two modes, and what actually differs between them. This is not a filter on one list: the
 * two halves of this studio point different instruments at different questions, and the map wants
 * to show different things for each.
 *
 *   * ASTROPHOTOGRAPHY offers the astrographs, and every layer the sky has - the whole Gaia
 *     catalogue, the bright-star background, and the planet hosts over the top of them, because
 *     a host star is just another star to point a camera at.
 *   * EXOPLANET DETECTION offers the detection instruments, and draws HOST STARS ONLY. The Gaia
 *     layer comes off, and so does the bright-star background. That is not decoration: in this
 *     mode every target is a host, so 7.4 million stars behind them are 7.4 million things that
 *     cannot be selected, and they hide the few thousand that can.
 */
function setMode(mode, opts = {}) {
  if (mode !== 'astro' && mode !== 'exo' && mode !== 'research') mode = 'astro';
  if (!opts.initial && mode === state.mode) return;
  state.mode = mode;
  state.modeSeq++;

  for (const btn of document.querySelectorAll('#modeBar .mode')) {
    const on = btn.dataset.mode === mode;
    btn.classList.toggle('on', on);
    btn.setAttribute('aria-selected', on ? 'true' : 'false');
  }
  document.body.dataset.mode = mode;

  // The instrument list holds only this mode's instruments. Leaving the other mode's in and
  // hiding them would let a stale selection survive a mode change, which is how you end up
  // photographing with a spectrograph.
  const inst = $('instrument');
  inst.innerHTML = mode === 'astro'
    ? state.telescopes.map((t) => `<option value="visual:${t.name}">${t.displayName}</option>`).join('')
    : state.boot.instruments.map((i) => `<option value="${i.name}">${i.displayName}</option>`).join('');
  inst.value = mode === 'astro'
    ? `visual:${(state.telescopes.find((t) => !t.isSpaceBased) || state.telescopes[0]).name}`
    : (state.boot.instruments.find((i) => i.name === 'HARPS') || state.boot.instruments[0]).name;

  // Anything the other mode put on the page goes, rather than lingering under the new one.
  stopCampaign();
  state.target = null;
  state.freePoint = null;
  state.skySel = null;
  state.capture = null;
  state.masters = {};
  $('targetCard').hidden = true;
  $('calibrationPanel').hidden = true;
  $('capturePanel').hidden = true;
  for (const id of ['clockbar', 'skyPanel', 'seriesPanel', 'foldPanel', 'resultPanel', 'orbitPanel']) {
    $(id).hidden = true;
  }

  // RESEARCH USES THE SAME TWO COLUMNS as the other modes: what to look at on the left, what came
  // back on the right. It takes no instrument, site or clock, because the observation already
  // happened and belongs to somebody else, so those blocks are the ones that go rather than the
  // whole layout. The page is not a different application in this mode, only a different question.
  const research = mode === 'research';
  for (const id of ['rsSetup', 'rsSearchBlock', 'rsSweepBlock', 'rsRun', 'rsAboutPanel',
                    'rsLookPanel', 'rsRunsPanel']) {
    $(id).hidden = !research;
  }
  $('rsSweepPanel').hidden = true;
  for (const id of ['rsCurvePanel', 'rsFoldPanel', 'rsResultPanel', 'rsInspectPanel', 'rsSubmitPanel'])
    $(id).hidden = true;
  $('rsError').hidden = true;

  // The simulator's own setup blocks, which have no meaning against an archive observation.
  for (const el of document.querySelectorAll('.panel.setup > section.block')) {
    if (!el.id.startsWith('rs')) el.hidden = research ? true : el.hidden;
  }
  $('observe').hidden = research;
  $('chartPanel').hidden = research;
  if (research) {
    $('forecastPanel').hidden = true;
    $('siteBlock').hidden = true;
    $('captureSetup').hidden = true;
    loadResearchRuns();
    return;
  }
  // Coming back out of research: the simulator's blocks return, and hidden state is recomputed
  // by onInstrumentChange below rather than remembered here.
  for (const el of document.querySelectorAll('.panel.setup > section.block')) {
    if (!el.id.startsWith('rs') && !['targetCard', 'spacecraftBlock', 'captureSetup'].includes(el.id)) {
      el.hidden = false;
    }
  }
  $('siteBlock').hidden = false;

  onInstrumentChange();
  applyGaiaVisibility();
  drawSkyStatic();
  drawSkyOverlay();
  if (!opts.initial) openingTarget();
}

/**
 * ONE MODE OWNS THE PAGE AT A TIME, INCLUDING THE ANSWERS STILL IN THE AIR. Every panel on the
 * right is filled by something that was awaited - a capture, a forecast, an archive search, a
 * stream message - and an await outlives a click on the mode bar. Whatever was in flight when
 * the mode changed was asked for by a page that no longer exists, so it is dropped rather than
 * drawn: this is what stops an exoplanet detection table from appearing under the
 * astrophotography chart a moment after the mode changed.
 *
 * Take the receipt before the await, check it after:
 *
 *     const mine = modeReceipt();
 *     const d = await (await fetch(...)).json();
 *     if (!ofThisMode(mine)) return;
 *
 * A counter rather than the mode name, because leaving a mode and coming back builds a fresh
 * page too: the target, the frame and the run are all cleared on the way out, so an answer from
 * the previous visit describes nothing that is still on screen.
 */
function modeReceipt() { return state.modeSeq; }
function ofThisMode(receipt) { return state.modeSeq === receipt; }

/** Whether the Gaia layer belongs on the chart at all right now. See setMode. */
function gaiaWanted() {
  return state.mode === 'astro' && !!(state.gaia && state.gaia.loaded);
}

function applyGaiaVisibility() {
  const want = gaiaWanted();
  $('gaiaBar').hidden = !want;
  const img = $('gaiaLayer');
  if (!want) img.hidden = true;
  else if (img.getAttribute('src')) img.hidden = false;
  else loadGaiaLayer();
  $('starLayerLabel').textContent =
    state.mode === 'exo' ? 'planet hosts only'
      : (state.gaia && state.gaia.loaded ? 'Gaia DR3' : 'Bright Star Catalogue');

  // The host and selection keys describe marks that exist in exoplanet mode only, so they go with
  // them. A legend entry for something never drawn is an instruction to look for it.
  const exo = state.mode === 'exo';
  if ($('hostKey')) $('hostKey').hidden = !exo;
  if ($('selKey')) $('selKey').hidden = !exo;

  updateChartNote();
}

function updateChartNote() {
  if (!state.sky) return;
  $('chartNote').textContent = state.mode === 'exo'
    ? `${fmt.int(state.sky.hosts.length)} planet hosts · north up, east left · click a host to observe it`
    : (gaiaWanted() ? `${fmt.int(state.gaia.stars)} Gaia stars` : 'Bright Star Catalogue') +
      ' · north up, east left · click any patch of sky to aim at it';
}

/** The demo each mode opens on: a real object, so the page is never staring at an empty chart. */
async function openingTarget() {
  const q = state.mode === 'astro' ? 'M 51' : '51 Peg b';
  $('search').value = q;
  await search(q);
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
  // The frame panel only reappears once there IS a frame; showing it empty after an instrument
  // change would present the last telescope's picture as this one's.
  if (!isAstrograph || !state.capture) $('capturePanel').hidden = true;
  if (!isAstrograph || !state.capture) $('calibrationPanel').hidden = true;

  for (const id of ['clockbar', 'skyPanel', 'seriesPanel', 'foldPanel', 'resultPanel']) {
    if (isAstrograph) $(id).hidden = true;
  }
  // Leaving astrograph mode drops the frame itself, not just its panel: the next capture
  // starts from nothing rather than replacing a picture of a different telescope. The masters
  // go with it, since each was checked against that exposure and describes no other.
  if (!isAstrograph) {
    $('captureImg').removeAttribute('src');
    $('captureLinks').innerHTML = '';
    $('captureReport').textContent = '';
    state.capture = null;
    state.masters = {};
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

  const mine = modeReceipt();
  const qs = new URLSearchParams({ q: q || '', limit: SEARCH_LIMIT });
  if (state.filter === 'rv') qs.set('rv', 'true');
  if (state.filter === 'transit') qs.set('transiting', 'true');

  const r = await fetch(`/api/targets?${qs}`);
  const hits = await r.json();
  if (!ofThisMode(mine)) return;
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
  const mine = modeReceipt();
  const qs = new URLSearchParams({ q: q || 'type:nebula', site: $('site').value, limit: 60 });
  const r = await fetch(`/api/pointing-search?${qs}`);
  if (!r.ok) return;
  const d = await r.json();
  if (!ofThisMode(mine)) return;
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
  const mine = modeReceipt();
  const r = await fetch(`/api/targets/${encodeURIComponent(name)}`);
  if (!r.ok) return;
  const { target, system } = await r.json();
  if (!ofThisMode(mine)) return;
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
  const mine = modeReceipt();
  es.onmessage = (ev) => {
    if (!ofThisMode(mine)) { es.close(); return; }
    const msg = JSON.parse(ev.data);
    state.campaign = msg.campaign;
    if (msg.points && msg.points.length) state.points.push(...msg.points);
    render();
  };
  es.onerror = () => { /* the browser retries on its own */ };
}

/**
 * End the observing run and forget it.
 *
 * Hiding its panels is not enough, and that was the bug: the stream stayed open, every message
 * called render(), and render() unhides the series, the fold and the detection table. A run left
 * behind in exoplanet mode therefore re-opened its own panels a second or two after the mode
 * changed, and the detection sat under the astrophotography chart as though it belonged there.
 *
 * The run is stopped on the server rather than merely dropped here, because its id goes with the
 * mode: nothing can ever show it again, and a campaign nobody is watching would otherwise go on
 * collecting epochs for as long as the page is open.
 */
function stopCampaign() {
  if (state.stream) { state.stream.close(); state.stream = null; }
  if (state.campaign) {
    fetch(`/api/campaigns/${state.campaign.id}/stop`, { method: 'POST' }).catch(() => {});
  }
  state.campaign = null;
  state.points = [];
  state.startUt = null;
  $('signalRows').innerHTML = '';
  $('verdict').innerHTML = '';
  $('analyse').textContent = 'Analyse';
  $('analyse').disabled = true;
  $('pause').disabled = true;
  $('warp').disabled = true;
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
  const mine = modeReceipt();
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
    if (!ofThisMode(mine)) return;
    if (!r.ok) {
      $('captureError').hidden = false;
      $('captureError').textContent = data.error || 'Capture failed.';
      return;
    }

    $('capturePanel').hidden = false;
    $('captureImg').src = 'data:image/png;base64,' + data.png;

    // A new frame invalidates every master chosen for the old one: they were checked against
    // that exposure's geometry, binning and pedestal, and silently carrying them over is how a
    // master from a different binning ends up subtracted pixel for pixel from something it does
    // not describe.
    state.capture = data.id;
    state.masters = {};
    renderMasters();
    applyStretch();          // re-render this frame in whichever view is selected
    $('calibrationPanel').hidden = false;
    $('calNote').textContent =
      `bias, dark and flat for this ${$('capExp').value} s frame at binning ${$('capBin').value}`;
    $('reduceOut').textContent = '';
    $('calError').hidden = true;
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

    // GROUPED, NOT CONCATENATED. This used to be every entry joined with a middle dot, which on
    // a full install is a paragraph of absolute paths with the interesting part buried in it. The
    // label is what matters; the directory is the same for all of them and is available on hover.
    // Deep sky patches collapse to a count, because six lines saying the same thing is not six
    // pieces of information.
    const esc = (t) => String(t).replace(/[&<>"]/g, (c) =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const patches = [];
    const rows = [];
    for (const line of paths) {
      const at = line.indexOf(': ');
      const label = at > 0 ? line.slice(0, at) : line;
      const value = at > 0 ? line.slice(at + 2) : '';
      if (/^star field patch$/i.test(label)) { patches.push(value.split(',')[0].trim()); continue; }
      const leaf = value.includes('/') ? value.slice(value.lastIndexOf('/') + 1) : value;
      rows.push(`<span class="dataItem" title="${esc(value)}"><b>${esc(label)}</b>${
        value ? ' ' + esc(leaf) : ''}</span>`);
    }
    if (patches.length) {
      rows.push(`<span class="dataItem" title="${esc(patches.join(', '))}">` +
        `<b>deep star field patches</b> ${patches.length} installed</span>`);
    }
    $('captureData').innerHTML = rows.join('');
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
  const mine = modeReceipt();
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
  if (!ofThisMode(mine)) return;
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

/**
 * Redraw the chart whenever its BOX changes, not only when the WINDOW does.
 *
 * THE GAP THIS CLOSES. Two things are stacked here and they scale differently: the Gaia layer is
 * an `<img>` that CSS resizes continuously and for free, and the graticule, labels and overlay
 * are canvas BITMAPS sized from clientWidth at the moment they were drawn. Let the element's box
 * change without a redraw and the two disagree, so every star sits slightly off the overlay drawn
 * on top of it. Until now the only trigger was `window.resize`, which is not the same event: the
 * chart's box can change because a neighbour reflowed, and the window never moved.
 *
 * NOT REPRODUCED ON THIS MACHINE, and worth saying so rather than inventing a symptom. macOS
 * draws overlay scrollbars, so the capture and calibration panels appearing below do not narrow
 * the column - measured, 833 px before and after. On a platform with classic scrollbars that same
 * reflow takes about 15 px off the width, which is exactly the case `window.resize` misses. This
 * is here for that, and it costs one observer that no-ops whenever the size has not changed.
 */
if (typeof ResizeObserver !== 'undefined') {
  let lastW = 0, lastH = 0, pending = null;
  new ResizeObserver((entries) => {
    const box = entries[0].contentRect;
    const w = Math.round(box.width), h = Math.round(box.height);
    if (w === lastW && h === lastH) return;      // a reflow that did not move this element
    lastW = w; lastH = h;
    if (!w || !h) return;
    clearTimeout(pending);
    pending = setTimeout(redrawChart, 60);
  }).observe($('skychart'));
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
  applyGaiaVisibility();     // which decides whether the layer is wanted in this mode at all
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
  if (!gaiaWanted()) return;
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
  updateChartNote();
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
  //
  // And not at all in exoplanet mode, where every selectable object is a host: a
  // background of stars that cannot be clicked is 9 000 things competing with the few
  // thousand that can. See setMode.
  if (state.mode !== 'exo' && !gaiaWanted()) {
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
  // These label stars in the background layer, so they go with it in exoplanet mode:
  // a name floating over an empty patch of chart labels nothing.
  if (state.mode !== 'exo') {
    g.font = '9.5px -apple-system, system-ui, sans-serif';
    g.fillStyle = 'rgba(125,138,156,.85)';
    g.textAlign = 'left'; g.textBaseline = 'middle';
    for (const l of sky.labels) {
      const p = skyXY(l.ra, l.dec, geo);
      g.fillText(l.name, p.x + 5, p.y - 4);
    }
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

  // HOSTS BELONG TO EXOPLANET MODE AND TO NOTHING ELSE. They are not drawn in astrophotography
  // mode, not hovered, and not clickable: there, a host is just a star, and the chart already
  // has 7.4 million of those to point at. Marking four thousand of them for a reason that has
  // nothing to do with taking a picture is a claim that they are special to this instrument.
  //
  // THE CACHED POSITIONS ARE CLEARED RATHER THAN LEFT, which is the part that actually matters.
  // skyHitTest walks these, so a host carrying last-mode coordinates would still answer a click
  // after it stopped being drawn: an invisible target under the cursor. Deleting them means the
  // hit test has nothing to find even if it is reached.
  if (state.mode !== 'exo') {
    for (const hst of sky.hosts) { delete hst._x; delete hst._y; }
  } else {
    for (const hst of sky.hosts) {
      const p = skyXY(hst.ra, hst.dec, geo);
      hst._x = p.x; hst._y = p.y;      // cached for hit-testing

      const matches = state.filter === 'all' || (state.filter === 'rv' ? hst.rv : hst.tr);
      g.globalAlpha = matches ? 0.8 : 0.14;
      g.fillStyle = '#5ecfff';
      g.beginPath(); g.arc(p.x, p.y, hst.n > 1 ? 2.1 : 1.5, 0, 6.2832); g.fill();
    }
    g.globalAlpha = 1;
  }

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
  if (state.mode !== 'exo') return;     // no host ring on a chart with no hosts on it
  if (!state.sky || !hostName) return;
  const hst = state.sky.hosts.find((x) => x.name.toLowerCase() === hostName.toLowerCase());
  if (hst) { state.skySel = hst; drawSkyOverlay(); }
}

function skyHitTest(mx, my) {
  // Hosts are exoplanet mode's alone. Refusing here rather than only declining to draw them is
  // what keeps a click on bare sky in astrophotography mode reaching aimAt: a host that answered
  // the hit test while invisible would swallow the pointing instead.
  if (state.mode !== 'exo') return null;
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
  const mine = modeReceipt();
  const scope = selectedScope();
  if (!scope || !scope.isSpaceBased) { $('orbitPanel').hidden = true; return; }

  const qs = new URLSearchParams({ ra: $('capRa').value, dec: $('capDec').value, samples: 120 });
  const d = await (await fetch(`/api/platforms/${encodeURIComponent(scope.platform)}/conditions?${qs}`)).json();
  if (!ofThisMode(mine)) return;
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

/* ---------------------------------------------------------------- stretch */
/* Why the picture on the page and the picture in the FITS viewer are not the same picture.

   They are the same PIXELS. What differs is where black and white sit, and on a deep-sky frame
   that decides essentially everything: the subject occupies a few tens of ADU on top of a sky
   pedestal, out of a converter counting to tens of thousands. The page has always chosen those
   levels from the frame; a viewer opened with defaults has not. Switching between the three here
   costs one request and no recomputation of the frame — the stored ADU are rendered again. */

state.stretch = 'asinh';

async function applyStretch() {
  if (!state.capture) return;
  $('stretchNote').textContent = 're-rendering…';
  try {
    const r = await fetch(`/api/captures/${state.capture}/render?stretch=${state.stretch}`);
    const d = await r.json();
    if (!r.ok) {
      // Say so rather than leaving the previous view up under the newly-selected chip, which
      // reads as "this stretch looks identical" instead of "that did not happen".
      $('stretchNote').textContent = d.error || 'That view could not be rendered.';
      return;
    }
    $('captureImg').src = 'data:image/png;base64,' + d.png;
    $('stretchNote').textContent =
      `black ${fmt.num(d.blackAdu, 1)} ADU · white ${fmt.num(d.whiteAdu, 1)} ADU · ` +
      `${((d.whiteAdu - d.blackAdu) / d.maxAdu * 100).toFixed(2)} % of the converter's ` +
      `${fmt.num(d.maxAdu, 0)} ADU range — ${d.note}`;
  } catch (e) {
    $('stretchNote').textContent = String(e);
  }
}

for (const chip of document.querySelectorAll('#stretchChips .chip')) {
  chip.onclick = () => {
    state.stretch = chip.dataset.stretch;
    for (const c of document.querySelectorAll('#stretchChips .chip')) {
      c.classList.toggle('on', c === chip);
    }
    applyStretch();
  };
}

/* ------------------------------------------------------------- calibration */
/* Bias, dark and flat, and the reduction that uses them.

   The server has been able to do all of this since the calibration work landed and nothing on
   the page called any of it, so the only way to take a bias was curl. Two things are offered
   here and they are not equivalent:

     * MASTERS THIS PIPELINE BUILDS, which remove exactly the patterns it put in. Useful, and
       circular: a defect the forward model does not have cannot be found by a calibration frame
       the forward model wrote.
     * A MASTER THE OBSERVER UPLOADS, which is the one that breaks the circle. A real camera's
       flat carries dust motes, accessory vignetting and tree rings, none of which this model
       generates and two of which it explicitly declines to invent. */

const CAL_KINDS = ['Bias', 'Dark', 'Flat'];

function calBusy(on) {
  for (const b of document.querySelectorAll('#calibrationPanel button')) b.disabled = on;
  $('upFile').disabled = on;
}

function calFail(message) {
  $('calError').hidden = false;
  $('calError').textContent = message;
}

/** Every note the server returned, warnings first and marked, because they are the point. */
function renderCalNotes(notes) {
  const ul = $('calNotes');
  ul.innerHTML = '';
  for (const n of notes || []) {
    const li = document.createElement('li');
    const warning = /^WARNING/i.test(n);
    li.className = warning ? 'warn' : '';
    li.textContent = warning ? n.replace(/^WARNING:?\s*/i, '') : n;
    ul.appendChild(li);
  }
}

function renderMasters() {
  const rows = $('masterRows');
  const held = CAL_KINDS.filter((k) => state.masters[k]);
  $('masterTable').hidden = held.length === 0;
  rows.innerHTML = '';

  for (const kind of held) {
    const m = state.masters[kind];
    const tr = document.createElement('tr');
    tr.innerHTML =
      `<td class="mkind">${kind}</td>` +
      `<td>${m.imported ? 'your file' : `${m.framesAveraged}× simulated`}</td>` +
      `<td class="mono">${m.exposureSeconds ? fmt.num(m.exposureSeconds, 1) + ' s' : '—'}</td>` +
      `<td class="mono">${fmt.num(m.meanAdu, 1)}</td>` +
      `<td class="mono">${fmt.num(m.rmsAdu, 2)}</td>` +
      `<td><label><input type="checkbox" data-use="${kind}" ${m.use === false ? '' : 'checked'}></label></td>` +
      `<td><a href="${m.fitsUrl}" download>FITS</a></td>`;
    rows.appendChild(tr);
  }

  for (const box of rows.querySelectorAll('input[data-use]')) {
    box.onchange = () => { state.masters[box.dataset.use].use = box.checked; };
  }
}

for (const btn of document.querySelectorAll('#calibrationPanel .calbtn')) {
  btn.onclick = async () => {
    if (!state.capture) return;
    const mine = modeReceipt();
    const kind = btn.dataset.cal;
    $('calError').hidden = true;
    calBusy(true);
    const was = btn.textContent;
    btn.textContent = 'Exposing…';
    try {
      const r = await fetch(`/api/captures/${state.capture}/calibration`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          kind,
          count: Math.max(1, Math.min(256, $('calCount').valueAsNumber || 16)),
        }),
      });
      const data = await r.json();
      if (!ofThisMode(mine)) return;
      if (!r.ok) { calFail(data.error || 'Could not build that frame.'); return; }
      state.masters[kind] = data;
      renderMasters();
      renderCalNotes(data.notes);
    } catch (e) {
      calFail(String(e));
    } finally {
      btn.textContent = was;
      calBusy(false);
    }
  };
}

/* The upload. The body is the file itself; the name rides in a header purely so the server can
   quote it back in an error message, since the person who has to act on "this is 4144x2822 and
   the frame is 2072x1411" is the one holding the file. */
$('upFile').onchange = async () => {
  const file = $('upFile').files[0];
  if (!file || !state.capture) return;
  const kind = $('upKind').value;
  $('calError').hidden = true;
  calBusy(true);
  $('upHint').textContent = `reading ${file.name}…`;
  try {
    const r = await fetch(`/api/captures/${state.capture}/masters?kind=${encodeURIComponent(kind)}`, {
      method: 'POST',
      headers: { 'X-File-Name': file.name, 'content-type': 'application/octet-stream' },
      body: file,
    });
    const data = await r.json();
    if (!r.ok) {
      calFail(data.error || 'That file was refused.');
      $('upHint').textContent = 'refused — nothing was loaded';
      return;
    }
    state.masters[kind] = data;
    renderMasters();
    renderCalNotes(data.notes);
    $('upHint').textContent =
      `${file.name}: ${data.width}×${data.height}, BITPIX ${data.bitPix}` +
      (data.headerInstrument ? `, ${data.headerInstrument}` : '');
  } catch (e) {
    calFail(String(e));
  } finally {
    $('upFile').value = '';
    calBusy(false);
  }
};

/* Reduce, which is the only thing on this page that runs the model BACKWARDS: it recovers each
   injected star's magnitude out of the pixels and reports how far off it came back. */
$('reduce').onclick = async () => {
  if (!state.capture) return;
  const mine = modeReceipt();
  $('calError').hidden = true;
  calBusy(true);
  $('reduceOut').textContent = 'reducing…';
  try {
    const qs = new URLSearchParams();
    for (const kind of CAL_KINDS) {
      const m = state.masters[kind];
      if (m && m.use !== false) qs.set(kind.toLowerCase(), m.id);
    }
    const r = await fetch(`/api/captures/${state.capture}/photometry?${qs}`);
    const d = await r.json();
    if (!ofThisMode(mine)) return;
    if (!r.ok) { calFail(d.error || 'Reduction failed.'); $('reduceOut').textContent = ''; return; }

    const num = (v, dp) => (v === null || v === undefined ? null : fmt.num(v, dp));
    const bits = [
      // Whether to believe any of the rest. A frame can be unreducible and still return numbers.
      d.reliable === false ? '⚠ UNRELIABLE' : null,
      `${fmt.int(d.detection.sourcesFound)} sources, ${fmt.int(d.detection.matched)} matched to injected stars`,
      num(d.residuals.medianAbsMag, 4) ? `median |residual| ${num(d.residuals.medianAbsMag, 4)} mag` : null,
      num(d.residuals.brightRmsMag, 4)
        ? `bright RMS ${num(d.residuals.brightRmsMag, 4)} mag over ${fmt.int(d.residuals.brightCount)} stars` : null,
      // The two zero points come by completely different routes, one through the pixels and one
      // through the passband integral, so their difference is evidence rather than a formality.
      num(d.zeroPoint.residualColourMatched, 4)
        ? `zero point ${num(d.zeroPoint.residualColourMatched, 4)} mag from the analytic one` : null,
      num(d.fluxRecovery.magnitudes, 4) ? `flux recovery ${num(d.fluxRecovery.magnitudes, 4)} mag` : null,
    ].filter(Boolean);
    $('reduceOut').textContent = bits.join(' · ');
    renderCalNotes(d.notes);
  } catch (e) {
    calFail(String(e));
    $('reduceOut').textContent = '';
  } finally {
    calBusy(false);
  }
};

boot();


// ============================ REAL EXOPLANET RESEARCH ============================
//
// The only part of Studio that consumes data it did not generate. A thin front end over
// /api/research: the science is in Engine/Research, and the detector it reaches is
// Core/TransitDetector, unchanged and blind.

const rsFmt = (v, d = 2) => (v === null || v === undefined || Number.isNaN(v) ? '—' : Number(v).toFixed(d));

let rsLast = null;

function rsRow(label, value, note) {
  return `<div class="rsRow"><span class="rsLabel">${label}</span>` +
         `<span class="rsValue">${value}</span>` +
         (note ? `<span class="rsNote">${note}</span>` : '') + '</div>';
}

/**
 * Scatter of the light curve, through the page's own setupCanvas so it matches every other chart
 * here: logical height in data-h, backing store scaled by the device pixel ratio, and all drawing
 * afterwards in CSS pixels. Sizing the canvas by hand instead drew everything at twice the
 * intended scale on a retina display, which is the bug setupCanvas exists to prevent.
 */
function rsDrawCurve(cv, points, opts = {}) {
  // WATCHED, NOT TIMED. setupCanvas sizes the backing store from clientWidth, and a canvas in a
  // panel that was hidden a moment ago has not been laid out yet: measured here, one reported 262
  // device pixels of backing against 833 CSS pixels of display and drew 3.2 times too large, and
  // a second reported 300 against 131. Deferring by a frame fixed the first and not the second,
  // because the number of frames layout takes is not something to guess at. A ResizeObserver
  // fires when the box actually has its size, and again whenever it changes, which also covers
  // the window being resized.
  cv._rsDraw = () => rsPaintCurve(cv, points, opts);

  // The observer catches later resizes; it does NOT reliably catch a panel going from hidden to
  // visible, which is the case that matters most here. So the first paint retries itself while
  // the box still has no width, bounded so a canvas that is legitimately never shown does not
  // spin forever.
  if (!cv._rsObserver) {
    cv._rsObserver = new ResizeObserver(() => { if (cv._rsDraw) cv._rsDraw(); });
    cv._rsObserver.observe(cv);
  }
  // setTimeout RATHER THAN requestAnimationFrame, and that is not a style choice.
  // requestAnimationFrame does not fire at all while a tab is in the background, so a run opened
  // in a tab the reader is not looking at would show every number and an empty chart, and would
  // stay that way until they happened to focus it. A timer is throttled in the background but it
  // still runs, so the chart is drawn and waiting when they arrive.
  let tries = 0;
  const attempt = () => {
    if (cv.clientWidth > 0) { cv._rsDraw(); return; }
    if (++tries < 60) setTimeout(attempt, 50);
  };
  attempt();
}

function rsPaintCurve(cv, points, opts = {}) {
  if (!cv.clientWidth) return;
  const { g, w, h } = setupCanvas(cv);
  if (!points.length) return;

  const pad = { l: 46, r: 12, t: 14, b: 26 };
  let xMin = Infinity, xMax = -Infinity, yMin = Infinity, yMax = -Infinity;
  for (const p of points) {
    if (p.x < xMin) xMin = p.x; if (p.x > xMax) xMax = p.x;
    if (p.y < yMin) yMin = p.y; if (p.y > yMax) yMax = p.y;
  }
  const span = Math.max(yMax - yMin, 1e-6);
  yMin -= span * 0.08; yMax += span * 0.08;

  const sx = (x) => pad.l + (x - xMin) / Math.max(xMax - xMin, 1e-9) * (w - pad.l - pad.r);
  const sy = (y) => h - pad.b - (y - yMin) / Math.max(yMax - yMin, 1e-9) * (h - pad.t - pad.b);

  const css = getComputedStyle(document.body);
  const dim = (css.getPropertyValue('--dim') || 'rgba(255,255,255,0.45)').trim();
  const accent = (css.getPropertyValue('--accent') || '#e0a44c').trim();

  g.strokeStyle = 'rgba(255,255,255,0.12)';
  g.lineWidth = 1;
  g.beginPath(); g.moveTo(pad.l, h - pad.b); g.lineTo(w - pad.r, h - pad.b); g.stroke();
  g.beginPath(); g.moveTo(pad.l, pad.t); g.lineTo(pad.l, h - pad.b); g.stroke();

  // The unbroken flux level, so a dip is read against something rather than in the abstract.
  g.strokeStyle = 'rgba(255,255,255,0.18)';
  g.setLineDash([4, 4]);
  g.beginPath(); g.moveTo(pad.l, sy(1)); g.lineTo(w - pad.r, sy(1)); g.stroke();
  g.setLineDash([]);

  g.fillStyle = accent;
  for (const p of points) g.fillRect(sx(p.x) - 0.6, sy(p.y) - 0.6, 1.2, 1.2);

  g.fillStyle = dim;
  g.font = '11px system-ui, sans-serif';
  g.fillText(opts.xLabel || '', pad.l, h - 8);
  g.textAlign = 'right';
  g.fillText(((yMax - yMin) * 1e6).toFixed(0) + ' ppm', pad.l - 6, pad.t + 8);
  g.fillText('1.000', pad.l - 6, sy(1) + 4);
  g.textAlign = 'left';
}


/**
 * Opens a recorded run in the inspection panels.
 *
 * The stored record and the live response are NOT the same shape: the record is serialised from
 * the C# objects with their field names, the response is the camel cased view the page reads. This
 * translates one into the other in ONE place, because there are two lists that open runs and
 * having each do its own translation is how one of them ends up showing a blank chart.
 */
async function rsOpenRun(id) {
  const d = await (await fetch(`/api/research/runs/${id}`)).json();
  const num = (v) => (typeof v === 'number' ? v : NaN);
  renderResearch({
    ok: true,
    detected: !!(d.result && d.result.detected),
    id: d.id,
    log: d.log || [],
    lightCurve: d.lightCurve || {},
    series: d.series || null,
    curveTrimmed: !!d.curveTrimmed,
    singleTransits: (d.singleTransits || []).map((x) => ({
      centreTimeDays: x.CentreTimeDays, durationHours: x.DurationHours,
      depthPpm: x.DepthPpm, snr: x.Snr, pointsInDip: x.PointsInDip,
      centroidShiftPixels: num(x.CentroidShiftPixels), concerns: x.Concerns || [],
      passed: !(x.Concerns || []).length,
    })),
    candidate: d.result && d.result.detected ? {
      periodDays: d.result.BestPeriodDays, depthPpm: d.result.BestDepthPpm,
      depthUncertaintyPpm: 0, durationHours: d.result.BestDurationHours,
      phase: d.result.BestPhase01, snr: d.result.Snr,
      inTransitPoints: d.result.InTransitPointCount,
      radiusRatio: Math.sqrt(Math.max(0, d.result.BestDepthPpm) / 1e6),
    } : null,
    vetting: d.vetting ? {
      oddDepthPpm: d.vetting.OddDepthPpm, evenDepthPpm: d.vetting.EvenDepthPpm,
      oddEvenSigma: d.vetting.OddEvenDifferenceSigma,
      secondaryDepthPpm: d.vetting.SecondaryDepthPpm,
      secondarySigma: d.vetting.SecondarySignificanceSigma,
      secondaryRatio: d.vetting.SecondaryToPrimaryRatio,
      durationRatio: d.vetting.DurationRatio,
      concerns: d.vetting.Concerns || [], passed: !(d.vetting.Concerns || []).length,
    } : { concerns: [], passed: true },
    known: {
      anything: !!(d.known || []).length,
      matches: (d.known || []).map((m) => ({
        register: m.Register, name: m.Name, periodDays: m.PeriodDays,
        separationArcsec: m.SeparationArcsec, periodRatio: m.PeriodRatio, note: m.Note,
      })),
      unavailable: [],
    },
    caveat: 'Reopened from the recorded run. A candidate, not a planet.',
  });
  $('rsCurvePanel').scrollIntoView({ behavior: 'smooth', block: 'start' });
}

// THE RECORDED RUNS, AND NOT KEEPING ALL OF THEM. One record per star searched means a single
// field sweep adds hundreds, at a few hundred kilobytes each, and the list stops being something
// anyone reads. The listing is held here so the filter chips can re-render without asking the
// server again, and so the counts in the header are the same numbers the buttons act on.
let rsRunList = [];
let rsRunFilter = 'all';
// Whether the person has folded this open or shut themselves. Until they do, the first state is
// chosen from how long the list is; after they do, it is theirs and re-rendering leaves it alone.
let rsRunsFoldTouched = false;

const RS_RUN_FILTERS = {
  all: () => true,
  candidates: (r) => r.detected,
  events: (r) => r.events > 0,
  reviewed: (r) => !!r.verdict,
};

function rsBytes(n) {
  if (!(n > 0)) return '0 B';
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${Math.round(n / 1024)} kB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

const rsEsc = (t) => String(t).replace(/[&<>"]/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

async function loadResearchRuns() {
  const box = $('rsRuns');
  if (!box) return;
  try {
    rsRunList = await (await fetch('/api/research/runs')).json();
  } catch {
    rsRunList = [];
    $('rsRunsCount').textContent = '';
    box.innerHTML = '<p class="hint dim">Could not read the run list.</p>';
    return;
  }
  if (!rsRunsFoldTouched) {
    // A handful of runs is a useful thing to see on arrival. Three hundred is a wall, and the
    // panel sits between the reader and the light curve above it.
    $('rsRunsPanel').open = rsRunList.length <= 20;
  }
  rsRenderRuns();
}

function rsRenderRuns() {
  const box = $('rsRuns');
  if (!box) return;

  const runs = rsRunList;
  const candidates = runs.filter((r) => r.detected).length;
  const reviewed = runs.filter((r) => r.verdict).length;
  const bytes = runs.reduce((t, r) => t + (r.bytes || 0), 0);
  const empty = runs.filter((r) => !r.detected && !r.events && !r.verdict).length;

  // WHAT IS STORED, SAID IN THE HEADER. The question that leads anyone to these controls is
  // whether keeping all this is worth it, and that is not answerable from a list of names.
  $('rsRunsCount').textContent = runs.length
    ? `${runs.length} run${runs.length > 1 ? 's' : ''} · ${rsBytes(bytes)} · ` +
      `${candidates} candidate${candidates === 1 ? '' : 's'}` +
      (reviewed ? ` · ${reviewed} reviewed` : '')
    : 'nothing recorded yet';

  // Trim counts the empty runs STILL CARRYING A CURVE, not the empty ones: once they are trimmed
  // there is nothing left for the button to do, and leaving it lit to answer "nothing to trim" is
  // a control that lies about having work.
  const trimmable = runs.filter((r) => !r.detected && !r.events && !r.verdict && r.curve).length;
  $('rsTrim').disabled = !trimmable;
  $('rsTrim').textContent = trimmable ? `Trim ${trimmable} curve${trimmable > 1 ? 's' : ''}`
                                      : 'Trim curves';
  $('rsClearNull').disabled = !empty;
  $('rsClearNull').textContent = empty ? `Clear ${empty} empty run${empty > 1 ? 's' : ''}`
                                       : 'Clear empty runs';
  $('rsClearAll').disabled = !runs.length;

  const keep = RS_RUN_FILTERS[rsRunFilter] || RS_RUN_FILTERS.all;
  const shown = runs.filter(keep);

  if (!runs.length) {
    box.innerHTML = '<p class="hint dim">No runs yet.</p>';
    return;
  }
  if (!shown.length) {
    box.innerHTML = '<p class="hint dim">No run matches that filter.</p>';
    return;
  }

  box.innerHTML = shown.map((r) => {
    const what = r.detected ? 'candidate'
      : r.events ? `${r.events} single event${r.events > 1 ? 's' : ''}`
      : 'nothing above threshold';
    return `<div class="rsRun ${r.detected ? 'hit' : 'null'}">` +
      `<a href="#" data-run="${rsEsc(r.id)}">` +
        `<b>${rsEsc(r.label || '(unnamed)')}</b><span>${what}</span>` +
        (r.verdict ? `<span class="rsVerdictTag">${rsEsc(r.verdict)}</span>` : '') +
        (r.curve ? '' : `<span class="rsTrimmed">${r.trimmed ? 'curve trimmed' : 'no curve stored'}</span>`) +
      '</a>' +
      `<time>${rsEsc((r.recordedUtc || '').replace('T', ' ').slice(0, 16))}</time>` +
      `<button class="rsDrop" data-drop="${rsEsc(r.id)}" title="Remove this run" ` +
        'aria-label="Remove this run">&times;</button>' +
      '</div>';
  }).join('');

  // Opens it in the panels. It used to link straight at the stored JSON, which showed a page of
  // raw record instead of the light curve the whole tab exists to look at.
  for (const a of box.querySelectorAll('a[data-run]')) {
    a.onclick = (e) => { e.preventDefault(); rsOpenRun(a.dataset.run); };
  }
  for (const b of box.querySelectorAll('button[data-drop]')) {
    b.onclick = async () => {
      const run = rsRunList.find((r) => r.id === b.dataset.drop);
      if (!confirm(`Remove the run on ${run && run.label ? run.label : b.dataset.drop}?\n\n` +
                   'The record goes with it, including the row it contributes to the exported ' +
                   'dataset. This cannot be undone.')) return;
      await fetch(`/api/research/runs/${encodeURIComponent(b.dataset.drop)}`, { method: 'DELETE' });
      loadResearchRuns();
    };
  }
}

function rsRunsSay(text) {
  const out = $('rsRunsOut');
  out.hidden = false;
  out.textContent = text;
}

window.addEventListener('DOMContentLoaded', () => {
  if (!$('rsRuns')) return;

  $('rsRunsPanel').addEventListener('toggle', () => { rsRunsFoldTouched = true; });

  for (const c of document.querySelectorAll('#rsRunsFilter .chip')) {
    c.onclick = () => {
      for (const o of document.querySelectorAll('#rsRunsFilter .chip')) o.classList.remove('on');
      c.classList.add('on');
      rsRunFilter = c.dataset.filter;
      rsRenderRuns();
    };
  }

  // TRIM IS THE ONE THAT SHOULD BE REACHED FOR. It answers the actual complaint - the megabytes -
  // without touching the argument for writing null runs in the first place, so it asks nothing
  // and simply reports what it freed.
  $('rsTrim').onclick = async () => {
    $('rsTrim').disabled = true;
    try {
      const r = await (await fetch('/api/research/runs/trim', { method: 'POST' })).json();
      rsRunsSay(r.trimmed
        ? `Dropped the stored light curve from ${r.trimmed} run${r.trimmed > 1 ? 's' : ''} ` +
          `nothing came of, freeing ${rsBytes(r.freedBytes)}. Every run is still in the dataset.`
        : 'Nothing to trim: no run that found nothing is still carrying a curve.');
    } catch { rsRunsSay('Could not trim the records.'); }
    loadResearchRuns();
  };

  $('rsClearNull').onclick = async () => {
    if (!confirm('Delete every run that found nothing and that nobody has reviewed?\n\n' +
                 'Those rows are how the dataset knows which stars were searched at all, and ' +
                 'they cannot be recovered. Export the CSV first if you want to keep them, or ' +
                 'use Trim curves, which frees most of the same space and keeps the runs.')) return;
    try {
      const r = await (await fetch('/api/research/runs/clear', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ everything: false }),
      })).json();
      rsRunsSay(`Deleted ${r.deleted} empty run${r.deleted === 1 ? '' : 's'}. ${r.kept} kept.`);
    } catch { rsRunsSay('Could not clear the records.'); }
    loadResearchRuns();
  };

  // Twice, because it takes the reviewed runs and the candidates with it, and a single misplaced
  // click on the same row as a filter chip should not be able to empty the directory.
  $('rsClearAll').onclick = async () => {
    if (!confirm('Delete ALL recorded runs, including candidates and anything reviewed?')) return;
    if (!confirm('Last check. Every record in the research directory will be deleted, and the ' +
                 'exported dataset with it. There is no undo.')) return;
    try {
      const r = await (await fetch('/api/research/runs/clear', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ everything: true }),
      })).json();
      rsRunsSay(`Deleted ${r.deleted} run${r.deleted === 1 ? '' : 's'}.`);
    } catch { rsRunsSay('Could not clear the records.'); }
    loadResearchRuns();
  };
});

function renderResearch(d) {
  $('rsError').hidden = true;
  if (!d.ok) {
    $('rsError').hidden = false;
    $('rsError').textContent = d.message;
    return;
  }

  const lc = d.lightCurve;
  $('rsCurvePanel').hidden = false;
  $('rsCurveMeta').textContent =
    `${lc.target || ''} · sector ${lc.sector} · ${fmt.int(lc.cadences)} cadences · ` +
    `${rsFmt(lc.baselineDays, 1)} d · ${rsFmt(lc.cadenceMinutes, 1)} min`;
  $('rsCurveNote').textContent =
    `scatter ${rsFmt(lc.scatterPpmRaw, 0)} ppm before detrending, ${rsFmt(lc.scatterPpmDetrended, 0)} after. ` +
    d.log[0];
  if (d.series && d.series.length) {
    const t0 = d.series[0][0];
    rsDrawCurve($('rsCurve'), d.series.map((p) => ({ x: p[0] - t0, y: p[1] })),
                { xLabel: 'days from the first cadence' });
  } else {
    // A trimmed record: everything measured from the curve is still here, the curve itself is not.
    // Drawn as nothing and SAID, rather than left showing whichever run was open before it.
    rsDrawCurve($('rsCurve'), []);
    if (d.curveTrimmed) {
      $('rsCurveNote').textContent += ' — the stored light curve was trimmed off this run.';
    }
  }

  if (!d.detected) {
    $('rsFoldPanel').hidden = true;
    $('rsResultPanel').hidden = false;
    $('rsResult').innerHTML =
      `<p class="hint"><b>${(d.singleTransits || []).length ? 'No repeating transit.' : 'Nothing above threshold.'}</b> ${d.message}</p>` +
      `<p class="hint dim">Saved as <code>${d.id}</code>.</p>`;
    rsLast = d;
    rsShowInspection(d);
    return;
  }

  const c = d.candidate, v = d.vetting;

  if (d.series) {
    // Folded on the recovered period: the check anyone can make with their own eyes.
    const tf = d.series[0][0];
    const folded = d.series.map((p) => {
      let ph = ((p[0] - tf) / c.periodDays) % 1;
      if (ph < 0) ph += 1;
      if (ph > 0.5) ph -= 1;
      return { x: ph, y: p[1] };
    });
    $('rsFoldPanel').hidden = false;
    $('rsFoldMeta').textContent = `period ${rsFmt(c.periodDays, 5)} d · depth ${rsFmt(c.depthPpm, 0)} ppm`;
    rsDrawCurve($('rsFold'), folded, { xLabel: 'phase, transit near 0' });
  }

  $('rsResultPanel').hidden = false;
  let html = '<div class="rsBlock">' +
    rsRow('Period', rsFmt(c.periodDays, 5) + ' d') +
    rsRow('Depth', rsFmt(c.depthPpm, 0) + ' ppm', '± ' + rsFmt(c.depthUncertaintyPpm, 0)) +
    rsRow('Duration', rsFmt(c.durationHours, 2) + ' h') +
    rsRow('Signal to noise', rsFmt(c.snr, 1)) +
    rsRow('Radius ratio', rsFmt(c.radiusRatio, 4), 'Rp/Rs, from the depth alone') +
    '</div>';

  html += `<div class="rsBlock"><h3>Vetting <span class="${v.passed ? 'rsPass' : 'rsWarn'}">` +
    `${v.passed ? 'nothing disqualifying' : v.concerns.length + ' concern(s)'}</span></h3>` +
    rsRow('Odd vs even depth', rsFmt(v.oddDepthPpm, 0) + ' / ' + rsFmt(v.evenDepthPpm, 0) + ' ppm',
          rsFmt(v.oddEvenSigma, 1) + ' sigma apart') +
    rsRow('Secondary eclipse', rsFmt(v.secondaryDepthPpm, 0) + ' ppm',
          rsFmt(v.secondarySigma, 1) + ' sigma, ' + rsFmt(v.secondaryRatio * 100, 1) + '% of transit') +
    rsRow('Duration vs period', rsFmt(v.durationRatio, 2) + '×', 'against a solar density star') +
    (v.concerns.length ? '<ul class="rsConcerns">' + v.concerns.map((x) => `<li>${x}</li>`).join('') + '</ul>' : '') +
    '</div>';

  const k = d.known;
  html += '<div class="rsBlock"><h3>Already known?</h3>' +
    (k.matches.length
      ? '<ul class="rsKnown">' + k.matches.map((m) =>
          `<li><b>${m.name}</b> in ${m.register}, ${rsFmt(m.separationArcsec, 1)} arcsec away` +
          (m.periodDays ? `, period ${rsFmt(m.periodDays, 5)} d (ratio ${rsFmt(m.periodRatio, 3)})` : '') +
          `<br><span class="rsNote">${m.note}</span></li>`).join('') + '</ul>'
      : '<p class="rsPass">Nothing registered within 30 arcsec of this position.</p>') +
    (k.unavailable.length ? '<p class="rsNote">Could not check: ' + k.unavailable.join('; ') + '</p>' : '') +
    '</div>';

  html += `<div class="rsCaveat">${d.caveat}</div>`;
  html += `<p class="hint dim">Saved as <code>${d.id}</code>.</p>`;
  $('rsResult').innerHTML = html;
  rsLast = d;
  rsShowInspection(d);
}

window.addEventListener('DOMContentLoaded', () => {
  const run = $('rsRun');
  if (!run) return;

  for (const chip of document.querySelectorAll('#rsPresets .chip')) {
    chip.onclick = () => {
      if (chip.dataset.preset === 'known') {
        $('rsLabel').value = 'WASP-18';
        $('rsRa').value = '24.354'; $('rsDec').value = '-45.678';
        $('rsMinP').value = '0.5'; $('rsMaxP').value = '10';
        $('rsTargetHint').textContent =
          'A hot Jupiter found in 2009. Useful for checking the pipeline works, not for discovery.';
      } else {
        $('rsMinP').value = '8'; $('rsMaxP').value = '25'; $('rsWindow').value = '1.5';
        $('rsTargetHint').textContent =
          'Long periods are where the mission pipeline is weakest: it wants three transits and a ' +
          'sector is 27 days, so anything beyond about 9 days is under searched.';
      }
    };
  }

  run.onclick = async () => {
    const mine = modeReceipt();
    const ra = parseFloat($('rsRa').value), dec = parseFloat($('rsDec').value);
    if (!Number.isFinite(ra) || !Number.isFinite(dec)) {
      $('rsError').hidden = false;
      $('rsError').textContent = 'A right ascension and declination in degrees are needed.';
      return;
    }
    run.disabled = true;
    const was = run.textContent;
    run.textContent = 'Fetching and searching…';
    $('rsError').hidden = true;
    try {
      const body = {
        raDeg: ra, decDeg: dec, label: $('rsLabel').value || null,
        minPeriodDays: parseFloat($('rsMinP').value) || 0.5,
        maxPeriodDays: parseFloat($('rsMaxP').value) || 12,
        detrendWindowDays: parseFloat($('rsWindow').value) || 0.75,
        snrThreshold: parseFloat($('rsSnr').value) || 8,
      };
      const d = await (await fetch('/api/research/search', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
      })).json();
      if (!ofThisMode(mine)) return;
      renderResearch(d);
      loadResearchRuns();
    } catch (e) {
      if (!ofThisMode(mine)) return;
      $('rsError').hidden = false;
      $('rsError').textContent = 'The search failed: ' + e;
    } finally {
      run.disabled = false;
      run.textContent = was;
    }
  };
});


// ------------------------------------------------------- visual inspection

let rsEvents = [];      // what was found, marked on the curve
let rsPicked = -1;      // which one the person is looking at
let rsRunId = null;
let rsVerdict = null;

/** The curve around one event, wide enough to see whether the baseline either side is flat. */
function rsDrawZoom(ev) {
  const cv = $('rsZoom');
  if (!rsLast || !rsLast.series || !ev) return;
  const half = ev.durationHours / 24 * 4;      // four durations either side
  const pts = rsLast.series
    .filter((p) => Math.abs(p[0] - ev.centreTimeDays) <= half)
    .map((p) => ({ x: (p[0] - ev.centreTimeDays) * 24, y: p[1] }));
  rsDrawCurve(cv, pts, { xLabel: 'hours from the centre of the dip' });

  $('rsZoomNote').textContent =
    `${ev.depthPpm.toFixed(0)} ppm deep over ${ev.durationHours.toFixed(1)} h, signal to noise ` +
    `${ev.snr.toFixed(1)}, ${ev.pointsInDip} cadences inside it` +
    (Number.isFinite(ev.centroidShiftPixels)
      ? `. The centre of light moved ${ev.centroidShiftPixels.toFixed(4)} px during the dip` +
        (ev.centroidShiftPixels < 0.01 ? ', which is consistent with the light coming from this star.' : '.')
      : '. This light curve carries no centroid, so a blended neighbour is not excluded.') +
    (ev.concerns && ev.concerns.length ? ' Concerns: ' + ev.concerns.join(' ') : '');
}

function rsShowInspection(d) {
  rsEvents = (d.singleTransits || []).slice();
  rsRunId = d.id;
  rsVerdict = null;
  $('rsSaveReview').disabled = true;
  $('rsReviewOut').textContent = '';
  for (const c of document.querySelectorAll('#rsVerdictChips .chip')) c.classList.remove('on');

  // A repeating detection is worth looking at too, as one event at its first transit.
  if (d.detected && d.candidate) {
    const t0 = d.series && d.series.length ? d.series[0][0] : 0;
    rsEvents.unshift({
      centreTimeDays: t0 + d.candidate.phase * d.candidate.periodDays
                      + d.candidate.durationHours / 24 / 2,
      durationHours: d.candidate.durationHours,
      depthPpm: d.candidate.depthPpm,
      snr: d.candidate.snr,
      pointsInDip: d.candidate.inTransitPoints,
      centroidShiftPixels: NaN,
      concerns: d.vetting ? d.vetting.concerns : [],
      repeating: true,
    });
  }

  if (!rsEvents.length) {
    $('rsInspectPanel').hidden = true;
    $('rsSubmitPanel').hidden = true;
    return;
  }

  $('rsInspectPanel').hidden = false;
  $('rsInspectMeta').textContent =
    rsEvents.length === 1 ? 'one event to judge' : `${rsEvents.length} events to judge`;
  $('rsEventChips').innerHTML = rsEvents.map((e, i) =>
    `<button class="chip${i === 0 ? ' on' : ''}" data-ev="${i}">` +
    `${e.repeating ? 'repeating' : 'single'} · ${e.depthPpm.toFixed(0)} ppm · SNR ${e.snr.toFixed(0)}</button>`).join('');
  for (const c of document.querySelectorAll('#rsEventChips .chip')) {
    c.onclick = () => {
      for (const o of document.querySelectorAll('#rsEventChips .chip')) o.classList.remove('on');
      c.classList.add('on');
      rsPicked = Number(c.dataset.ev);
      rsDrawZoom(rsEvents[rsPicked]);
    };
  }
  rsPicked = 0;
  rsDrawZoom(rsEvents[0]);
  rsLoadReadiness();
}

async function rsLoadReadiness() {
  if (!rsRunId) return;
  try {
    const r = await (await fetch(`/api/research/runs/${rsRunId}/readiness`)).json();
    $('rsSubmitPanel').hidden = false;
    const url = `/api/research/runs/${rsRunId}/ctoi?submitter=` +
                encodeURIComponent($('rsReviewer').value || '');
    $('rsReadiness').innerHTML = r.ready
      ? '<p class="rsPass">This run is fit to submit as a Community TOI.</p>' +
        `<p><a class="ghost" href="${url}" download="ctoi.csv">Download the CTOI file</a></p>` +
        '<div class="rsCaveat">The file is <b>not sent anywhere</b>. Read every number in it, then ' +
        'upload it yourself at exofop.ipac.caltech.edu under your own account. A submission needs a ' +
        'person who stands behind it, and that person is you.</div>' +
        (r.warnings.length ? '<ul class="rsConcerns">' + r.warnings.map((w) => `<li>${w}</li>`).join('') + '</ul>' : '')
      : '<p class="rsWarn">Not fit to submit yet:</p><ul class="rsConcerns">' +
        r.blocking.map((b) => `<li>${b}</li>`).join('') + '</ul>';
  } catch { /* the panel simply stays as it was */ }
}

window.addEventListener('DOMContentLoaded', () => {
  if (!$('rsSaveReview')) return;
  for (const c of document.querySelectorAll('#rsVerdictChips .chip')) {
    c.onclick = () => {
      for (const o of document.querySelectorAll('#rsVerdictChips .chip')) o.classList.remove('on');
      c.classList.add('on');
      rsVerdict = c.dataset.verdict;
      $('rsSaveReview').disabled = false;
    };
  }
  $('rsSaveReview').onclick = async () => {
    if (!rsRunId || !rsVerdict) return;
    const r = await fetch(`/api/research/runs/${rsRunId}/review`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        verdict: rsVerdict, note: $('rsReviewNote').value, reviewer: $('rsReviewer').value,
      }),
    });
    $('rsReviewOut').textContent = r.ok
      ? 'Recorded against this run, with your name and the time.'
      : 'Could not record that.';
    rsLoadReadiness();
  };
});


// ------------------------------------------------------------------ look at one star
//
// The cheap question, kept separate from the expensive one. Fetching one sector and drawing it
// takes a couple of seconds; searching a star properly takes a minute and a half, because it joins
// every sector it has and folds them against tens of thousands of trial periods. Anyone judging
// light curves by eye needs to be able to ask the cheap question constantly.

let rsLookData = null;
let rsLookFlux = 'unprocessed';

function rsPaintLook() {
  if (!rsLookData) return;
  const raw = rsLookData[rsLookFlux] || [];
  // The chart wants {x, y}; the API sends pairs. Time is shown from the start of the sector
  // rather than as a barycentric day, because the number that matters when reading a dip by eye
  // is how far into the observation it happened.
  const t0 = raw.length ? raw[0][0] : 0;
  rsDrawCurve($('rsLookCurve'), raw.map((p) => ({ x: p[0] - t0, y: p[1] })),
              { xLabel: `days from BTJD ${t0.toFixed(2)}` });

  $('rsLookNote').textContent = rsLookFlux === 'unprocessed'
    ? 'The raw photometry, flattened here on a five day median. Events lasting a day survive this, ' +
      'and so does the scattered light, so expect it to be less tidy.'
    : "The provider's own detrended flux. Cleaner to read, but its filter was built for short " +
      'transits and removes anything lasting a day or more. A dip visible in the other view and ' +
      'absent here is one this column deleted.';
}

async function rsLook(tic, sector) {
  const meta = $('rsLookMeta');
  meta.textContent = 'fetching…';
  $('rsLookSectors').textContent = '';
  try {
    const q = `/api/research/curve?tic=${encodeURIComponent(tic)}` +
      (sector ? `&sector=${encodeURIComponent(sector)}` : '');
    const d = await (await fetch(q)).json();
    if (!d.ok) { meta.textContent = d.message || 'nothing came back'; return; }

    rsLookData = d;
    meta.textContent = `TIC ${d.tic}, sector ${d.sector}, ${d.provider} · ` +
      `${d.points.toLocaleString()} cadences at ${d.cadenceMinutes.toFixed(1)} min · ` +
      `scatter ${Math.round(d.scatterPpm).toLocaleString()} ppm · ${d.tookSeconds.toFixed(1)} s`;

    // Every other sector this star has, one click each, which is how you tell a transit from a
    // one off artefact: a real repeating signal is in more than one of them.
    $('rsLookSectors').innerHTML = 'sectors: ' + (d.sectors || []).map((s) =>
      `<a href="#" data-sector="${s}" class="${s === d.sector ? 'on' : ''}">${s}</a>`).join(' ');
    for (const a of $('rsLookSectors').querySelectorAll('a[data-sector]')) {
      a.onclick = (e) => { e.preventDefault(); $('rsLookSector').value = a.dataset.sector;
                           rsLook(tic, a.dataset.sector); };
    }
    $('rsLookFlux').hidden = false;
    rsPaintLook();
  } catch (err) {
    meta.textContent = 'could not reach the archive: ' + err.message;
  }
}

window.addEventListener('DOMContentLoaded', () => {
  const go = $('rsLookGo');
  if (!go) return;
  const fire = () => {
    const tic = ($('rsLookTic').value || '').replace(/[^0-9]/g, '');
    if (!tic) { $('rsLookMeta').textContent = 'a TIC number is needed'; return; }
    rsLook(tic, ($('rsLookSector').value || '').trim());
  };
  go.onclick = fire;
  for (const id of ['rsLookTic', 'rsLookSector']) {
    $(id).addEventListener('keydown', (e) => { if (e.key === 'Enter') fire(); });
  }
  for (const chip of $('rsLookFlux').querySelectorAll('.chip')) {
    chip.onclick = () => {
      rsLookFlux = chip.dataset.flux;
      for (const c of $('rsLookFlux').querySelectorAll('.chip')) c.classList.toggle('on', c === chip);
      rsPaintLook();
    };
  }
});

// ------------------------------------------------------------------ field sweep

const RS_FIELDS = {
  'cvz-south': { ra: 90.0, dec: -66.5, note: 'The southern continuous viewing zone. TESS returns ' +
    'here sector after sector, so it holds the longest baselines the mission produces.' },
  'cvz-north': { ra: 270.0, dec: 66.5, note: 'The northern continuous viewing zone, the same idea ' +
    'in the other hemisphere.' },
};

let rsSweepId = null;
let rsSweepTimer = null;

function rsRenderSweep(s) {
  const p = $('rsSweepProgress');
  p.hidden = false;
  if (s.state === 'listing') {
    p.textContent = 'Reading the registers of known planets and candidates, then listing the ' +
      'stars in this field…';
  } else if (s.state === 'empty') {
    p.textContent = 'Nothing in this field has that many sectors. Lower the sector floor, or move.';
  } else if (s.state === 'failed') {
    p.textContent = 'The sweep failed: ' + (s.error || 'unknown');
  } else {
    p.textContent = `${s.done} of ${s.total} stars searched` +
      (s.current ? ` · ${s.current}` : '') +
      (s.state === 'done' ? ' · finished' : '');
  }

  const worth = (s.hits || []).filter((h) => h.score > 0);
  const f = s.filtered || {};
  $('rsSweepPanel').hidden = false;
  $('rsSweepMeta').textContent =
    `${s.field.radius}° around ${s.field.ra.toFixed(2)} ${s.field.dec >= 0 ? '+' : ''}` +
    `${s.field.dec.toFixed(2)}, at least ${s.field.minSectors} sectors · ` +
    `${worth.length} worth opening of ${s.done} searched`;

  // WHAT WAS RULED OUT, AND WHY, rather than a list that silently omits things. A star already
  // carrying a published planet or a mission candidate is dropped before anything is downloaded,
  // so the list below holds only stars with no host on record. Saying so is the difference
  // between a short list and a list that looks suspiciously short.
  const note = $('rsSweepFiltered');
  if (note) {
    const bits = [];
    if (f.listed) bits.push(`${f.listed} stars in the field`);
    if (f.alreadyTaken) bits.push(`${f.alreadyTaken} skipped as already having a planet or candidate on record`);
    if (f.tooFewSectors) bits.push(`${f.tooFewSectors} skipped for too little coverage`);
    if (f.coverageUnknown) bits.push(`${f.coverageUnknown} the archive would not answer about, ` +
      `whose coverage is unknown rather than absent`);
    note.hidden = bits.length === 0;
    note.innerHTML = bits.length
      ? `<p class="hint dim">${bits.join(' · ')}.</p>` +
        ((f.examples || []).length
          ? `<details><summary>what was skipped</summary><ul>` +
            f.examples.map((e) => `<li>${e}</li>`).join('') + `</ul></details>`
          : '') +
        (f.warning ? `<p class="hint warn">${f.warning}</p>` : '')
      : '';
  }

  $('rsSweepList').innerHTML = worth.length
    ? worth.map((h) => `<a class="rsRun hit" href="#" data-run="${h.runId}">` +
        `<b>TIC ${h.target}</b><span>${h.why}</span>` +
        `<time>${h.sectors} sectors</time></a>`).join('')
    : (s.state === 'done'
        ? '<p class="hint dim">Nothing in this field worth opening. That is the usual answer, and ' +
          'every star is recorded, so the field is now searched rather than unknown.</p>'
        : '<p class="hint dim">Nothing yet.</p>');

  for (const a of document.querySelectorAll('#rsSweepList a[data-run]')) {
    a.onclick = (e) => { e.preventDefault(); rsOpenRun(a.dataset.run); };
  }

  if (s.state === 'done' || s.state === 'failed' || s.state === 'empty') {
    clearInterval(rsSweepTimer);
    rsSweepTimer = null;
    $('rsSweep').disabled = false;
    $('rsSweep').textContent = 'Sweep this field';
  }
}

window.addEventListener('DOMContentLoaded', () => {
  const sweep = $('rsSweep');
  if (!sweep) return;

  for (const chip of document.querySelectorAll('#rsFieldPresets .chip')) {
    chip.onclick = () => {
      const f = RS_FIELDS[chip.dataset.field];
      $('rsRa').value = f.ra;
      $('rsDec').value = f.dec;
      $('rsSweepHint').textContent = f.note;
    };
  }

  sweep.onclick = async () => {
    const ra = parseFloat($('rsRa').value), dec = parseFloat($('rsDec').value);
    if (!Number.isFinite(ra) || !Number.isFinite(dec)) {
      $('rsError').hidden = false;
      $('rsError').textContent = 'Pick a field first: a right ascension and declination, or a preset.';
      return;
    }
    sweep.disabled = true;
    sweep.textContent = 'Sweeping…';
    $('rsError').hidden = true;
    const body = {
      raDeg: ra, decDeg: dec,
      radiusDeg: parseFloat($('rsSwRadius').value) || 0.3,
      minSectors: parseInt($('rsSwSectors').value, 10) || 10,
      limit: parseInt($('rsSwLimit').value, 10) || 40,
      minPeriodDays: parseFloat($('rsMinP').value) || 1,
      maxPeriodDays: parseFloat($('rsMaxP').value) || 20,
      detrendWindowDays: parseFloat($('rsWindow').value) || 1,
      snrThreshold: parseFloat($('rsSnr').value) || 8,
    };
    const r = await (await fetch('/api/research/sweep', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    })).json();
    if (!r.id) {
      $('rsError').hidden = false;
      $('rsError').textContent = r.error || 'could not start the sweep';
      sweep.disabled = false; sweep.textContent = 'Sweep this field';
      return;
    }
    rsSweepId = r.id;
    const poll = async () => {
      try { rsRenderSweep(await (await fetch(`/api/research/sweep/${rsSweepId}`)).json()); }
      catch { /* keep polling */ }
    };
    poll();
    rsSweepTimer = setInterval(poll, 3000);
  };
});
