/* ExoInstruments Studio - browser client.
   Talks to the engine only through /api/*. Nothing here knows the backend is C#,
   which is the point: a WebAssembly or Python-backed engine would serve the same
   shapes and this file would not change. */

'use strict';

const $ = (id) => document.getElementById(id);

const state = {
  boot: null,
  mode: 'astro',     // astro | exo | lc | research. The landing mode is astrophotography; see setMode.
  modeSeq: 0,        // bumped on every mode change; the receipt a late await checks. See ofThisMode.
  capture: null,     // the stored frame the calibration panel is working against
  sequence: null,    // the running or finished photometric sequence; see runSequence
  seqStream: null,   // its EventSource, closed by stopSequenceStream and by every mode change
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

  // --- light curve mode --------------------------------------------------------
  lcStars: null,     // the probe frame's matched stars, as photometry returned them
  lcStarSort: { key: 'snr', dir: -1 },
  lcStarFilter: 'usable',
  lcHost: null,      // the star chosen as the transit host: {raDeg, decDeg, colourBv, ...}
  lcReq: null,       // the last /api/pwv/requirement answer
  lcLoss: null,      // the last /api/pwv/loss-curve answer
  lcTransfer: null,  // the last /api/pwv/transit-bias answer
  lcDepth: null,     // the last fitted depth
  lcReqSort: null,   // {key, dir} once a requirement-table header has been clicked; null is the server's order
  // WHICH INSTRUMENT EACH FAMILY OF ANSWERS WAS MEASURED THROUGH, as {key, label}. Changing the
  // instrument used to leave the analytic panels and the star list on the page untouched, so a
  // reader who moved from a RedCat to the RC20 read five panels of RedCat numbers under an RC20
  // heading. These are compared against the selection in refreshLcInstrumentStaleness.
  lcProbedWith: null,       // the star list
  lcPredictedWith: null,    // the band, the loss curve, the requirement and the colour matrix
  lcTransferredWith: null,  // the transfer function
  lcStaleReason: null,      // 'request' after a refused prediction, so an instrument change and its undoing do not erase it
  lcRuns: [],        // finished sequences this session, for the paired comparison
  lcCurves: {},      // uploaded instrument curves, by band name
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
  if (!['astro', 'exo', 'lc', 'research'].includes(mode)) mode = 'astro';
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
  // THE LIGHT-CURVE MODE TAKES ASTROGRAPHS, and only the ground ones: an airmass ladder has no
  // meaning above the atmosphere, and the server refuses a sequence on an orbital instrument for
  // exactly that reason. Offering one here would be offering a control the server rejects.
  const inst = $('instrument');
  const wantsAstrograph = mode === 'astro' || mode === 'lc';
  const offered = mode === 'lc'
    ? state.telescopes.filter((t) => !t.isSpaceBased)
    : state.telescopes;
  inst.innerHTML = wantsAstrograph
    ? offered.map((t) => `<option value="visual:${t.name}">${t.displayName}</option>`).join('')
    : state.boot.instruments.map((i) => `<option value="${i.name}">${i.displayName}</option>`).join('');
  inst.value = wantsAstrograph
    ? `visual:${(offered.find((t) => !t.isSpaceBased) || offered[0]).name}`
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
  for (const id of LC_PANELS) $(id).hidden = true;

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
    $('seqSetup').hidden = true;
    $('waterBlock').hidden = true;
    $('seqPanel').hidden = true;
    for (const id of LC_BLOCKS) $(id).hidden = true;
    loadResearchRuns();
    return;
  }
  // Coming back out of research: the simulator's blocks return, and hidden state is recomputed
  // by onInstrumentChange below rather than remembered here.
  for (const el of document.querySelectorAll('.panel.setup > section.block')) {
    if (!el.id.startsWith('rs') && !el.id.startsWith('lc')
        && !['targetCard', 'spacecraftBlock', 'captureSetup', 'seqSetup', 'waterBlock'].includes(el.id)) {
      el.hidden = false;
    }
  }
  $('siteBlock').hidden = false;

  // THE LIGHT-CURVE MODE'S OWN SETUP. The target search and its card belong to the other two:
  // this mode points at a FIELD and picks its star out of a reduced frame, because a transit has
  // to go into a star that exists and the catalogue cannot say which of them this instrument can
  // actually measure. See the star list.
  const lc = mode === 'lc';
  for (const id of LC_BLOCKS) $(id).hidden = !lc;
  if (lc) {
    $('search').closest('section.block').hidden = true;
    $('targetCard').hidden = true;
    fillLcBands();
    seedLcBandList();
    fillLcSites();
    renderLcChain();
    refreshLcRunPickers();
  }

  onInstrumentChange();
  applyGaiaVisibility();
  drawSkyStatic();
  drawSkyOverlay();
  // NO OPENING TARGET IN THE LIGHT-CURVE MODE. It points at a field and picks its host out of a
  // reduced frame, because the catalogue cannot say which of a field's stars THIS instrument can
  // actually measure in every frame - which is the condition the whole run depends on. Running the
  // opening search here put a planet-host card at the top of a column that has no use for one, and
  // openingTarget() un-hides that card after setMode has hidden it.
  if (!opts.initial && mode !== 'lc') openingTarget();
  if (lc) { $('targetCard').hidden = true; $('search').closest('section.block').hidden = true; }
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
  // The sequence is an astrograph instrument's measurement and means nothing without one. Its
  // results panel goes too: a floor measured on one telescope is not a statement about another.
  if (!isAstrograph) {
    $('seqPanel').hidden = true;
    stopSequenceStream();
    state.sequence = null;
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
  const lc = state.mode === 'lc';
  state.captureMode = !!scope;
  // THE SEQUENCE MOVED. It used to sit under the astrophotography mode, which was the wrong
  // home for it: that mode answers "what does this field look like through this instrument",
  // and a sequence answers "how stable is this ratio over three hours". Different question,
  // different mode. The single capture stays where it was.
  $('captureSetup').hidden = !scope || lc;
  $('seqSetup').hidden = !scope || scope.isSpaceBased || !lc;
  // One water control, shown wherever a request can carry a column: the single capture in
  // astrophotography, the sequence here. There is only one of it, so the two cannot drift.
  $('waterBlock').hidden = !scope || scope.isSpaceBased || state.mode === 'exo' || state.mode === 'research';
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
    // Same rule for the water: in orbit there is no column, so the control goes rather than
    // sitting there set to 10 mm over a frame that will be refused for asking.
    drawPwvCurve();

    // THE SLOT IS THE VALUE, THE LABEL IS THE TEXT. CameraFilter is a fixed ten-name enum built
    // for an amateur wheel, so an observer's own band has to be mounted in whichever slot is free
    // and the slot's name then says something false about it. Every request still sends the slot;
    // only what the reader sees changes. Built with DOM nodes rather than an HTML string because a
    // label is user-supplied text and interpolating it would run whatever it contained.
    {
      const sel = $('capFilter');
      const was = sel.value;
      sel.textContent = '';
      // `bands` is authoritative when the server sends it: an instrument names its own, however
      // many, and every endpoint resolves a request against that list. `filters` is the older
      // ten-name enum vocabulary and is the fallback for anything that predates bands.
      const names = (scope.bands && scope.bands.length)
        ? scope.bands.map((b) => b.name)
        : scope.filters;
      for (const n of names) {
        const opt = document.createElement('option');
        opt.value = n;
        opt.textContent = (scope.filterLabels && scope.filterLabels[n]) || n;
        const b = scope.bands && scope.bands.find((x) => x.name === n);
        if (b && b.centralWavelengthNm) {
          const half = (b.bandwidthAngstrom || 0) / 20;
          opt.title = `${(b.centralWavelengthNm - half).toFixed(0)}-`
                    + `${(b.centralWavelengthNm + half).toFixed(0)} nm`
                    + (b.measuredCurve ? ', measured curve' : ', top-hat');
        }
        sel.appendChild(opt);
      }
      if (was && names.includes(was)) sel.value = was;
    }
    fillLcBands();
    // The light-curve answers already on the page were measured through SOME instrument, and it
    // may not be this one any more. See refreshLcInstrumentStaleness.
    if (lc) refreshLcInstrumentStaleness(scope);
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
  const asked = q || 'type:nebula';
  const qs = new URLSearchParams({ q: asked, site: $('site').value, limit: 60 });
  const r = await fetch(`/api/pointing-search?${qs}`);
  if (!r.ok) return;
  const d = await r.json();
  if (!ofThisMode(mine)) return;
  const ul = $('results');
  const notUnderstood = pointingFiltersNotUnderstood(asked, d.unrecognised);

  ul.innerHTML = d.rows.map((t, i) => `
    <li data-i="${i}">
      <span class="rname">${t.displayName}</span>
      <span class="rmeta">${t.magnitude !== null ? 'V ' + fmt.num(t.magnitude, 1) : (t.typeLabel || '').split(' ')[0]}${
        t.altitudeDeg !== null ? ' · alt ' + Math.round(t.altitudeDeg) + '°' : ''}</span>
    </li>`).join('');

  $('resultCount').textContent =
    `${d.total} of ${fmt.int(d.indexed)} pointable targets` +
    (notUnderstood.length
      ? ` · NOT understood and left out: ${notUnderstood.join(', ')}. The search ran wider than asked.`
      : ' · try type:nebula, in:Ori, mag:<9, alt:>30');

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

/* THE FILTERS THAT MEAN NOTHING, NAMED. Core/TargetQuery.Parse keeps every "key:value" token it
   cannot apply in its Unrecognised list and leaves it out of the query, which quietly widens the
   search: "typ:nebula" listed the whole index as if nothing had been asked, and the count line
   read like a success. /api/pointing-search does not forward that list yet, so this reads it the
   day it does (`served`) and until then judges what it can on its own, by the same rule Core uses:
   a key Core has no case for, or a magnitude or altitude whose value is not a number, or an
   altitude asked to be lower. A VALUE only Core's tables can judge, type:nebulla or in:Orx, still
   passes here unremarked; that is the server's to report, not the page's to guess. */
const POINTING_FILTER_KEYS = ['type', 'kind', 'in', 'constellation', 'con',
                              'mag', 'magnitude', 'v', 'alt', 'altitude'];
function pointingFiltersNotUnderstood(q, served) {
  if (Array.isArray(served)) return served;
  const bad = [];
  for (const raw of String(q || '').split(/[ \t,]+/).filter(Boolean)) {
    const colon = raw.indexOf(':');
    if (colon <= 0 || colon === raw.length - 1) continue;
    const key = raw.slice(0, colon).toLowerCase(), value = raw.slice(colon + 1);
    if (!POINTING_FILTER_KEYS.includes(key)) { bad.push(raw); continue; }
    if (['mag', 'magnitude', 'v', 'alt', 'altitude'].includes(key)) {
      const number = Number(value.replace(/^[<>]=?/, ''));
      if (!Number.isFinite(number) || (key.startsWith('alt') && value[0] === '<')) bad.push(raw);
    }
  }
  return bad;
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

// What the selected filter is CALLED, for anything a human reads. The value stays the slot.
function filterLabelNow() {
  const sel = $('capFilter');
  return sel.selectedOptions.length ? sel.selectedOptions[0].textContent : sel.value;
}

function currentObjectName() {
  if (state.capObject) return state.capObject;
  const ra = parseFloat($('capRa').value), dec = parseFloat($('capDec').value);
  return `field ${ra.toFixed(2)} ${dec >= 0 ? '+' : ''}${dec.toFixed(2)}`;
}

['capRa', 'capDec'].forEach((id) => $(id).addEventListener('change', scheduleForecast));

// ---------------------------------------------------------------- the FITS bundle
// The same request the single Capture sends, repeated N times per filter on the server and
// returned as one ZIP. Built here from the SAME controls, so the bundle cannot quietly differ
// from the frame the reader just looked at.
function bundleFilterList() {
  const mode = $('bunFilters').value;
  if (mode === 'rgb') return ['Red', 'Green', 'Blue'];
  if (mode === 'all') return [...$('capFilter').options].map((o) => o.value);
  return [$('capFilter').value];
}
function bundleCost() {
  const n = parseInt($('bunCount').value, 10) || 0;
  const f = bundleFilterList().length;
  const total = n * f;
  $('bunCost').textContent = total > 64
    ? `${f} filter(s) × ${n} = ${total} frames; the server bundles at most 64 at once.`
    : `${f} filter(s) × ${n} = ${total} frames, roughly ${Math.round(total * 12 / 60)} min at native resolution. Seeds run base + i·7919.`;
}
for (const id of ['bunCount', 'bunFilters', 'capFilter']) $(id).addEventListener('input', bundleCost);
bundleCost();

$('bundle').onclick = async () => {
  const scope = selectedScope();
  if (!scope) return;
  const btn = $('bundle');
  const filters = bundleFilterList();
  const count = parseInt($('bunCount').value, 10) || 1;
  if (filters.length * count > 64) {
    $('bundleError').hidden = false;
    $('bundleError').textContent = `${filters.length * count} frames is over the 64 this build bundles at once.`;
    return;
  }
  btn.disabled = true; btn.textContent = 'Exposing…';
  $('bundleError').hidden = true; $('bunLink').innerHTML = '';
  $('bunStatus').hidden = false;
  $('bunStatus').textContent = `${filters.length * count} frames on the server; the download starts when the last one is written.`;
  try {
    const r = await fetch('/api/captures/bundle', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        count,
        filters,
        capture: {
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
          atUtc: (scope.isSpaceBased ? undefined : state.fcStartIso) || undefined,
          seed: $('capSeed').value.trim() === '' ? undefined : Number($('capSeed').value),
          pwv: pwvRequestBody(),
        },
      }),
    });
    if (!r.ok) {
      let msg = 'The bundle was refused.';
      try { msg = (await r.json()).error || msg; } catch (e) { /* not JSON */ }
      $('bundleError').hidden = false; $('bundleError').textContent = msg;
      $('bunStatus').hidden = true;
      return;
    }
    const blob = await r.blob();
    const disp = r.headers.get('content-disposition') || '';
    const m = /filename\*?=(?:UTF-8'')?"?([^";]+)/i.exec(disp);
    const name = m ? decodeURIComponent(m[1]) : 'frames.zip';
    const url = URL.createObjectURL(blob);
    $('bunLink').innerHTML =
      `<a href="${url}" download="${name}">Download ${name}</a> <span class="dim">${(blob.size / 1e6).toFixed(1)} MB · ${filters.length * count} FITS + manifest.json · stack in Siril, PixInsight, Astro Pixel Processor</span>`;
    $('bunStatus').hidden = true;
    // Also click it, so the reader does not have to find the link after a long wait.
    $('bunLink').querySelector('a').click();
  } catch (e) {
    $('bundleError').hidden = false; $('bundleError').textContent = String(e);
    $('bunStatus').hidden = true;
  } finally {
    btn.disabled = false; btn.textContent = 'Capture and download the bundle';
  }
};

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
        // Empty means "draw one and tell me", which is what the server does and reports back.
        seed: $('capSeed').value.trim() === '' ? undefined : Number($('capSeed').value),
        pwv: pwvRequestBody(),
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
      `${scope.displayName}, ${currentObjectName()}, ${filterLabelNow()}, ${$('capExp').value} s`;
    $('captureNote').textContent =
      `${data.width}×${data.height} px · ${fmt.num(data.fovArcmin[0], 1)}′×${fmt.num(data.fovArcmin[1], 1)}′ · ` +
      `${fmt.num(data.plateScaleArcsec, 2)}″/px`;

    const bits = [
      data.observedUtc ? `${state.fcStartIso ? 'booked' : 'scheduled'} ${data.observedUtc}` : null,
      // The exact instant as a number, because the panel's own stamp is to the minute and a
      // sequence, a re-capture or a cross-check needs the second.
      data.observedUt !== null && data.observedUt !== undefined
        ? `ut ${fmt.num(data.observedUt, 1)} s` : null,
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

      // The seed, because the frame is reproducible from it (it is the header's RANDSEED):
      // posting the same request with this seed repeats the noise draw bit for bit.
      data.seed !== null && data.seed !== undefined ? `seed ${data.seed}` : null,
      // The water this frame was actually taken through, and which series it came from. Absent is
      // not zero, so nothing is printed when the term was not modelled.
      data.pwvMm !== null && data.pwvMm !== undefined
        ? `water ${fmt.num(data.pwvMm, 2)} mm (${data.pwvSeriesId})` : null,
      `${fmt.int(data.computeMs)} ms`,
    ].filter(Boolean);
    $('captureMeta').textContent = bits.join(' · ');

    // The seed that was USED, put where it can be reused: a drawn seed is only reproducible if
    // the observer can see it and send it back.
    if (data.seed !== null && data.seed !== undefined) {
      $('capSeed').value = data.seed;
      $('capSeedOut').textContent = String(data.seed);
    }

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
    const esc = (t) => String(t).replace(/[&<>"]/g, (c) =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const rows = [];
    for (const line of paths) {
      const at = line.indexOf(': ');
      const label = at > 0 ? line.slice(0, at) : line;
      const value = at > 0 ? line.slice(at + 2) : '';
      const leaf = value.includes('/') ? value.slice(value.lastIndexOf('/') + 1) : value;
      rows.push(`<span class="dataItem" title="${esc(value)}"><b>${esc(label)}</b>${
        value ? ' ' + esc(leaf) : ''}</span>`);
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
  drawPwvCurve();
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
  // Booking a slot changes the air column the frame will be exposed through, and the water plot
  // is drawn at that airmass rather than at one of its own choosing.
  drawPwvCurve();
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
  // Clearing the booking hands the epoch back to the server, which will schedule the forecast's
  // best moment - a different airmass, so a different water transmission.
  drawPwvCurve();
};

/* -------------------------------------------------------------------- misc */

$('notesToggle').onclick = () => { $('notes').hidden = !$('notes').hidden; };

let resizeTimer = null;
window.addEventListener('resize', () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => {
    redrawChart();
    drawForecast();
    drawSequence();
    // THE LIGHT-CURVE CANVASES TOO. Every one of them sizes its backing store from clientWidth,
    // so a column that changes width leaves the last drawing stretched across the new one: the
    // axis labels smear and the plot is a scaled picture of an older layout. Hiding a panel is
    // enough to trigger it, because the scrollbar goes with it.
    redrawLcCanvases();
    redrawPwvCurve();
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
      // A star the catalogue has no magnitude for arrives as null: the server writes null for
      // any non-finite number now, where it used to write the string "NaN". Drawn anyway, the
      // arithmetic below reads null as 0 and paints it as a magnitude 0 star, the brightest
      // thing on the chart, in a place the eye then looks for and finds nothing. Skipped.
      if (!Number.isFinite(v)) continue;
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
   costs one request and no recomputation of the frame: the stored ADU are rendered again. */

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
      `${fmt.num(d.maxAdu, 0)} ADU range · ${d.note}`;
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
      `<td class="mono">${m.exposureSeconds ? fmt.num(m.exposureSeconds, 1) + ' s' : 'n/a'}</td>` +
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
          // Empty draws one and reports it back, so a master is reproducible after the fact too.
          seed: $('calSeed').value.trim() === '' ? undefined : Number($('calSeed').value),
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
      $('upHint').textContent = 'refused, nothing was loaded';
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
    renderStarTable(d);
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

const rsFmt = (v, d = 2) => (v === null || v === undefined || Number.isNaN(v) ? 'n/a' : Number(v).toFixed(d));

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
/**
 * A line under a chart giving the cursor position in the data's own units.
 *
 * Attached once per canvas and fed by whatever painted it last, so it follows a chart that is
 * redrawn with different data without accumulating listeners.
 */
function rsAttachReadout(cv) {
  if (cv._rsReadout) return;
  const out = document.createElement('p');
  out.className = 'hint dim rsReadout';
  out.textContent = ' ';
  cv.insertAdjacentElement('afterend', out);
  cv._rsReadout = out;

  cv.addEventListener('mousemove', (e) => {
    const m = cv._rsMap;
    if (!m) return;
    const r = cv.getBoundingClientRect();
    const px = e.clientX - r.left, py = e.clientY - r.top;
    if (px < m.pad.l || px > m.w - m.pad.r || py < m.pad.t || py > m.h - m.pad.b) {
      out.textContent = ' ';
      return;
    }
    const x = m.xMin + (px - m.pad.l) / (m.w - m.pad.l - m.pad.r) * (m.xMax - m.xMin);
    const y = m.yMin + (m.h - m.pad.b - py) / (m.h - m.pad.t - m.pad.b) * (m.yMax - m.yMin);

    // Flux is read twice: as the normalised number the arithmetic uses, and as the loss of light
    // in parts per million, which is the quantity a transit is actually quoted in.
    const yText = m.yKind === 'flux'
      ? `${y.toFixed(6)}  (${((y - 1) * 1e6 >= 0 ? '+' : '')}${((y - 1) * 1e6).toFixed(0)} ppm)`
      : y.toFixed(4);
    out.textContent = `x = ${x.toFixed(4)}${m.xUnit ? ' ' + m.xUnit : ''}   y = ${yText}`;
  });
  cv.addEventListener('mouseleave', () => { out.textContent = ' '; });
}

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

  // WIDE ENOUGH FOR THE LABEL THAT GOES IN IT. The y labels are right aligned against this
  // margin, and at 46 px a value like "+4564 ppm" ran off the left edge and rendered as "64 ppm",
  // which reads as a plausible number and is not one. A curve whose points scatter by thousands of
  // ppm appeared to span sixty four of them.
  const pad = { l: 74, r: 12, t: 14, b: 26 };
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

  // WHERE THE SEARCH SAYS SOMETHING IS. Over a baseline of years a dip of a few hours is a couple
  // of pixels wide, so without a mark the reader is asked to find it unaided in a picture where it
  // is smaller than the sentence describing it.
  for (const m of (opts.marks || [])) {
    if (m < xMin || m > xMax) continue;
    g.strokeStyle = 'rgba(255,255,255,0.35)';
    g.setLineDash([2, 3]);
    g.beginPath(); g.moveTo(sx(m), pad.t); g.lineTo(sx(m), h - pad.b); g.stroke();
    g.setLineDash([]);
  }

  g.fillStyle = accent;
  for (const p of points) g.fillRect(sx(p.x) - 0.6, sy(p.y) - 0.6, 1.2, 1.2);

  g.fillStyle = dim;
  g.font = '11px system-ui, sans-serif';
  g.fillText(opts.xLabel || '', pad.l, h - 8);

  // WHERE THE CURSOR IS, IN THE UNITS OF THE DATA. A chart spanning a thousand days in eight
  // hundred pixels puts a day and a half in every pixel, so pointing at a feature and knowing when
  // it happened is not something the eye can do unaided.
  cv._rsMap = { pad, w, h, xMin, xMax, yMin, yMax, xUnit: opts.xUnit || '', yKind: opts.yKind || 'flux' };
  rsAttachReadout(cv);
  g.textAlign = 'right';
  // THE AXIS SAYS WHAT IT SHOWS. It printed the full plotted range in ppm at the top, directly
  // above a "1.000", which reads as two numbers in different units with no relation stated. A
  // transit is a fractional loss of light, so the labels that help are how far above and below
  // the normal level the plot reaches.
  g.fillText('+' + ((yMax - 1) * 1e6).toFixed(0) + ' ppm', pad.l - 6, pad.t + 8);
  g.fillText('1.000', pad.l - 6, sy(1) + 4);
  g.fillText(((yMin - 1) * 1e6).toFixed(0) + ' ppm', pad.l - 6, h - pad.b - 2);
  // The y axis carries two units at once: a normalised flux, and the deviation from it in parts
  // per million. Saying so once beats leaving the reader to infer it from the labels.
  g.save();
  g.translate(11, (pad.t + h - pad.b) / 2);
  g.rotate(-Math.PI / 2);
  g.textAlign = 'center';
  g.fillText('normalised flux, deviation in ppm', 0, 0);
  g.restore();
  g.textAlign = 'right';
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
  let r, d;
  try {
    r = await fetch(`/api/research/runs/${encodeURIComponent(id)}`);
    d = r.ok ? await r.json() : null;
  } catch (e) {
    renderResearch({ ok: false, message: `Run ${id} could not be opened: ${e}` });
    return;
  }
  if (!r.ok) {
    // A RECORD THAT IS GONE. The runs list and the sweep hits both link at a record by id, and
    // the record can go away underneath them: cleared in another tab, deleted from the results
    // directory by hand. The 404 comes back with no body, .json() threw on it, and the rejection
    // reached nobody: the click did nothing at all and the run merely looked slow. The message
    // now says what happened, and the list is asked again so it stops offering the run.
    renderResearch({
      ok: false,
      message: r.status === 404
        ? `Run ${id} is no longer on disk: its record was removed since this list was drawn. ` +
          'The list has been refreshed.'
        : `Run ${id} could not be opened: the server answered HTTP ${r.status}.`,
    });
    if (r.status === 404) loadResearchRuns();
    return;
  }
  const num = (v) => (typeof v === 'number' ? v : NaN);
  // WHAT THE LOG SAYS ABOUT THE REGISTERS. The record does not keep the list of registers that
  // could not be reached, but the run's log wrote one "could not check ..." line per register at
  // the time, so those lines ARE the record of it.
  const couldNotCheck = (d.log || [])
    .filter((l) => typeof l === 'string' && l.startsWith('could not check '))
    .map((l) => l.slice('could not check '.length));
  renderResearch({
    ok: true,
    detected: !!(d.result && d.result.detected),
    id: d.id,
    log: d.log || [],
    lightCurve: d.lightCurve || {},
    series: d.series || null,
    // Forwarded explicitly, like everything else here. A field left out of this mapping is a field
    // that exists in the record, exists in the live response, and silently disappears the moment a
    // run is reopened: isolatedSeries went missing that way, and with it the only curve that
    // actually contained the dip being reported.
    isolatedSeries: d.isolatedSeries || null,
    curveTrimmed: !!d.curveTrimmed,
    singleTransits: (d.singleTransits || []).map((x) => ({
      centreTimeDays: x.CentreTimeDays, durationHours: x.DurationHours,
      depthPpm: x.DepthPpm, snr: x.Snr, pointsInDip: x.PointsInDip,
      brighteningSnr: num(x.BrighteningSnr), coverageRatio: num(x.CoverageRatio),
      redNoiseFactor: num(x.RedNoiseFactor),
      centroidShiftPixels: num(x.CentroidShiftPixels), concerns: x.Concerns || [],
      passed: !(x.Concerns || []).length,
    })),
    candidate: d.result && d.result.detected ? {
      periodDays: d.result.BestPeriodDays, depthPpm: d.result.BestDepthPpm,
      // NOT RECORDED, SO NOT INVENTED. The record keeps the detector's period, depth, duration,
      // phase, signal to noise and point count, and not its depth uncertainty. This used to write
      // 0 here, and the panel printed "± 0" under the depth of every reopened run: an error bar
      // claiming a precision no measurement has. Null renders as "not recorded", and the value
      // is read the day the record carries it.
      depthUncertaintyPpm: typeof d.result.DepthUncertaintyPpm === 'number'
        ? d.result.DepthUncertaintyPpm : null,
      durationHours: d.result.BestDurationHours,
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
      // Null when the run was saved with no register report at all (an empty result is), which
      // renders as "not recorded" rather than as "nothing registered here".
      matches: Array.isArray(d.known) ? d.known.map((m) => ({
        register: m.Register, name: m.Name, periodDays: m.PeriodDays,
        separationArcsec: m.SeparationArcsec, periodRatio: m.PeriodRatio, note: m.Note,
      })) : null,
      // An empty list here used to stand for "every register answered", which the record never
      // said. The log's own lines when it has them, otherwise null, which renders as not recorded.
      unavailable: couldNotCheck.length ? couldNotCheck : null,
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
    `${lc.target || ''} · ` +
    ((lc.sectors && lc.sectors.length > 1)
      ? `${lc.sectors.length} sectors, ${lc.sectors[0]} to ${lc.sectors[lc.sectors.length - 1]} · `
      : `sector ${(lc.sectors && lc.sectors[0]) || lc.sector} · `) +
    `${fmt.int(lc.cadences)} cadences · ` +
    `${rsFmt(lc.baselineDays, 1)} d · ${rsFmt(lc.cadenceMinutes, 1)} min`;
  $('rsCurveNote').textContent =
    `scatter ${rsFmt(lc.scatterPpmRaw, 0)} ppm before detrending, ${rsFmt(lc.scatterPpmDetrended, 0)} after, ` +
    'per cadence. The plot averages the cadences into about four thousand points, so the scatter ' +
    'you see is smaller than that by roughly the root of the group size, while a real dip keeps ' +
    'its depth. ' + d.log[0];
  // THE CURVE SHOWN MUST BE THE ONE THE RESULT CAME FROM. The fold reads the provider's
  // detrended flux and the isolated search reads the raw flux flattened on a five day median.
  // Showing the first for every result made every star in a field look identical, because that
  // column is flat by design, and it could not contain a dip the second one had found.
  const shown = ((d.singleTransits || []).length && d.isolatedSeries && d.isolatedSeries.length)
    ? d.isolatedSeries : d.series;
  if (shown && shown.length) {
    const t0 = shown[0][0];
    rsDrawCurve($('rsCurve'), shown.map((p) => ({ x: p[0] - t0, y: p[1] })),
                { xLabel: 'days from the first cadence', xUnit: 'd',
                  marks: (d.singleTransits || []).map((e) => e.centreTimeDays - t0) });
    if (shown === d.isolatedSeries) {
      $('rsCurveNote').textContent +=
        ' Shown here is the unprocessed flux, flattened on a five day median, which is where the ' +
        'isolated dip was found. The detrended column the fold uses has events of a day or more ' +
        'removed from it.';
    }
  } else {
    // A trimmed record: everything measured from the curve is still here, the curve itself is not.
    // Drawn as nothing and SAID, rather than left showing whichever run was open before it.
    rsDrawCurve($('rsCurve'), []);
    if (d.curveTrimmed) {
      $('rsCurveNote').textContent += ' The stored light curve was trimmed off this run.';
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
    rsDrawCurve($('rsFold'), folded, { xLabel: 'phase, transit near 0', xUnit: 'in phase' });
  }

  $('rsResultPanel').hidden = false;
  let html = '<div class="rsBlock">' +
    rsRow('Period', rsFmt(c.periodDays, 5) + ' d') +
    rsRow('Depth', rsFmt(c.depthPpm, 0) + ' ppm',
          c.depthUncertaintyPpm === null || c.depthUncertaintyPpm === undefined
            ? 'uncertainty not recorded with this run'
            : '± ' + rsFmt(c.depthUncertaintyPpm, 0)) +
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
  // Null in either field means the record does not say, and the page says so rather than
  // printing the reassuring form of an answer it does not have.
  html += '<div class="rsBlock"><h3>Already known?</h3>' +
    (k.matches === null
      ? '<p class="rsNote">No register report was recorded with this run.</p>'
      : k.matches.length
        ? '<ul class="rsKnown">' + k.matches.map((m) =>
            `<li><b>${m.name}</b> in ${m.register}, ${rsFmt(m.separationArcsec, 1)} arcsec away` +
            (m.periodDays ? `, period ${rsFmt(m.periodDays, 5)} d (ratio ${rsFmt(m.periodRatio, 3)})` : '') +
            `<br><span class="rsNote">${m.note}</span></li>`).join('') + '</ul>'
        : '<p class="rsPass">Nothing registered within 30 arcsec of this position.</p>') +
    (k.unavailable === null
      ? '<p class="rsNote">Whether every register answered is not recorded with this run; its log ' +
        'carries no "could not check" line.</p>'
      : k.unavailable.length ? '<p class="rsNote">Could not check: ' + k.unavailable.join('; ') + '</p>' : '') +
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
        // 25 DAYS WAS A LEFTOVER FROM SEARCHING ONE SECTOR AT A TIME. A sector is 27 days, so a
        // period past about nine of them showed too few transits to fold and there was no point
        // asking for more. Sectors are joined now, a continuous viewing zone star gives a baseline
        // of years, and the cost of raising the ceiling is about a second. What stops it going
        // further is not the search but the coverage: the run log works out the period past which
        // three transits no longer fit inside the days this particular star was recorded.
        $('rsMinP').value = '8'; $('rsMaxP').value = '60'; $('rsWindow').value = '1.5';
        $('rsTargetHint').textContent =
          'Long periods are where the mission pipeline is weakest: it wants three transits, and its ' +
          'own detrending removes anything lasting a day. Past the ceiling the run log reports, the ' +
          'isolated event search is the one doing the work, and it needs no period at all.';
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
  if (!rsLast || !ev) return;
  // The curve the event was found in, not the one the fold used; see the note where the full
  // light curve is drawn. Zooming a reported dip on the detrended column showed a flat stretch
  // that could not contain it, because that column has events of a day or more removed.
  const source = (rsLast.isolatedSeries && rsLast.isolatedSeries.length)
    ? rsLast.isolatedSeries : rsLast.series;
  if (!source) return;
  const half = ev.durationHours / 24 * 4;      // four durations either side
  const pts = source
    .filter((p) => Math.abs(p[0] - ev.centreTimeDays) <= half)
    .map((p) => ({ x: (p[0] - ev.centreTimeDays) * 24, y: p[1] }));
  rsDrawCurve(cv, pts, { xLabel: 'hours from the centre of the dip', xUnit: 'h' });

  $('rsZoomNote').textContent =
    `${ev.depthPpm.toFixed(0)} ppm deep over ${ev.durationHours.toFixed(1)} h, signal to noise ` +
    `${ev.snr.toFixed(1)}, ${ev.pointsInDip} cadences inside it` +
    // THE MARGIN IS THE NUMBER THAT DECIDES, so it belongs beside the depth rather than buried in
    // a concern. Nothing makes a star brighter in the shape of a box, so the best brightening in
    // the same curve is what its noise manages for no reason at all, and how far the dip clears
    // that is the whole question.
    (Number.isFinite(ev.brighteningSnr) && ev.brighteningSnr > 0
      ? `. The strongest brightening of the same duration in this curve reaches ` +
        `${ev.brighteningSnr.toFixed(1)}, so this dip clears it ` +
        `${(ev.snr / ev.brighteningSnr).toFixed(2)} times over`
      : '') +
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
              { xLabel: `days from BTJD ${t0.toFixed(2)}`, xUnit: 'd' });

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
  } else if (s.state === 'interrupted') {
    p.textContent = s.error || 'This sweep was interrupted.';
  } else if (s.state === 'failed') {
    p.textContent = 'The sweep failed: ' + (s.error || 'unknown');
  } else {
    p.textContent = `${s.done} of ${s.total} stars searched` +
      (s.current ? ` · ${s.current}` : '') +
      (s.state === 'done' ? ' · finished' : '');
  }

  // Everything with a score is listed, because the numbers are there to be compared. The count
  // above says how many are worth OPENING, which is a different and much smaller question.
  const worth = (s.hits || []).filter((h) => h.score > 0);
  const f = s.filtered || {};
  $('rsSweepPanel').hidden = false;
  $('rsSweepMeta').textContent =
    `${s.field.radius}° around ${s.field.ra.toFixed(2)} ${s.field.dec >= 0 ? '+' : ''}` +
    `${s.field.dec.toFixed(2)}, at least ${s.field.minSectors} sectors · ` +
    `${s.worthLooking} worth opening of ${s.done} searched, ${worth.length} listed, ranked by ` +
    `how far each dip stands above what the same curve manages upward`;

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
        `<b>TIC ${h.target}${h.clean ? '' : ' <span class="dim">(flagged)</span>'}</b>` +
        `<span>${h.why}</span>` +
        `<time>${h.score.toFixed(1)} · ${h.sectors} sectors</time></a>`).join('')
    : (s.state === 'done'
        ? '<p class="hint dim">Nothing in this field worth opening. That is the usual answer, and ' +
          'every star is recorded, so the field is now searched rather than unknown.</p>'
        : '<p class="hint dim">Nothing yet.</p>');

  for (const a of document.querySelectorAll('#rsSweepList a[data-run]')) {
    a.onclick = (e) => { e.preventDefault(); rsOpenRun(a.dataset.run); };
  }

  if (s.state === 'done' || s.state === 'failed' || s.state === 'empty'
      || s.state === 'interrupted') {
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
    // A FAILED POLL WAS BEING SWALLOWED, and that is how a dead sweep passes for a slow one. If
    // the server holding it restarts, the identifier stops existing and every poll 404s; catching
    // that and carrying on left the last state frozen on screen, so a sweep killed at 34 of 100
    // looked exactly like a sweep still working on the 35th, for as long as anybody watched.
    let missed = 0;
    const poll = async () => {
      try {
        const response = await fetch(`/api/research/sweep/${rsSweepId}`);
        if (response.status === 404) {
          clearInterval(rsSweepTimer); rsSweepTimer = null;
          $('rsSweepProgress').textContent =
            'This sweep is gone: the server restarted while it was running, so what it had ' +
            'searched is saved under Runs recorded but the sweep itself cannot continue. ' +
            'Start another to carry on.';
          $('rsSweep').disabled = false; $('rsSweep').textContent = 'Sweep this field';
          return;
        }
        if (!response.ok) throw new Error(String(response.status));
        missed = 0;
        rsRenderSweep(await response.json());
      } catch (e) {
        // A network hiccup is not a dead sweep; several in a row is worth saying.
        if (++missed >= 5) {
          $('rsSweepProgress').textContent =
            `Cannot reach the server (${missed} attempts). The sweep may still be running; this ` +
            'page will keep trying.';
        }
      }
    };
    poll();
    rsSweepTimer = setInterval(poll, 3000);
  };
});

// ======================================================================================
// PHOTOMETRIC SEQUENCES
//
// A transit is not a frame. It is a RATIO of one star to several others in the same exposure,
// followed across hours, and how stable that ratio is decides whether a planet is detectable.
// That number cannot be read off a single frame, which is why this panel exists and why it is
// the only place in the interface that measures across time rather than within one picture.
//
// The run is long by nature - a hundred sub-exposures is tens of minutes - so it streams. The
// server keeps no frames: it reduces each one and discards it, which is why there is a preview
// image and not a gallery.
// ======================================================================================

function stopSequenceStream() {
  if (state.seqStream) {
    state.seqStream.close();
    state.seqStream = null;
  }
}

function seqCost() {
  const frames = parseInt($('seqFrames').value, 10) || 0;
  const bin = parseInt($('seqBin').value, 10) || 1;
  // Measured on this pipeline: a frame costs about the same whatever its pixel count, because the
  // work is the cone search, the PSF kernel and the emission integral rather than the pixels. The
  // reduction adds roughly as much again at binning 1, less as the frames get smaller.
  const perFrame = bin === 1 ? 22 : bin === 2 ? 16 : 11;
  const seconds = frames * perFrame;
  const mins = Math.round(seconds / 60);
  $('seqCost').textContent =
    `${frames} frames at binning ${bin} is roughly ${mins < 1 ? 'under a minute' : mins + ' minutes'} of `
    + `compute. The run keeps going if you leave this panel; it stops if you press Stop.`;
}

for (const id of ['seqFrames', 'seqBin']) $(id).addEventListener('input', seqCost);
seqCost();

// AN INJECTED TRANSIT, which the endpoint has accepted since it existed and the page never sent.
// A depth of zero injects nothing and is not an error - that is the server's own convention - so
// the unchecked box simply returns null and the run is a plain sequence.
function transientRequestBody() {
  if (!$('seqTransit').checked) return undefined;
  const ppt = parseFloat($('seqTrDepth').value);
  if (!(ppt > 0)) return undefined;
  const ra = $('seqTrRa').value.trim(), dec = $('seqTrDec').value.trim();
  return {
    depth: ppt / 1000,                       // the panel is in parts per thousand, the API in fraction
    durationHours: parseFloat($('seqTrDur').value),
    periodDays: parseFloat($('seqTrPer').value),
    matchRadiusArcsec: parseFloat($('seqTrRad').value),
    // Omitted means the frame centre, which is what the server already defaults to.
    raDeg: ra === '' ? undefined : Number(ra),
    decDeg: dec === '' ? undefined : Number(dec),
  };
}

$('seqTransit').addEventListener('change', () => {
  const on = $('seqTransit').checked;
  $('seqTransitBox').hidden = !on;
  $('seqTransitHint').hidden = !on;
});

$('seqStart').onclick = async () => {
  const mine = modeReceipt();
  const scope = selectedScope();
  if (!scope) return;

  const btn = $('seqStart');
  btn.disabled = true;
  btn.textContent = 'Starting…';
  $('seqError').hidden = true;

  try {
    const seedRaw = $('seqSeed').value.trim();
    const r = await fetch('/api/sequences', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        telescope: scope.name,
        site: $('site').value,
        raDeg: fieldRa(),
        decDeg: fieldDec(),
        filter: fieldBand(),
        objectName: currentObjectName(),
        exposureSeconds: parseFloat($('seqExp').value),
        binning: parseInt($('seqBin').value, 10),
        frames: parseInt($('seqFrames').value, 10),
        airmassFrom: parseFloat($('seqXFrom').value),
        airmassTo: parseFloat($('seqXTo').value),
        comparisons: parseInt($('seqComps').value, 10),
        calibrate: $('seqCal').checked,
        seed: seedRaw === '' ? undefined : Number(seedRaw),
        pwv: pwvRequestBody(),
        transient: transientRequestBody(),
      }),
    });
    const data = await r.json();
    if (!ofThisMode(mine)) return;
    if (!r.ok) {
      $('seqError').hidden = false;
      $('seqError').textContent = data.error || 'The sequence could not be started.';
      return;
    }

    state.sequence = data;
    state.lcDepth = null;
    lcExportLinks(null);
    $('lcDepthPanel').hidden = true;
    $('lcClosurePanel').hidden = true;
    $('seqPanel').hidden = false;
    $('seqStop').hidden = false;
    renderSequence();
    openSequenceStream(data.id, mine);
  } finally {
    btn.disabled = false;
    btn.textContent = 'Run the sequence';
  }
};

$('seqStop').onclick = async () => {
  if (!state.sequence) return;
  await fetch(`/api/sequences/${state.sequence.id}/stop`, { method: 'POST' });
  // The stream reports the state change itself; nothing to do here but wait for it.
};

function openSequenceStream(id, mine) {
  stopSequenceStream();
  const es = new EventSource(`/api/sequences/${id}/stream`);
  state.seqStream = es;

  es.onmessage = async (ev) => {
    if (!ofThisMode(mine)) { stopSequenceStream(); return; }
    const msg = JSON.parse(ev.data);
    if (!state.sequence) return;

    state.sequence.state = msg.state;
    state.sequence.done = msg.done;
    if (msg.frames && msg.frames.length) {
      state.sequence.frames = (state.sequence.frames || []).concat(msg.frames);
    }
    renderSequence();
    if (state.mode === 'lc') renderLcRun();

    if (msg.finished) {
      stopSequenceStream();
      // The analysis is only computed once the run is over, so it comes from a final read
      // rather than from the stream. A run that was stopped or failed has none, and says so.
      const full = await (await fetch(`/api/sequences/${id}`)).json();
      if (!ofThisMode(mine)) return;
      state.sequence = full;
      $('seqStop').hidden = true;
      renderSequence();

      // THE RUN IS OVER, SO THE FIFTH STEP CAN HAPPEN. In the light-curve mode a finished run is
      // not the end of the measurement, it is the input to it: the depth is fitted, the run joins
      // the list two conditions can be subtracted from, and the closure panel can compare what the
      // analytic half predicted with what the frames actually gave back.
      if (state.mode === 'lc') {
        rememberLcRun(full);
        renderLcRun();
        renderLcChain();
        if (full.state === 'finished' && full.transient) fitLcDepth();
      }
    }
  };
  es.onerror = () => { /* the browser retries on its own, as it does for campaigns */ };
}

function renderSequence() {
  const s = state.sequence;
  if (!s) return;

  const frames = s.frames || [];
  const measured = frames.filter((f) => !f.error);
  const refused = frames.length - measured.length;

  $('seqProgress').textContent =
    s.state === 'running' ? `${s.done} of ${s.total}`
    : s.state === 'finished' ? `${measured.length} frames measured`
    : s.state === 'cancelled' ? `stopped at ${s.done} of ${s.total}`
    : 'failed';

  const bits = [
    `${s.objectName || 'the field'} · ${s.telescope} · ${s.filter} · ${fmt.num(s.exposureSeconds, 0)} s · binning ${s.binning}`,
    `airmass ${fmt.num(s.airmassFrom, 2)} to ${fmt.num(s.airmassTo, 2)}`,
    `${s.startUtc} → ${s.endUtc}`,
    s.calibrate ? 'each frame calibrated' : 'no calibration applied',
    s.pwv ? `water: ${s.pwv.description}` : 'no water-vapour term',
    `seed ${s.seed}`,
    refused ? `${refused} frame(s) refused` : null,
    s.stopReason || null,
  ].filter(Boolean);
  $('seqSubtitle').textContent = bits.join(' · ');

  const a = s.analysis;
  $('seqVerdict').hidden = !a || !(a.ratio > 0);
  if (a && a.ratio > 0) {
    $('seqFloor').textContent = `${fmt.num(a.detrendedPpt, 2)} ppt`;
    $('seqPhoton').textContent = `${fmt.num(a.photonPpt, 2)} ppt`;
    $('seqRatio').textContent = fmt.num(a.ratio, 2);
    // The ratio is the whole verdict, so it says what it means rather than leaving the reader
    // to remember which way is good.
    $('seqRatioSub').textContent = a.ratio <= 1.25
      ? 'at the photon limit: nothing unexplained is left'
      : 'above the photon limit: something is not photons';
  }

  // WHAT THIS PANEL STILL OWNS IN THE LIGHT-CURVE MODE. The curve, the refusal reasons and the
  // preview frame all have their own panels there, and showing them here too meant the reader saw
  // each of them twice. What is left is what nothing else draws: the floor against the photon
  // limit, the colour trend, and the numbers behind them.
  const lcOwnsTheRest = state.mode === 'lc';
  $('seqCurve').hidden = lcOwnsTheRest;
  $('seqCurveNote').hidden = lcOwnsTheRest;
  $('seqPreviewWrap').hidden = lcOwnsTheRest || !s.previewUrl;

  renderSequenceNumbers(a);
  renderSequenceNotes(a, measured, refused);
  drawSequence();

  // The comparison needs a finished run: it reads the same frames the analysis did.
  $('bridgeFold').hidden = s.state !== 'finished';

  if (s.previewUrl && !lcOwnsTheRest
      && $('seqPreview').getAttribute('src') !== s.previewUrl) {
    $('seqPreview').src = s.previewUrl;
  }
}

function renderSequenceNumbers(a) {
  const box = $('seqNumbers');
  if (!a) { box.innerHTML = ''; return; }
  const rows = [
    ['frames measured', fmt.int(a.frames)],
    ['stars in every frame', fmt.int(a.sharedStars)],
    ['target', a.target || 'n/a'],
    ['ensemble', a.ensemble || 'n/a'],
    ['airmass', `${fmt.num(a.airmassMin, 3)} to ${fmt.num(a.airmassMax, 3)}`],
    ['RMS of the ratio, raw', `${fmt.num(a.rawPpt, 2)} ppt`],
    ['drift with airmass', `${fmt.num(a.driftPpt, 2)} ppt end to end`],
    ['RMS with the drift removed', `${fmt.num(a.detrendedPpt, 2)} ppt`],
    ['photon-limited prediction', `${fmt.num(a.photonPpt, 2)} ppt`],
    ['raw / photon', fmt.num(a.rawRatio, 3)],
    ['detrended / photon', fmt.num(a.ratio, 3)],
    ['colour × airmass slope',
      a.colourSlope === null ? 'n/a'
        : `${fmt.num(a.colourSlope, 1)} ± ${fmt.num(a.colourSlopeError, 1)} mmag per airmass per mag B−V (${fmt.int(a.slopeStars)} stars)`],
  ];
  box.innerHTML = rows.map(([k, v]) =>
    `<dt>${k}</dt><dd class="mono">${v}</dd>`).join('');
}

function renderSequenceNotes(a, measured, refused) {
  const ul = $('seqNotes');
  const notes = [];
  // THE REASONS BELONG TO ONE PANEL. In the light-curve mode the frame table is on screen with
  // this one and both were printing the same list, so the reader saw every refusal twice. The
  // frame table owns them there, because that is where the frames are.
  const framesPanelOwnsThem = state.mode === 'lc';
  if (refused && !framesPanelOwnsThem) {
    for (const { reason, count } of groupRefusals(state.sequence?.frames)) {
      notes.push({ warn: true, text: `${count} frame(s) refused: ${reason}` });
    }
  }
  if (state.sequence?.ladderNote && !framesPanelOwnsThem) {
    notes.push({ warn: false, text: state.sequence.ladderNote });
  }
  const unreliable = measured.filter((f) => f.reliable === false).length;
  if (unreliable) {
    notes.push({ warn: true, text:
      `${unreliable} of ${measured.length} frames reduced unreliably. A floor measured through them is not `
      + `to be believed; capture one of those epochs on its own to see the reduction's own reasons.` });
  }
  for (const n of (a?.notes || [])) notes.push({ warn: n.startsWith('UNRELIABLE'), text: n });
  ul.innerHTML = notes.map((n) =>
    `<li class="${n.warn ? 'warn' : ''}">${escapeSeq(n.text)}</li>`).join('');
}

const escapeSeq = (t) => String(t).replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

// ---------------------------------------------------------------------- the two charts

function drawSequence() {
  const a = state.sequence?.analysis;
  if (!a || $('seqPanel').hidden) return;
  // The ratio curve has its own panel in the light-curve mode; here it would be the same picture
  // twice, and a hidden canvas has no width to size itself from anyway.
  if (!$('seqCurve').hidden) drawSequenceCurve(a);
  drawSequenceSlopes(a);
}

function drawSequenceCurve(a) {
  const cv = $('seqCurve');
  if (!cv || !cv.clientWidth) return;
  const pts = a.curve || [];
  if (pts.length < 2) return;

  const { g, w, h } = setupCanvas(cv);
  const xs = pts.map((p) => p[0]);
  const ys = pts.map((p) => p[1]);
  const [xlo, xhi] = extent(xs);
  const [ylo, yhi] = extent(ys);
  const { X, Y } = axes(g, w, h, xlo, xhi, ylo, yhi, 'airmass', 'target / ensemble');

  // The fitted drift, drawn over the points, because it is the part that is REAL PHYSICS and is
  // removed before the floor is scored. Showing the scatter without it would look like noise.
  const n = pts.length;
  const mx = xs.reduce((s, v) => s + v, 0) / n;
  const my = ys.reduce((s, v) => s + v, 0) / n;
  let sxx = 0, sxy = 0;
  for (let i = 0; i < n; i++) { sxx += (xs[i] - mx) ** 2; sxy += (xs[i] - mx) * (ys[i] - my); }
  const b = sxx > 0 ? sxy / sxx : 0;
  const a0 = my - b * mx;

  g.strokeStyle = 'rgba(255,180,84,.95)';
  g.lineWidth = 1.5;
  g.beginPath();
  g.moveTo(X(xlo), Y(a0 + b * xlo));
  g.lineTo(X(xhi), Y(a0 + b * xhi));
  g.stroke();

  g.fillStyle = 'rgba(94,207,255,.85)';
  for (const [x, y] of pts) {
    g.beginPath();
    g.arc(X(x), Y(y), 2.2, 0, Math.PI * 2);
    g.fill();
  }

  $('seqCurveNote').textContent =
    `The differential light curve: the target divided by the summed ensemble, normalised. In this ratio `
    + `the zero point, the exposure and the collecting area all cancel, and so does scintillation, which `
    + `is one draw shared by every star in a frame. The amber line is the drift with airmass: real `
    + `second-order extinction, because the target and its comparisons are different colours and the `
    + `atmosphere is not grey. It is removed before the floor is scored.`;
}

function drawSequenceSlopes(a) {
  const cv = $('seqSlopes');
  if (!cv || !cv.clientWidth) return;
  const pts = a.slopes || [];
  if (pts.length < 3) {
    const { g, w, h } = setupCanvas(cv);
    g.fillStyle = '#4d5867';
    g.font = '11px ui-monospace, Menlo, monospace';
    g.fillText('too few stars measured in every frame to fit a colour trend', 16, h / 2);
    $('seqSlopeNote').textContent = '';
    return;
  }

  const { g, w, h } = setupCanvas(cv);
  const xs = pts.map((p) => p[0]);
  const ys = pts.map((p) => p[1]);
  const [xlo, xhi] = extent(xs);
  const [ylo, yhi] = extent(ys);
  const { X, Y } = axes(g, w, h, xlo, xhi, ylo, yhi, 'B−V', 'mmag / airmass');

  // Zero is the line that means "this star does not care about the air", so it is drawn.
  if (ylo < 0 && yhi > 0) {
    g.strokeStyle = '#2a3340';
    g.setLineDash([3, 3]);
    g.beginPath(); g.moveTo(X(xlo), Y(0)); g.lineTo(X(xhi), Y(0)); g.stroke();
    g.setLineDash([]);
  }

  g.strokeStyle = 'rgba(255,180,84,.95)';
  g.lineWidth = 1.5;
  g.beginPath();
  g.moveTo(X(xlo), Y(a.colourSlope * xlo + (ys.reduce((s, v) => s + v, 0) / ys.length
      - a.colourSlope * (xs.reduce((s, v) => s + v, 0) / xs.length))));
  g.lineTo(X(xhi), Y(a.colourSlope * xhi + (ys.reduce((s, v) => s + v, 0) / ys.length
      - a.colourSlope * (xs.reduce((s, v) => s + v, 0) / xs.length))));
  g.stroke();

  g.fillStyle = 'rgba(94,207,255,.85)';
  for (const [x, y] of pts) {
    g.beginPath();
    g.arc(X(x), Y(y), 2.6, 0, Math.PI * 2);
    g.fill();
  }

  $('seqSlopeNote').textContent =
    `Each star's own drift against the same ensemble, plotted against its colour. The slope of this line `
    + `is the second-order extinction coefficient, ${fmt.num(a.colourSlope, 1)} ± ${fmt.num(a.colourSlopeError, 1)} `
    + `mmag per airmass per magnitude of B−V over ${fmt.int(a.slopeStars)} stars. Red stars fade more slowly `
    + `than blue ones as the air thickens, which is the direction the physics demands. Its zero point is `
    + `arbitrary, since it depends on the ensemble's own colour; only the slope is the measurement.`;
}


// The per-star measurements, which the reduction has always returned and this panel used to
// discard. One number for a whole frame says nothing about WHICH stars carry it, and the colour
// column is what a differential measurement groups on - a red target against blue comparisons is
// where second-order extinction shows.
function renderStarTable(d) {
  const rows = d.matches || [];
  $('starFold').hidden = rows.length === 0;
  if (!rows.length) return;

  const shown = rows.slice(0, 300);
  $('starNote').textContent =
    `${fmt.int(rows.length)} stars matched to the injected catalogue`
    + (rows.length > shown.length ? `, brightest ${shown.length} listed` : '')
    + '. Recovered is what the pixels gave back; residual is that minus the magnitude that went in. '
    + 'Saturated stars are marked and are excluded from the zero point, the colour term and the scatter.';

  $('starRows').innerHTML = shown.map((m) => {
    const cls = m.saturated ? ' class="miss"' : '';
    const bad = m.residualMag !== null && Math.abs(m.residualMag) > 0.05;
    return `<tr${cls}>`
      + `<td>${fmt.num(m.trueMagnitude, 2)}</td>`
      + `<td>${m.colourBv === null || m.colourBv === undefined ? 'n/a' : fmt.num(m.colourBv, 2)}</td>`
      + `<td>${fmt.num(m.recoveredMagnitude, 3)}</td>`
      + `<td${bad ? ' class="tag below"' : ''}>${fmt.num(m.residualMag, 3)}</td>`
      + `<td>${fmt.num(m.snr, 0)}</td>`
      + `<td>${m.fluxElectrons === null || m.fluxElectrons === undefined ? 'n/a' : fmt.int(m.fluxElectrons)}</td>`
      + `<td>${m.trueElectrons === null || m.trueElectrons === undefined ? 'n/a' : fmt.int(m.trueElectrons)}</td>`
      + `<td>${fmt.num(m.raDeg, 4)}</td>`
      + `<td>${fmt.num(m.decDeg, 4)}</td>`
      + `</tr>`;
  }).join('');
}

// ---------------------------------------------------------------------- the noise bridge
//
// Two independent noise models, subtracted on the sequence's own stars. The imaging path is the
// one ACCURACY.md's cross-validations cover; the light-curve path is the one every detection in
// this program actually runs on, and its noise side was checked against nothing until there was a
// sequence to check it against. A model that under-predicts noise calls planets detectable that
// are not, and a yield map inherits the factor whole.

$('bridgeRun').onclick = async () => {
  const mine = modeReceipt();
  if (!state.sequence) return;

  const btn = $('bridgeRun');
  btn.disabled = true;
  btn.textContent = 'Comparing…';
  $('bridgeError').hidden = true;

  try {
    const r = await fetch(`/api/sequences/${state.sequence.id}/noise-bridge`);
    const d = await r.json();
    if (!ofThisMode(mine)) return;
    if (!r.ok) {
      $('bridgeError').hidden = false;
      $('bridgeError').textContent = d.error || 'The comparison failed.';
      return;
    }

    $('bridgeVerdict').hidden = false;
    $('bridgeCurve').textContent = fmt.num(d.curveOverMeasured, 3);
    $('bridgeImaging').textContent = fmt.num(d.imagingOverMeasured, 3);
    $('bridgeStars').textContent = fmt.int(d.stars);

    const off = (1 - d.curveOverMeasured) * 100;
    $('bridgeNote').textContent =
      `Over ${fmt.int(d.stars)} stars in all ${fmt.int(d.frames)} frames. `
      + (d.curveOverMeasured < 0.95
          ? `The light-curve model predicts ${fmt.num(d.curveOverMeasured, 3)} of the scatter these frames `
            + `actually show, optimistic by ${fmt.num(off, 0)} %. On a yield map that is planets called `
            + `detectable that are not.`
          : d.curveOverMeasured > 1.05
          ? `The light-curve model predicts ${fmt.num(d.curveOverMeasured, 3)} of the measured scatter: `
            + `pessimistic, which costs detections rather than inventing them.`
          : `The light-curve model matches the measured scatter to better than 5 %.`)
      + ` The imaging error bar reads ${fmt.num(d.imagingOverMeasured, 3)}; it carries no scintillation `
      + `term, which is not an omission: scintillation is an atmospheric transfer effect and not a `
      + `term of the CCD equation.`;

    const rows = (d.rows || []).slice(0, 200);
    $('bridgeTable').hidden = rows.length === 0;
    $('bridgeRows').innerHTML = rows.map((m) =>
      `<tr>`
      + `<td>${fmt.num(m.v, 2)}</td>`
      + `<td>${fmt.num(m.bv, 2)}</td>`
      + `<td>${fmt.num(m.measured * 1000, 2)} ppt</td>`
      + `<td>${fmt.num(m.imaging * 1000, 2)} ppt</td>`
      + `<td>${fmt.num(m.curve * 1000, 2)} ppt</td>`
      + `<td>${fmt.num(m.curveOverMeasured, 2)}</td>`
      + `<td>${fmt.num(m.imagingOverMeasured, 2)}</td>`
      + `</tr>`).join('');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Compare';
  }
};

// ======================================================================================
// WATER VAPOUR
//
// The one weather term this program models, and the reason it is worth a control rather than a
// constant: it absorbs in narrow bands in the red, so it does NOT cancel in the differential ratio
// a transit is measured in - unlike everything grey, which does.
//
// Three modes, and the choice is the experiment. Constant is the control. Analytic injects a KNOWN
// signal, which is the one thing a real night cannot provide and the whole reason a simulator can
// answer "how precisely would I have to measure the water to recover this transit". Measured drives
// the simulation from a real record.
// ======================================================================================

function pwvModeChanged() {
  const mode = $('pwvMode').value;
  $('pwvConstant').hidden = mode !== 'constant';
  $('pwvAnalytic').hidden = mode !== 'analytic';
  $('pwvMeasured').hidden = mode !== 'measured';

  $('pwvOut').textContent =
    mode === 'none' ? 'absent'
    : mode === 'constant' ? `${$('pwvMm').value} mm`
    : mode === 'analytic' ? `${$('pwvMean').value} ± ${$('pwvAmp').value} mm`
    : 'measured';

  drawPwvCurve();

  $('pwvHint').textContent =
    mode === 'none'
      ? 'The frame is taken without the term, exactly as it was before the term existed.'
    : mode === 'constant'
      ? 'One column for the whole frame. This is the control an injection-recovery experiment needs: '
        + 'the run against which a varying column is compared.'
    : mode === 'analytic'
      ? 'Not a weather model and not offered as one. It is a KNOWN signal to inject, so that a '
        + 'correction can be scored against truth, which is the thing a real night cannot give you, '
        + 'because no real dataset knows its own true water column.'
      : 'An instant and a column in millimetres per line, whitespace or comma separated. ISO or '
        + 'seconds since J2000; anything after # is ignored, so a GNSS archive file usually pastes '
        + 'in unedited. Outside the record the value is held flat rather than extrapolated.';
}

// BOTH OF THESE SIT ABOVE THE FIRST CALL THAT READS THEM. pwvModeChanged() runs at the top level a
// few lines down, and it reaches drawPwvCurve, which increments the token; the token used to be
// declared with `let` further down the file, past that call, so the first draw on every page load
// died in the temporal dead zone with "Cannot access 'pwvCurveToken' before initialization", an
// unhandled rejection the console reported and the page did not: the water panel simply came up
// empty until something else redrew it. Same for the last plot kept for resizes.
let pwvCurveToken = 0;
// The last transmission answer drawn, kept so a resize can redraw it from the numbers already in
// hand rather than asking the server again. Null when the box shows a message and no curve.
let pwvLastPlot = null;

// EVERY control that changes the column, not just the obvious ones: period and drift alter the
// value at the frame's instant, and fired no redraw at all, so the panel kept plotting a column
// the capture would not use.
for (const id of ['pwvMode', 'pwvMm', 'pwvMean', 'pwvAmp', 'pwvPeriod', 'pwvDrift', 'pwvSeries']) {
  $(id).addEventListener('input', pwvModeChanged);
  $(id).addEventListener('change', pwvModeChanged);
}
$('capFilter').addEventListener('change', drawPwvCurve);
pwvModeChanged();

// ======================================================================================
// THE CURVE THE INTEGRAL ACTUALLY SEES.
//
// A water column is a number in a FITS header until you can look at what it did. This plots
// the three things the passband integral is built from, over exactly the span it integrates:
// the water's transmission, the filter's own response, and their product. It is the difference
// between "the term is on" and "here is where it took the light from".
//
// Drawn on band MEANS rather than samples: a water spectrum has tens of thousands of lines, and
// sampling it at 400 points would draw a picture of the sampling.
// ======================================================================================

/**
 * The airmass the FRAME will be taken through, and why the plot must not pick its own.
 *
 * Water absorption scales with the air column, so a transmission plotted at one airmass against a
 * frame exposed at another is a picture of a different night. tools/pwv_pair.py made exactly that
 * mistake - it asked the table at 1.5 while its frames were at 1.02 - and its prediction came out
 * half again too large, which looked like a physics error rather than a question asked wrong.
 *
 * The number comes from the SERVER: /api/forecast carries the airmass of every cell, computed by
 * Core's own Kasten & Young. Nothing here recomputes it. A booked slot uses that slot's cell; with
 * nothing booked the server will schedule the forecast's best moment, so the plot uses that one.
 * Returns null when there is no forecast to ask, and the caller says so rather than inventing 1.5.
 */
function frameAirmass() {
  const f = lastForecast;
  if (!f) return null;
  // NOTHING BOOKED MEANS THE SERVER'S 25-HOUR SCAN, not the forecast's 30-night best cell. Those
  // are two different searches and they land weeks apart: on one field the best cell was 26 nights
  // out at airmass 1.54 while the frame was taken that night at 1.83, so the panel under-quoted the
  // water loss by 18 % while captioning it "the moment the server will schedule". The server now
  // reports the instant the capture will really use, and this reads that.
  if (!state.fcStartUt) {
    return typeof f.scheduledAirmass === 'number' && isFinite(f.scheduledAirmass)
      ? f.scheduledAirmass : null;
  }
  if (!f.airmass || !(state.fcStartUt >= f.startUt)) return null;
  const idx = Math.floor((state.fcStartUt - f.startUt) / f.cellSeconds);
  const x = f.airmass[idx];
  return typeof x === 'number' && isFinite(x) ? x : null;
}

async function drawPwvCurve() {
  const box = $('pwvCurveBox');
  // EVERY EXIT TAKES THE TOKEN, including the ones that hide the box. Without that, an in-flight
  // request still held the current token and, on arrival, un-hid the panel the observer had just
  // switched off - leaving a confident transmission plot under a control reading "not modelled",
  // for a column the capture would not use.
  const token = ++pwvCurveToken;
  const mode = $('pwvMode').value;
  const scope = selectedScope();
  // In orbit there is no water column; the whole row is hidden rather than left as a dead control.
  const grounded = scope && !scope.isSpaceBased;
  $('pwvRow').hidden = !grounded;
  if (mode === 'none' || !grounded) { box.hidden = true; return; }

  // THE SERVER RESOLVES THE SERIES, because it is the one that will drive the frame. Parsing a
  // pasted record here as well meant plotting a different column than the capture used - the panel
  // read the last token of each line, the parser reads the second.
  const body = pwvRequestBody();
  if (!body) {
    // An empty measured textarea makes pwvRequestBody return undefined, so the capture goes out
    // with NO water at all while the control still reads "measured". Silence there is the thing
    // this panel exists to stop.
    box.hidden = false;
    clearPwvCanvas();
    $('pwvCurveHint').textContent = mode === 'measured'
      ? 'No measurements pasted yet, so no water-vapour term will be applied to the frame at all: '
        + 'the control reads "measured" but the capture will go out dry.'
      : 'This water-vapour series is incomplete, so no term will be applied to the frame.';
    return;
  }

  // THE INSTANT THE FRAME WILL BE TAKEN AT, not "now". A booked slot is that instant; with nothing
  // booked the server schedules the forecast's best moment, which is the same cell frameAirmass()
  // prices the air column at. Asking about any other instant plots a different night.
  const atIso = state.fcStartIso || forecastBestIso();
  let series;
  try {
    const r = await fetch('/api/pwv/series' + (atIso ? `?atUtc=${encodeURIComponent(atIso)}` : ''), {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    });
    series = await r.json();
    if (token !== pwvCurveToken) return;
    if (!r.ok || !series || series.meanMm === undefined) {
      box.hidden = false;
      $('pwvCurveHint').textContent =
        (series && series.error) || 'That water-vapour series could not be read.';
      clearPwvCanvas();
      return;
    }
  } catch { if (token === pwvCurveToken) { box.hidden = true; } return; }

  // THE COLUMN AT THAT INSTANT, not the series mean. The server returns both and the panel used to
  // plot the mean: pasting a record that runs 1 to 9 mm drew the curve for 5 mm and quoted 1.03
  // mmag, while the frame was exposed through 9 mm and lost 1.91 - the panel disagreeing with the
  // capture about the very number it exists to show.
  const mm = typeof series.mmAtEpoch === 'number' ? series.mmAtEpoch : series.meanMm;
  const varies = series.maxMm - series.minMm > 0.005;
  const caveat = [
    varies ? `the column at ${atIso ? 'that instant' : 'the scheduled moment'}; over the run it goes `
           + `${fmt.num(series.minMm, 2)} to ${fmt.num(series.maxMm, 2)} mm` : null,
    series.coversEpoch === false
      ? 'and that instant is outside the pasted record, so the value is held flat at its nearest end'
      : null,
    (series.notes || []).join(' ') || null,
  ].filter(Boolean).join('; ');
  return plotPwv(token, scope.name, mm, caveat || null);
}

/**
 * The instant the server would schedule if nothing is booked: the forecast's best cell, which is
 * exactly what /api/capture picks when atUtc is absent. Null when there is no forecast to ask.
 */
function forecastBestIso() {
  // The instant an unbooked capture will actually be taken at, as the SERVER computes it. This
  // used to derive the forecast's best cell and call it "exactly what /api/capture picks when
  // atUtc is absent", which was false: bestUt grades thirty nights, the capture scans twenty-five
  // hours. The server now publishes the one the capture uses.
  const f = lastForecast;
  return (f && f.scheduledUtc) ? f.scheduledUtc : null;
}

function clearPwvCanvas() {
  pwvLastPlot = null;
  const cv = $('pwvCurve');
  if (cv.clientWidth) setupCanvas(cv); else cv.getContext('2d').clearRect(0, 0, cv.width, cv.height);
}

function redrawPwvCurve() {
  if (pwvLastPlot && !$('pwvCurveBox').hidden && !$('pwvRow').hidden) paintPwv(pwvLastPlot);
}

/**
 * The curve itself, through setupCanvas like every other chart here: logical height in data-h,
 * bitmap sized from the element's own width, drawing in CSS pixels. This used to draw into a fixed
 * 640 by 180 bitmap under the page's "canvas { width: 100% }" rule, which stretched that bitmap to
 * whatever the column measured and squashed it back to 180 px tall: the one chart on the page
 * whose text and lines were the wrong shape.
 */
function paintPwv(d) {
  const cv = $('pwvCurve');
  if (!cv.clientWidth) return;
  const { g: ctx, w: W, h: H } = setupCanvas(cv);
  const L = 44, R = 10, T = 10, B = 24;
  const css = getComputedStyle(document.documentElement);
  const ink = css.getPropertyValue('--ink') || '#dfe6ee';
  const dim = css.getPropertyValue('--dim') || '#8a97a6';

  const xs = d.curve.map(p => p.nm);
  const x0 = xs[0], x1 = xs[xs.length - 1];
  const px = nm => L + (W - L - R) * (nm - x0) / (x1 - x0);
  const py = t => T + (H - T - B) * (1 - t);

  ctx.strokeStyle = dim; ctx.globalAlpha = 0.35; ctx.lineWidth = 1;
  for (const t of [0, 0.5, 1]) {
    ctx.beginPath(); ctx.moveTo(L, py(t)); ctx.lineTo(W - R, py(t)); ctx.stroke();
  }
  ctx.globalAlpha = 1; ctx.fillStyle = dim; ctx.font = '10px ui-monospace, monospace';
  ctx.textAlign = 'right';
  for (const t of [0, 0.5, 1]) ctx.fillText(t.toFixed(1), L - 6, py(t) + 3);
  ctx.textAlign = 'center';
  for (const nm of [x0, 0.5 * (x0 + x1), x1]) ctx.fillText(nm.toFixed(0), px(nm), H - 8);
  ctx.textAlign = 'left';

  const line = (key, colour, width, alpha) => {
    ctx.beginPath(); ctx.strokeStyle = colour; ctx.lineWidth = width; ctx.globalAlpha = alpha;
    d.curve.forEach((p, i) => (i ? ctx.lineTo(px(p.nm), py(p[key])) : ctx.moveTo(px(p.nm), py(p[key]))));
    ctx.stroke(); ctx.globalAlpha = 1;
  };
  line('filter', dim, 1, 0.7);
  line('water', '#5fb0ff', 1, 0.9);
  line('product', ink, 1.6, 1);
}

async function plotPwv(token, telescope, mm, caveat) {
  const box = $('pwvCurveBox');
  const x = frameAirmass();
  let d;
  try {
    const r = await fetch(`/api/pwv/transmission?pwv=${mm}&airmass=${x === null ? 1.5 : x}`
                        + `&telescope=${encodeURIComponent(telescope)}`
                        + `&filter=${encodeURIComponent(fieldBand())}&points=400`);
    d = await r.json();
    if (token !== pwvCurveToken) return;
    if (!r.ok) {
      box.hidden = false;
      $('pwvCurveHint').textContent = d.error || 'The transmission curve is unavailable.';
      clearPwvCanvas();
      return;
    }
  } catch { if (token === pwvCurveToken) { box.hidden = true; } return; }

  box.hidden = false;
  pwvLastPlot = d;
  paintPwv(d);

  const whichX = x === null
    ? `at airmass ${fmt.num(d.airmass, 2)}, a reference value: no forecast has answered yet, so this `
      + `is not the air column the frame will be exposed through`
    : `at airmass ${fmt.num(d.airmass, 2)}, which is `
      + `${state.fcStartUt ? 'the slot booked on the calendar' : "the moment the server will schedule"}`;
  $('pwvCurveHint').textContent =
    `${d.telescopeDisplay}, ${d.filter}, ${d.fromNm} to ${d.toNm} nm ${whichX}. `
  + `${d.pwvMm} mm of water transmits ${(100 * d.meanTransmission).toFixed(3)} % of the band on `
  + `average, ${d.lossMmagFlat.toFixed(2)} mmag for a flat spectrum, against the ${d.referencePwvMm} mm `
  + `reference the table is measured from. (The library carries ozone and molecular oxygen too, and `
  + `those do not vary with the water; referencing to its driest column removes them, so they are not `
  + `counted twice against the site's own measured extinction.) `
  + `Grey is the filter, blue the water, white their product, which is what the passband integral `
  + `sees.${caveat ? ' Plotted at ' + caveat + '.' : ''}`
  + (d.clippedToTable ? ' The passband runs past the table, and the plot stops where the table does.' : '');
}

// The request body's water block, or undefined when the term is off. Shared by the single capture
// and the sequence, so the two cannot drift into meaning different things.
function pwvRequestBody() {
  const mode = $('pwvMode').value;
  if (mode === 'none') return undefined;
  // Never sent for an orbital instrument: the control is hidden there, and the server refuses a
  // water series above the atmosphere rather than dropping it silently. Sending one anyway would
  // turn a hidden control into a rejected capture.
  const scope = selectedScope();
  if (!scope || scope.isSpaceBased) return undefined;
  if (mode === 'constant') return { mode: 'constant', mm: parseFloat($('pwvMm').value) };
  if (mode === 'analytic') {
    return {
      mode: 'analytic',
      meanMm: parseFloat($('pwvMean').value),
      amplitudeMm: parseFloat($('pwvAmp').value),
      periodHours: parseFloat($('pwvPeriod').value),
      driftMmPerDay: parseFloat($('pwvDrift').value),
    };
  }
  const text = $('pwvSeries').value.trim();
  if (!text) return undefined;
  return { mode: 'measured', series: text, label: 'pasted in the interface' };
}


// ======================================================================================
// LIGHT CURVE
//
// The fourth mode, and the only one that measures across time. The other three ask a question
// about one frame, one campaign or one archive record; this one takes a batch of frames at a
// regular interval, reduces each as it goes, and fits a depth out of the ratio.
//
// IT HAS TWO HALVES AND THEY MEET AT THE END.
//
//   ANALYTIC   the passband integral run against the water table. No frames, no exposure,
//              seconds to answer. It predicts what a column costs a given pair of stars, what
//              column accuracy a photometric budget demands, and how much of a water excursion
//              survives the baseline fit a transit pipeline runs.
//
//   MEASURED   real frames of a real field, through a real instrument, with a transit of known
//              depth injected into a catalogue star. It recovers a depth and reports the bias.
//
// The closure panel puts them side by side on the same field, the same instrument and the same
// stars, which is the only place either number can be checked against anything.
//
// THE RULE THIS SECTION OBEYS, and it has been broken here twice: THE PAGE ASKS, THE SERVER
// ANSWERS. Not one physical quantity below is computed in JavaScript. Everything drawn is a
// number the server returned; the arithmetic in this file is limited to axes, colours and pixel
// positions. When a figure needed something the endpoints did not have, the endpoint gained it.
// ======================================================================================

const LC_BLOCKS = ['lcChainBlock', 'lcFieldBlock', 'lcPredictBlock', 'lcTransferBlock',
                   'lcInstrumentBlock'];
const LC_PANELS = ['lcBandPanel', 'lcLossPanel', 'lcBandsPanel', 'lcReqPanel', 'lcColourPanel',
                   'lcTransferPanel', 'lcStarsPanel', 'lcCurvePanel', 'lcFramesPanel',
                   'lcDepthPanel', 'lcPairPanel', 'lcClosurePanel'];

/* ONE SOURCE PER QUANTITY, WHICHEVER MODE IS ASKING. The capture panel and the light-curve panel
   both need a field and a band, and the tempting shape is a copy of each control. That is exactly
   how the water term went wrong once before: two controls that were meant to agree, did not, and
   the plot showed a column the frame was never exposed through. These read whichever control is
   on screen, and every request builder goes through them. */
function fieldRa() {
  return parseFloat((state.mode === 'lc' ? $('lcRa') : $('capRa')).value);
}
function fieldDec() {
  return parseFloat((state.mode === 'lc' ? $('lcDec') : $('capDec')).value);
}
function fieldBand() {
  const el = state.mode === 'lc' ? $('lcBand') : $('capFilter');
  return (el && el.value) || 'Luminance';
}

/** The instrument's bands, in the picker, with each one's span in the tooltip. */
function fillLcBands() {
  const sel = $('lcBand');
  if (!sel) return;
  const scope = selectedScope();
  const was = sel.value;
  sel.textContent = '';
  if (!scope) return;
  const names = (scope.bands && scope.bands.length) ? scope.bands.map((b) => b.name) : scope.filters;
  for (const n of names) {
    const opt = document.createElement('option');
    opt.value = n;
    opt.textContent = (scope.filterLabels && scope.filterLabels[n]) || n;
    const b = scope.bands && scope.bands.find((x) => x.name === n);
    if (b && b.centralWavelengthNm) {
      const half = (b.bandwidthAngstrom || 0) / 20;
      opt.title = `${(b.centralWavelengthNm - half).toFixed(0)}-${(b.centralWavelengthNm + half).toFixed(0)} nm`
                + (b.measuredCurve ? ', measured curve' : ', top-hat');
    }
    sel.appendChild(opt);
  }
  if (was && names.includes(was)) sel.value = was;
  lcBandHint();
}

function lcBandHint() {
  const scope = selectedScope();
  const b = scope && scope.bands && scope.bands.find((x) => x.name === $('lcBand').value);
  if (!b || !b.centralWavelengthNm) { $('lcBandHint').textContent = ''; return; }
  const half = (b.bandwidthAngstrom || 0) / 20;
  $('lcBandHint').textContent =
    `${b.name}: ${(b.centralWavelengthNm - half).toFixed(0)} to ${(b.centralWavelengthNm + half).toFixed(0)} nm, `
    + (b.measuredCurve
        ? `measured curve, ${b.curvePoints || '?'} points.`
        : `top-hat, no measured curve supplied.`);
}

/** The nine DUET bands, which is what this whole term was measured on. */
const LC_DUET_BANDS = [
  ["g'", 400, 550], ["r'", 550, 700], ["i'", 700, 850], ["z'", 850, 1000], ["I+z'", 750, 1000],
  ['Y', 970, 1070], ['YJ', 970, 1330], ['J', 1170, 1330], ['Hs', 1500, 1650],
];

function seedLcBandList() {
  const ta = $('lcBands');
  if (ta && !ta.value.trim()) {
    ta.value = LC_DUET_BANDS.map(([n, a, b]) => `${n}, ${a}, ${b}`).join('\n');
  }
}

/** Parse the band textarea. `name, from, to` per line; a name alone means a band the instrument has. */
function parseLcBands() {
  const out = [], bad = [];
  for (const raw of ($('lcBands').value || '').split('\n')) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    const parts = line.split(',').map((t) => t.trim());
    if (parts.length === 1) { out.push({ name: parts[0] }); continue; }
    if (parts.length < 3) { bad.push(line); continue; }
    const from = Number(parts[1]), to = Number(parts[2]);
    if (!isFinite(from) || !isFinite(to) || !(to > from)) { bad.push(line); continue; }
    out.push({ name: parts[0], fromNm: from, toNm: to });
  }
  return { bands: out, bad };
}

function fillLcSites() {
  const sel = $('ciSite');
  if (!sel || !state.boot) return;
  const was = sel.value;
  sel.innerHTML = state.boot.sites.map((s) => `<option value="${s.id}">${s.name} · ${s.country}</option>`).join('');
  if (was) sel.value = was;
}

/** Which steps of the chain are done, so the column says where you are. */
function renderLcChain() {
  const done = {
    1: !!selectedScope(),
    2: !!(state.lcStars && state.lcStars.length),
    3: !!state.lcReq,
    4: !!(state.sequence && state.sequence.state === 'finished'),
    5: !!state.lcDepth,
  };
  let now = 1;
  for (let i = 1; i <= 5; i++) if (done[i]) now = i + 1;
  for (const li of document.querySelectorAll('#lcChain li')) {
    const n = Number(li.dataset.step);
    li.classList.toggle('done', !!done[n]);
    li.classList.toggle('now', n === now && !done[n]);
  }
}

const lcEsc = (t) => String(t === null || t === undefined ? '' : t)
  .replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

function lcFail(id, message) {
  const el = $(id);
  el.hidden = !message;
  el.textContent = message || '';
}

// ---------------------------------------------------------------------- step 2: the field's stars

$('lcProbe').onclick = async () => {
  const mine = modeReceipt();
  const scope = selectedScope();
  if (!scope) return;
  const btn = $('lcProbe');
  btn.disabled = true; btn.textContent = 'Exposing…';
  lcFail('lcProbeError', '');

  try {
    const r = await fetch('/api/capture', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        telescope: scope.name,
        site: $('site').value,
        raDeg: fieldRa(),
        decDeg: fieldDec(),
        filter: fieldBand(),
        exposureSeconds: parseFloat($('lcProbeExp').value),
        binning: parseInt($('lcBin').value, 10),
        objectName: 'light-curve probe',
        // The probe is a LOOK, not a measurement, so it carries no water term: the point is to
        // find out which stars this instrument can measure in this field, and a column would only
        // make them fainter without changing which ones qualify.
      }),
    });
    const d = await r.json();
    if (!ofThisMode(mine)) return;
    if (!r.ok) { lcFail('lcProbeError', d.error || 'The probe frame was refused.'); return; }

    state.capture = d.id;
    btn.textContent = 'Reducing…';
    const pr = await fetch(`/api/captures/${d.id}/photometry`);
    const pd = await pr.json();
    if (!ofThisMode(mine)) return;
    if (!pr.ok) { lcFail('lcProbeError', pd.error || 'The frame could not be reduced.'); return; }

    state.lcStars = pd.matches || [];
    state.lcProbe = { id: d.id, reliable: pd.reliable, detection: pd.detection };
    state.lcProbedWith = lcScopeStamp(scope);
    markLcStarsStale(false);
    renderLcStars();
    renderLcChain();
  } catch (e) {
    lcFail('lcProbeError', String(e));
  } finally {
    btn.disabled = false; btn.textContent = 'Take a probe frame and list its stars';
  }
};

/* THE ARRAY THE PAGE USED TO IGNORE.
   /api/captures/{id}/photometry has always returned every matched star with its position, its
   colour, its magnitude and its signal-to-noise. Choosing a transit host out of that was a
   scripting job: the engine refuses a host that is not a real star ("the transit would have been
   injected into empty sky"), and the sequence says so when the host is not measurable in every
   frame ("the recovered depth means nothing"). Both of those are avoidable in two clicks if the
   list is on the page, sorted, with the disqualifying conditions marked. */
function renderLcStars() {
  const rows = state.lcStars || [];
  $('lcStarsPanel').hidden = rows.length === 0;
  if (!rows.length) return;

  const usable = (m) => !m.saturated && m.snr !== null && m.snr >= 100;
  const filtered = rows.filter((m) =>
    state.lcStarFilter === 'all' ? true
    : state.lcStarFilter === 'saturated' ? m.saturated
    : usable(m));

  const { key, dir } = state.lcStarSort;
  const sorted = filtered.slice().sort((a, b) => {
    const av = a[key], bv = b[key];
    if (av === null || av === undefined) return 1;
    if (bv === null || bv === undefined) return -1;
    return av === bv ? 0 : (av < bv ? -1 : 1) * dir;
  });

  const shown = sorted.slice(0, 400);
  const nUsable = rows.filter(usable).length;
  $('lcStarsNote').textContent = `${fmt.int(filtered.length)} shown of ${fmt.int(rows.length)} matched`;
  $('lcStarsCaption').textContent =
    `${fmt.int(nUsable)} of ${fmt.int(rows.length)} are usable as a host. The reddest gives the `
    + `largest water term.`
    + (state.lcProbe && state.lcProbe.reliable === false
        ? ' This frame reduced UNRELIABLY, so its own numbers are not to be believed.' : '');

  $('lcStarRows').innerHTML = shown.map((m, i) => {
    const picked = state.lcHost
      && Math.abs(state.lcHost.raDeg - m.raDeg) < 1e-6
      && Math.abs(state.lcHost.decDeg - m.decDeg) < 1e-6;
    const state_ = m.saturated ? '<span class="tag below">saturated</span>'
      : (m.snr === null || m.snr < 100) ? '<span class="tag below">S/N low</span>'
      : '<span class="tag detected">usable</span>';
    return `<tr class="${picked ? 'picked' : (m.saturated ? 'miss' : '')}" data-star="${i}">`
      + `<td>${fmt.num(m.trueMagnitude, 2)}</td>`
      + `<td>${m.colourBv === null || m.colourBv === undefined ? 'n/a' : fmt.num(m.colourBv, 2)}</td>`
      + `<td>${fmt.num(m.snr, 0)}</td>`
      + `<td>${m.fluxElectrons === null || m.fluxElectrons === undefined ? 'n/a' : fmt.int(m.fluxElectrons)}</td>`
      + `<td>${fmt.num(m.raDeg, 5)}</td>`
      + `<td>${fmt.num(m.decDeg, 5)}</td>`
      + `<td>${state_}</td>`
      + `<td><button class="rowbtn" data-host="${i}">${picked ? 'host ✓' : 'use as host'}</button></td>`
      + `</tr>`;
  }).join('');

  // The button carries the index into the SHOWN list, so it is bound after each render rather
  // than delegated on a stale array.
  for (const btn of $('lcStarRows').querySelectorAll('button[data-host]')) {
    btn.onclick = () => useAsHost(shown[Number(btn.dataset.host)]);
  }
}

/** Put a real star's position into the transit controls, and say what it will cost. */
function useAsHost(star) {
  if (!star) return;
  state.lcHost = star;
  $('seqTransit').checked = true;
  $('seqTransitBox').hidden = false;
  $('seqTransitHint').hidden = false;
  $('seqTrRa').value = star.raDeg.toFixed(6);
  $('seqTrDec').value = star.decDeg.toFixed(6);
  // Three arcsec is the engine's own default and is comfortably inside one pixel of every
  // instrument on the roster, so it matches this star and nothing else.
  if (!parseFloat($('seqTrRad').value)) $('seqTrRad').value = '3';
  // The predicted requirement is a function of the HOST's colour, so choosing a host changes it.
  if (star.colourBv !== null && star.colourBv !== undefined) {
    $('lcTargetK').dataset.fromColour = String(star.colourBv);
  }
  renderLcStars();
  renderLcChain();
  $('seqSetup').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

for (const th of document.querySelectorAll('#lcStarTable th[data-sort]')) {
  th.onclick = () => {
    const key = th.dataset.sort;
    state.lcStarSort = { key, dir: state.lcStarSort.key === key ? -state.lcStarSort.dir : -1 };
    for (const other of document.querySelectorAll('#lcStarTable th[data-sort]')) {
      other.classList.remove('sorted-asc', 'sorted-desc');
    }
    th.classList.add(state.lcStarSort.dir > 0 ? 'sorted-asc' : 'sorted-desc');
    renderLcStars();
  };
}
for (const chip of document.querySelectorAll('#lcStarChips .chip')) {
  chip.onclick = () => {
    state.lcStarFilter = chip.dataset.starfilter;
    for (const c of document.querySelectorAll('#lcStarChips .chip')) c.classList.toggle('on', c === chip);
    renderLcStars();
  };
}


/**
 * THE BAND THE WHOLE ANALYTIC HALF IS ABOUT.
 *
 * Four panels - the drawn band, the loss curve, the colour matrix and the transfer function - are
 * each a property of ONE band, and they have to be the same one or the page is four answers about
 * four different things under one heading. Two of them independently fell back to `bands[0]`
 * whenever the instrument's own band was not in the list, so a page set to Luminance on a RedCat
 * silently drew a g' matrix and swept a g' transfer function. One function, so there is one answer
 * to "which band is this".
 *
 * The order of preference is: what the reader explicitly picked; what the server chose when it
 * derived the requirement (the widest differential, the band where it actually bites); the band in
 * the instrument's own picker; and failing all of those, the first line of the list.
 */
function lcChosenBand() {
  const { bands } = parseLcBands();
  const wanted = state.lcGridBand
              || (state.lcReq && state.lcReq.colourGrid && state.lcReq.colourGrid.band)
              || $('lcBand').value;
  return bands.find((b) => b.name === wanted)
      || bands.find((b) => b.name === $('lcBand').value)
      || bands[0]
      || { name: $('lcBand').value };
}

// ---------------------------------------------------------------------- step 3: predict

$('lcBandsDuet').onclick = () => {
  $('lcBands').value = LC_DUET_BANDS.map(([n, a, b]) => `${n}, ${a}, ${b}`).join('\n');
};
$('lcBandsInstrument').onclick = () => {
  const scope = selectedScope();
  if (!scope) return;
  const names = (scope.bands && scope.bands.length) ? scope.bands.map((b) => b.name) : scope.filters;
  // Named alone, with no span: the server then integrates the instrument's OWN passband, measured
  // curve and all, rather than a rectangle standing in for it.
  $('lcBands').value = names.join('\n');
};

$('lcBand').addEventListener('change', () => {
  // THE READER'S PICK WINS. lcChosenBand() prefers the server's own choice once a requirement has
  // been derived, so changing this select redrew the band panel and left the loss curve, the
  // colour matrix and the transfer function on whatever band the server had picked - four panels
  // under one heading answering about two different bands. An explicit change here is exactly the
  // "what the reader explicitly picked" that the order of preference puts first.
  state.lcGridBand = $('lcBand').value;
  lcBandHint(); drawLcBand();
  if (state.lcLoss) drawLcLoss();
  if (state.lcReq) renderLcColourGrid(state.lcReq.colourGrid, state.lcReq);
});

$('lcPredict').onclick = async () => {
  const mine = modeReceipt();
  const scope = selectedScope();
  if (!scope) return;
  const { bands, bad } = parseLcBands();
  if (!bands.length) {
    lcFail('lcPredictError', 'Give at least one band: a name alone for one this instrument carries, '
                           + 'or "name, fromNm, toNm" for one it does not.');
    return;
  }
  if (bad.length) {
    lcFail('lcPredictError', `Could not read: ${bad.slice(0, 3).map(lcEsc).join(' | ')}`
                           + `. Each line is "name, fromNm, toNm", or a band name on its own.`);
    return;
  }

  const btn = $('lcPredict');
  btn.disabled = true; btn.textContent = 'Integrating…';
  lcFail('lcPredictError', '');

  const airmass = parseFloat($('lcX').value);
  const pwv = parseFloat($('lcPwv').value);
  const step = parseFloat($('lcStep').value);
  const targetK = parseFloat($('lcTargetK').value);
  const compK = parseFloat($('lcCompK').value);

  try {
    // THE BAND THE GRID IS FOR. The colour matrix is a property of ONE band, so which one is not
    // an afterthought: an earlier version fell back to the first band in the list whenever the
    // instrument's own band was not among them, and drew a g' matrix under a panel the reader had
    // set to I+z'. Omitted, the server picks the band where the requirement actually bites - the
    // largest differential - and reports which it chose. `state.lcGridBand` overrides that once
    // the reader has picked one from the panel's own list.
    const gridBand = state.lcGridBand
      ? bands.find((b) => b.name === state.lcGridBand) || null
      : null;

    const r = await fetch('/api/pwv/requirement', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        telescope: scope.name, bands, airmass, pwvMm: pwv, stepMm: step,
        targetTeffK: targetK, compTeffK: compK,
        budgetPpm: parseFloat($('lcBudget').value),
        achievedMm: parseFloat($('lcAchieved').value),
        specMm: parseFloat($('lcSpec').value),
        gridBand: gridBand || undefined,
        // A grid wide enough to contain both the M dwarfs the effect is largest on and the
        // solar-type stars an ensemble is usually made of, including the matched diagonal.
        gridTargetTeffK: [2000, 2600, 3200, 4000],
        gridCompTeffK: [3000, 4000, 5000, 5800],
      }),
    });
    const d = await r.json();
    if (!ofThisMode(mine)) return;
    if (!r.ok) {
      lcFail('lcPredictError', d.error || 'The requirement could not be derived.');
      // NUMBERS MUST NOT OUTLIVE THE REQUEST THAT PRODUCED THEM. The panels kept the previous
      // run's table under the refusal, so a reader who mistyped a temperature saw an error message
      // beside a full set of figures that no longer answered anything they had asked. They are not
      // thrown away - that would punish a typo - but they are marked as belonging to the request
      // before this one.
      state.lcStaleReason = 'request';
      markLcPredictionStale(true);
      return;
    }
    state.lcStaleReason = null;
    markLcPredictionStale(false);
    state.lcReq = d;
    state.lcPredictedWith = lcScopeStamp(scope);

    // The loss curve, for the band in the picker: the figure that is Peter's own, plus the
    // differential column his does not have.
    // The loss curve is drawn for the SAME band as the matrix and the transfer function. The
    // requirement has just come back, so lcChosenBand() now resolves to whatever the server chose.
    const shown = lcChosenBand();
    const q = new URLSearchParams({
      telescope: scope.name, filter: shown.name || $('lcBand').value,
      airmass: String(airmass), points: '28',
      teffK: '2000,2600,3200,4000,5000,5800', compTeffK: String(compK),
    });
    if (shown.fromNm) q.set('fromNm', String(shown.fromNm));
    if (shown.toNm) q.set('toNm', String(shown.toNm));
    const lr = await fetch('/api/pwv/loss-curve?' + q);
    const ld = await lr.json();
    if (!ofThisMode(mine)) return;
    state.lcLoss = lr.ok ? ld : null;

    renderLcPrediction();
    drawLcBand();
    renderLcChain();
  } catch (e) {
    lcFail('lcPredictError', String(e));
  } finally {
    btn.disabled = false; btn.textContent = 'Predict';
  }
};

const LC_STALE_REQUEST_NOTE = 'The panels below answer the request BEFORE this one. They were kept '
  + 'rather than cleared, but they do not describe what was just asked.';

/**
 * Mark, or clear, the prediction panels as belonging to something other than what is now asked:
 * a request older than the last one (the default note), or an instrument other than the one
 * selected (`why` says which). The band panel is in the set because it is drawn for the same
 * instrument and band as the other four.
 */
function markLcPredictionStale(stale, why) {
  for (const id of ['lcBandPanel', 'lcBandsPanel', 'lcReqPanel', 'lcLossPanel', 'lcColourPanel']) {
    const el = $(id);
    if (el) el.classList.toggle('stale', !!stale);
  }
  const note = $('lcStaleNote');
  if (!note) return;
  note.hidden = !stale;
  note.textContent = stale ? (why || LC_STALE_REQUEST_NOTE) : '';
}

function markLcStarsStale(stale, why) {
  $('lcStarsPanel').classList.toggle('stale', !!stale);
  const note = $('lcStarsStale');
  note.hidden = !stale;
  note.textContent = stale ? why : '';
}

function markLcTransferStale(stale, why) {
  $('lcTransferPanel').classList.toggle('stale', !!stale);
  const note = $('lcTransferStale');
  note.hidden = !stale;
  note.textContent = stale ? why : '';
}

/** The instrument an answer was measured through, as the key every lookup uses and the label the reader saw. */
function lcScopeStamp(scope) {
  return { key: scope.name, label: `${scope.telescope} + ${scope.camera}` };
}

/**
 * THE INSTRUMENT CHANGED; WHAT ON THE PAGE STILL DESCRIBES THE OLD ONE.
 *
 * Nothing here was cleared when the instrument changed, so the four analytic panels, the transfer
 * function and the star list went on showing the previous instrument's numbers under the new
 * instrument's name: a requirement derived through a deep-depletion CCD read as if it were the
 * InGaAs arm's. The same rule as a refused request applies. Nothing is thrown away, because a
 * reader who flicks through the roster and comes back should not lose a probe frame that took a
 * minute to expose, but every panel measured through another instrument is dimmed and says which
 * one, and it stays that way until it is measured again. Coming back to the instrument it was
 * measured with clears the mark, and only that mark: a prediction refused before the change keeps
 * its own note.
 */
function refreshLcInstrumentStaleness(scope) {
  const other = (w) => !!(w && w.key !== scope.name);
  const why = (w) => `Measured through ${w.label}, not through ${scope.telescope} + ${scope.camera}, `
    + 'which is now selected. Kept rather than cleared, but it does not describe this instrument.';

  if (state.lcReq || state.lcLoss) {
    if (other(state.lcPredictedWith)) markLcPredictionStale(true, why(state.lcPredictedWith));
    else if (state.lcStaleReason === 'request') markLcPredictionStale(true);
    else markLcPredictionStale(false);
  }
  if (state.lcTransfer) {
    markLcTransferStale(other(state.lcTransferredWith),
                        other(state.lcTransferredWith) ? why(state.lcTransferredWith) : '');
  }
  if (state.lcStars && state.lcStars.length) {
    markLcStarsStale(other(state.lcProbedWith),
                     other(state.lcProbedWith)
                       ? why(state.lcProbedWith) + ' A different instrument sees different stars, '
                         + 'and which of them can be a host with it is not known until a probe frame is taken.'
                       : '');
  }
}

/**
 * The requirement rows in the order the reader asked for, or the server's own when no header has
 * been clicked. Six headers were styled as sortable, with the arrow and the pointer, and clicking
 * them did nothing: the handler below existed only for the star table. The chart under the table
 * takes the same rows, so sorting the one sorts the other. "no limit" sorts last under the sigma
 * column, which is where an unlimited band belongs when looking for the tightest.
 */
function lcSortedReqRows(rows) {
  const sort = state.lcReqSort;
  if (!sort) return rows;
  const val = (b) => (sort.key === 'requiredSigmaMm' && b.unlimited ? Infinity : b[sort.key]);
  return rows.slice().sort((a, b) => {
    const av = val(a), bv = val(b);
    if (av === null || av === undefined) return 1;
    if (bv === null || bv === undefined) return -1;
    if (typeof av === 'string' || typeof bv === 'string') return String(av).localeCompare(String(bv)) * sort.dir;
    return av === bv ? 0 : (av < bv ? -1 : 1) * sort.dir;
  });
}

for (const th of document.querySelectorAll('#lcReqTable th[data-sort]')) {
  th.onclick = () => {
    const key = th.dataset.sort;
    const was = state.lcReqSort;
    state.lcReqSort = { key, dir: was && was.key === key ? -was.dir : 1 };
    for (const other of document.querySelectorAll('#lcReqTable th[data-sort]')) {
      other.classList.remove('sorted-asc', 'sorted-desc');
    }
    th.classList.add(state.lcReqSort.dir > 0 ? 'sorted-asc' : 'sorted-desc');
    if (state.lcReq) renderLcPrediction();
  };
}

function renderLcPrediction() {
  const d = state.lcReq;
  if (!d) return;
  const rows = lcSortedReqRows((d.bands || []).filter((b) => !b.refusal));
  const refused = (d.bands || []).filter((b) => b.refusal);

  $('lcBandsPanel').hidden = rows.length === 0;
  $('lcReqPanel').hidden = rows.length === 0;
  $('lcLossPanel').hidden = !state.lcLoss;
  $('lcColourPanel').hidden = !(d.colourGrid && d.colourGrid.sigmaMm);

  $('lcBandsNote').textContent =
    `${d.telescopeDisplay} · airmass ${fmt.num(d.airmass, 2)} · ${fmt.num(d.pwvMm, 2)} ± ${fmt.num(d.stepMm, 2)} mm`;
  $('lcReqNote').textContent =
    `${fmt.num(d.targetTeffK, 0)} K against ${fmt.num(d.compTeffK, 0)} K · budget ${fmt.num(d.budgetPpm, 0)} ppm`;

  // The table, sortable, because nine bands is exactly the number where an eye wants to reorder.
  const cell = (b) => {
    const sig = b.requiredSigmaMm;
    const cls = b.unlimited ? 'cell-none' : (sig < 0.1 ? 'cell-tight' : sig > d.achievedMm ? 'cell-loose' : '');
    const txt = b.unlimited ? 'no limit' : `${fmt.num(sig, 3)} mm`;
    return `<td class="${cls}">${txt}</td>`;
  };
  // A ROW THAT CONTAINS NO DETECTOR SAYS SO. Past a quantum-efficiency curve's range the
  // response is held at its endpoint, and a constant multiplier cancels exactly out of a loss
  // ratio, so the row becomes a top-hat on the sky with no instrument in it. Served
  // indistinguishable from a real row, J and Hs came back at 201 and 211 µmag/mm on a
  // deep-depletion curve ending at 1100 nm AND on a flat-response roster instrument: not similar,
  // identical. Marked here, and the reason is on the row rather than in a footnote nobody reads.
  $('lcReqRows').innerHTML = rows.map((b) =>
    `<tr${b.detectorNote ? ' class="nodetector" title="' + lcEsc(b.detectorNote) + '"' : ''}>`
    + `<td>${lcEsc(b.band)}${b.detectorNote ? ' <span class="warnmark">no detector</span>' : ''}</td>`
    + `<td>${fmt.num(b.fromNm, 0)}-${fmt.num(b.toNm, 0)}</td>`
    + `<td>${fmt.num(b.absorbedUmagPerMm, 0)}</td>`
    + `<td>${fmt.num(b.differentialUmagPerMm, 0)}</td>`
    + `<td>${fmt.num(b.residualAtAchievedPpm, 0)} ppm</td>`
    + cell(b)
    + `</tr>`).join('')
    + refused.map((b) =>
      `<tr class="miss"><td>${lcEsc(b.band)}</td><td colspan="5">${lcEsc(b.refusal)}</td></tr>`).join('');

  $('lcBandsCaption').textContent =
    `Grey is what one star loses; cyan is what survives the ratio. Only the second limits a transit.`;

  // AND THE CAPTION MUST NOT CLAIM A BAND FITS when the row carries no detector. The old caption
  // listed J among the bands that "already fit inside 0.53 mm" on an instrument whose response
  // stops at 1100 nm.
  const blind = rows.filter((b) => b.detectorNote).map((b) => b.band);
  const blindNote = blind.length
    ? ` ${blind.join(', ')} lie outside this detector's published response, so those rows are the `
      + `atmosphere and the filter with no instrument in them.`
    : '';

  // A VERDICT IS ONLY GIVEN ON A BAND THAT CARRIES THE INSTRUMENT. The caption used to read
  // "g', r', i', J already fit inside 0.53 mm" on a detector whose response stops at 1100 nm: J
  // was being certified as safe on the strength of a number that contains no detector at all.
  // Blind rows are named separately, in blindNote, rather than folded into a verdict.
  const seeing = rows.filter((b) => !b.detectorNote);
  const tight = seeing.filter((b) => !b.unlimited && b.requiredSigmaMm < d.specMm);
  const loose = seeing.filter((b) => !b.unlimited && b.requiredSigmaMm > d.achievedMm);
  $('lcReqCaption').textContent =
    (tight.length
      ? `${tight.map((b) => b.band).join(', ')} need${tight.length === 1 ? 's' : ''} better than the `
        + `${fmt.num(d.specMm, 2)} mm goal. `
      : `No band here is tighter than the ${fmt.num(d.specMm, 2)} mm goal. `)
    + (loose.length
      ? `${loose.map((b) => b.band).join(', ')} already fit inside ${fmt.num(d.achievedMm, 2)} mm. `
        + `The verdict is per band, not per observatory.`
      : '')
    + blindNote;

  $('lcReqNotes').innerHTML = (d.notes || []).map((n) =>
    `<li class="${n.startsWith('THE SIGMA COLUMN') ? 'warn' : ''}">${lcEsc(n)}</li>`).join('');

  drawLcBandsChart(rows);
  drawLcRequirement(rows, d);
  renderLcColourGrid(d.colourGrid, d);
  if (state.lcLoss) drawLcLoss();
}

// ---------------------------------------------------------------------- the drawings
//
// Every one of these takes numbers the server returned and turns them into pixels. There is no
// physics in this half of the file, and there must never be: the panel that once parsed a pasted
// water record itself read a different column than the server did, and drew a confident curve for
// a column no frame was ever exposed through.

/** A log scale that survives a zero, which a µmag/mm column legitimately contains. */
function logScale(values, floorFactor = 1e-3) {
  const pos = values.filter((v) => v > 0);
  if (!pos.length) return null;
  const hi = Math.max(...pos);
  const lo = Math.max(Math.min(...pos), hi * floorFactor);
  return { lo: lo / 2, hi: hi * 1.6 };
}

function drawLcBandsChart(rows) {
  const cv = $('lcBandsChart');
  if (!cv || !cv.clientWidth || !rows.length) return;
  const { g, w, h } = setupCanvas(cv);

  const all = rows.flatMap((b) => [b.absorbedUmagPerMm, b.differentialUmagPerMm]);
  const sc = logScale(all);
  if (!sc) return;

  const L = 78, R = 16, T = 16, B = 46;
  const Y = (v) => h - B - (Math.log10(Math.max(v, sc.lo)) - Math.log10(sc.lo))
                          / (Math.log10(sc.hi) - Math.log10(sc.lo)) * (h - T - B);
  const slot = (w - L - R) / rows.length;

  // Decade gridlines, labelled, because a log axis without them cannot be read off.
  g.font = '10px ui-monospace, Menlo, monospace';
  g.textBaseline = 'middle'; g.textAlign = 'right';
  for (let e = Math.floor(Math.log10(sc.lo)); e <= Math.ceil(Math.log10(sc.hi)); e++) {
    const v = Math.pow(10, e);
    if (v < sc.lo || v > sc.hi) continue;
    g.strokeStyle = '#141a22';
    g.beginPath(); g.moveTo(L, Y(v)); g.lineTo(w - R, Y(v)); g.stroke();
    g.fillStyle = '#4d5867';
    g.fillText(v >= 1000 ? (v / 1000) + 'k' : String(v), L - 8, Y(v));
  }

  rows.forEach((b, i) => {
    const x0 = L + slot * i;
    const bw = Math.max(4, slot * 0.32);
    // Absorbed: what one star loses. Dim, because it is the number that does NOT limit anything.
    g.fillStyle = 'rgba(125,138,156,.45)';
    g.fillRect(x0 + slot * 0.12, Y(b.absorbedUmagPerMm), bw, h - B - Y(b.absorbedUmagPerMm));
    // Differential: what survives the ratio. Cyan, the measurement colour everywhere else here.
    g.fillStyle = 'rgba(94,207,255,.85)';
    g.fillRect(x0 + slot * 0.12 + bw + 2, Y(b.differentialUmagPerMm), bw, h - B - Y(b.differentialUmagPerMm));

    g.save();
    g.translate(x0 + slot / 2, h - B + 8);
    g.textAlign = 'right'; g.textBaseline = 'middle';
    g.rotate(-Math.PI / 4);
    g.fillStyle = '#7d8a9c';
    g.fillText(b.band, 0, 0);
    g.restore();
  });

  g.textAlign = 'left'; g.textBaseline = 'top';
  g.fillStyle = '#3d4757';
  g.fillText('µmag per mm of PWV', L, 2);
  g.fillStyle = 'rgba(125,138,156,.75)'; g.fillText('■ absorbed', w - 168, 2);
  g.fillStyle = 'rgba(94,207,255,.95)'; g.fillText('■ differential', w - 88, 2);
}

function drawLcRequirement(rows, d) {
  const cv = $('lcReqChart');
  if (!cv || !cv.clientWidth || !rows.length) return;
  const { g, w, h } = setupCanvas(cv);

  const finite = rows.filter((b) => !b.unlimited && b.requiredSigmaMm > 0);
  if (!finite.length) return;
  const vals = finite.map((b) => b.requiredSigmaMm).concat([d.specMm, d.achievedMm].filter((v) => v > 0));
  const sc = logScale(vals);
  if (!sc) return;

  const L = 78, R = 16, T = 16, B = 46;
  const Y = (v) => h - B - (Math.log10(Math.max(v, sc.lo)) - Math.log10(sc.lo))
                          / (Math.log10(sc.hi) - Math.log10(sc.lo)) * (h - T - B);
  const slot = (w - L - R) / rows.length;

  g.font = '10px ui-monospace, Menlo, monospace';
  g.textBaseline = 'middle'; g.textAlign = 'right';
  for (let e = Math.floor(Math.log10(sc.lo)); e <= Math.ceil(Math.log10(sc.hi)); e++) {
    const v = Math.pow(10, e);
    if (v < sc.lo || v > sc.hi) continue;
    g.strokeStyle = '#141a22';
    g.beginPath(); g.moveTo(L, Y(v)); g.lineTo(w - R, Y(v)); g.stroke();
    g.fillStyle = '#4d5867'; g.fillText(String(v), L - 8, Y(v));
  }

  // THE TWO REFERENCE LINES ARE THE POINT OF THE FIGURE. One is what a thesis stated as a goal;
  // the other is what four low-cost receivers actually delivered. A band whose bar sits BELOW a
  // line needs better than that line provides.
  // THE LABELS SIT AT THE RIGHT EDGE, not the left. On the left they landed on top of the decade
  // labels and the axis title, and the two most important annotations on the figure were the least
  // legible thing on it.
  const rule = (v, colour, label) => {
    if (!(v > 0) || v < sc.lo || v > sc.hi) return;
    g.strokeStyle = colour; g.lineWidth = 1.25; g.setLineDash([5, 4]);
    g.beginPath(); g.moveTo(L, Y(v)); g.lineTo(w - R, Y(v)); g.stroke();
    g.setLineDash([]);
    g.font = '10px ui-monospace, Menlo, monospace';
    const tw = g.measureText(label).width;
    // A pill behind it, so the text stays readable where a bar passes under the line.
    g.fillStyle = 'rgba(14,18,24,.85)';
    g.fillRect(w - R - tw - 8, Y(v) - 12, tw + 8, 12);
    g.fillStyle = colour; g.textAlign = 'right'; g.textBaseline = 'bottom';
    g.fillText(label, w - R - 4, Y(v) - 2);
  };

  rows.forEach((b, i) => {
    const x0 = L + slot * i + slot * 0.18;
    const bw = Math.max(6, slot * 0.64);
    if (b.unlimited) {
      // A colour-matched ensemble cancels the water exactly, so there is no bar to draw. Saying
      // so is the honest answer; a very tall bar would be a rounding artefact pretending to be a
      // measurement.
      g.fillStyle = 'rgba(94,207,255,.5)';
      g.textAlign = 'center'; g.textBaseline = 'middle';
      g.fillText('no limit', x0 + bw / 2, (T + h - B) / 2);
    } else {
      const tight = b.requiredSigmaMm < d.specMm;
      g.fillStyle = tight ? 'rgba(255,123,114,.8)' : 'rgba(126,231,135,.7)';
      g.fillRect(x0, Y(b.requiredSigmaMm), bw, h - B - Y(b.requiredSigmaMm));
    }
    g.save();
    g.translate(x0 + slot * 0.32, h - B + 8);
    g.textAlign = 'right'; g.textBaseline = 'middle';
    g.rotate(-Math.PI / 4);
    g.fillStyle = '#7d8a9c'; g.fillText(b.band, 0, 0);
    g.restore();
  });

  rule(d.specMm, 'rgba(255,180,84,.9)', `${d.specMm} mm, the stated goal`);
  rule(d.achievedMm, 'rgba(185,138,255,.9)', `${d.achievedMm} mm, achieved by low-cost GNSS`);

  g.textAlign = 'left'; g.textBaseline = 'top';
  g.fillStyle = '#3d4757';
  g.fillText('σ_PWV required, mm. Lower is harder.', 4, 2);
}

function drawLcLoss() {
  const d = state.lcLoss;
  if (!d) return;
  const temps = d.teffK || [];
  const rows = d.curve || [];
  if (rows.length < 2 || !temps.length) return;

  $('lcLossNote').textContent =
    `${d.band} · ${fmt.num(d.fromNm, 0)}-${fmt.num(d.toNm, 0)} nm · airmass ${fmt.num(d.airmass, 2)}`;

  // A colour per temperature, blue for hot and red for cool: the same direction as the sky, so
  // nobody has to consult a key to know which line is the M dwarf.
  const colourFor = (t) => {
    const f = Math.max(0, Math.min(1, (t - 2000) / 3800));
    return `hsl(${Math.round(8 + f * 200)}, 78%, ${Math.round(58 + f * 8)}%)`;
  };

  const plot = (cvId, key, label) => {
    const cv = $(cvId);
    if (!cv || !cv.clientWidth) return;
    const series = temps.map((_, k) => rows.map((r) => (r[key] || [])[k]).map((v) => (v === null ? NaN : v)));
    const finite = series.flat().filter((v) => isFinite(v));
    if (!finite.length) return;
    const { g, w, h } = setupCanvas(cv);
    const xs = rows.map((r) => r.pwvMm);
    const [xlo, xhi] = extent(xs);
    const [ylo, yhi] = extent(finite);
    const { X, Y } = axes(g, w, h, xlo, xhi, ylo, yhi, 'PWV mm', label);
    temps.forEach((t, k) => {
      g.strokeStyle = colourFor(t); g.lineWidth = 1.6;
      g.beginPath();
      let started = false;
      rows.forEach((r, i) => {
        const v = series[k][i];
        if (!isFinite(v)) return;
        if (!started) { g.moveTo(X(xs[i]), Y(v)); started = true; } else g.lineTo(X(xs[i]), Y(v));
      });
      g.stroke();
      const last = series[k].map((v, i) => [v, i]).filter(([v]) => isFinite(v)).pop();
      if (last) {
        g.fillStyle = colourFor(t);
        g.font = '10px ui-monospace, Menlo, monospace';
        g.textAlign = 'right'; g.textBaseline = 'middle';
        g.fillText(`${t} K`, X(xs[last[1]]) - 4, Y(last[0]) - 7);
      }
    });
  };

  plot('lcLossCurve', 'loss', 'mmag absorbed');
  plot('lcDiffCurve', 'differential', 'mmag differential');

  $('lcLossCaption').textContent =
    `Against ${fmt.num(d.compTeffK, 0)} K comparisons, referenced to the table's driest column `
    + `(${d.referencePwvMm} mm) rather than to vacuum.`;
}

function renderLcColourGrid(grid, d) {
  if (!grid || !grid.sigmaMm) { $('lcColourPanel').hidden = true; return; }
  $('lcColourPanel').hidden = false;
  $('lcColourNote').textContent = `${grid.band} · budget ${fmt.num(d.budgetPpm, 0)} ppm`
    + (grid.chosenAutomatically ? ' · the widest differential of the bands given' : '');

  // Which band the matrix is for, as a control rather than a caption: it is a property of ONE
  // band, and a reader comparing it against the table above has to be able to move it.
  const picker = $('lcColourPick');
  if (picker) {
    const rows = (d.bands || []).filter((b) => !b.refusal);
    picker.innerHTML = rows.map((b) =>
      `<option value="${lcEsc(b.band)}"${b.band === grid.band ? ' selected' : ''}>${lcEsc(b.band)}</option>`).join('');
  }

  $('lcColourHead').innerHTML = '<th>target ＼ comps</th>'
    + grid.compTeffK.map((c) => `<th>${fmt.num(c, 0)} K</th>`).join('');

  $('lcColourRows').innerHTML = grid.targetTeffK.map((t, i) =>
    `<tr><td>${fmt.num(t, 0)} K</td>`
    + grid.compTeffK.map((c, j) => {
        const v = grid.sigmaMm[i][j];
        if (v === null) return `<td class="cell-none">no limit</td>`;
        const cls = v < d.specMm ? 'cell-tight' : v > d.achievedMm ? 'cell-loose' : '';
        return `<td class="${cls}">${fmt.num(v, 3)}</td>`;
      }).join('')
    + `</tr>`).join('');

  const flat = grid.sigmaMm.flat().filter((v) => v !== null && v > 0);
  const span = flat.length ? Math.max(...flat) / Math.min(...flat) : 0;
  $('lcColourCaption').textContent =
    `Required σ_PWV in mm. Across this grid it moves by a factor of ${fmt.num(span, 0)}, and choosing `
    + `the ensemble costs nothing. Every other way of relaxing it is hardware.`;
}

/** Figure 7: the band itself: filter, water, and the product the integral actually sees. */
async function drawLcBand() {
  const scope = selectedScope();
  const cv = $('lcBandCurve');
  if (!scope || !cv) return;
  const mine = modeReceipt();

  const chosen = lcChosenBand();

  const q = new URLSearchParams({
    telescope: scope.name, filter: chosen.name, points: '400',
    pwv: String(parseFloat($('lcPwv').value) || 2.5),
    airmass: String(parseFloat($('lcX').value) || 1.5),
    teffK: String(parseFloat($('lcTargetK').value) || 2600),
  });
  if (chosen.fromNm) q.set('fromNm', String(chosen.fromNm));
  if (chosen.toNm) q.set('toNm', String(chosen.toNm));

  let d;
  try {
    const r = await fetch('/api/pwv/transmission?' + q);
    d = await r.json();
    if (!ofThisMode(mine)) return;
    if (!r.ok) {
      $('lcBandPanel').hidden = false;
      $('lcBandCaption').textContent = d.error || 'That band could not be integrated.';
      return;
    }
  } catch { return; }

  $('lcBandPanel').hidden = false;
  $('lcBandNote').textContent =
    `${d.telescopeDisplay} · ${d.filter} · ${fmt.num(d.fromNm, 0)}-${fmt.num(d.toNm, 0)} nm`;

  const { g, w, h } = setupCanvas(cv);
  const rows = d.curve || [];
  if (rows.length < 2) return;
  const xs = rows.map((p) => p.nm);
  const { X, Y } = axes(g, w, h, xs[0], xs[xs.length - 1], 0, 1, 'nm', 'transmission');

  const line = (key, colour, width, alpha) => {
    g.beginPath(); g.strokeStyle = colour; g.lineWidth = width; g.globalAlpha = alpha;
    rows.forEach((p, i) => (i ? g.lineTo(X(p.nm), Y(p[key])) : g.moveTo(X(p.nm), Y(p[key]))));
    g.stroke(); g.globalAlpha = 1;
  };
  line('filter', '#7d8a9c', 1, 0.75);
  line('water', '#5ecfff', 1, 0.9);
  line('product', '#ccd6e4', 1.6, 1);

  $('lcBandCaption').textContent =
    `Water at ${d.pwvMm} mm, airmass ${fmt.num(d.airmass, 2)}. `
    + (d.measuredFilterCurve
        ? `The band is this instrument's own measured curve. `
        : `The band is a top-hat: no measured curve was supplied, so its edges are rectangles. `)
    + `${d.pwvMm} mm transmits ${(100 * d.meanTransmission).toFixed(3)} % of the band on average, `
    + `${fmt.num(d.lossMmagFlat, 2)} mmag for a flat spectrum.`
    + (d.spanNote ? ' ' + d.spanNote : '');
}

// ---------------------------------------------------------------------- the transfer function

$('lcTransfer').onclick = async () => {
  const mine = modeReceipt();
  const scope = selectedScope();
  if (!scope) return;
  const btn = $('lcTransfer');
  btn.disabled = true; btn.textContent = 'Sweeping…';
  lcFail('lcTransferError', '');

  const chosen = lcChosenBand();

  try {
    const r = await fetch('/api/pwv/transit-bias', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        telescope: scope.name, band: chosen,
        targetTeffK: parseFloat($('lcTargetK').value),
        compTeffK: parseFloat($('lcCompK').value),
        depthPpm: parseFloat($('lcTbDepth').value),
        durationHours: parseFloat($('lcTbDur').value),
        baselineHours: parseFloat($('lcTbBase').value),
        cadenceSeconds: parseFloat($('lcTbCad').value),
        pwvMm: parseFloat($('lcPwv').value),
        amplitudeMm: parseFloat($('lcTbAmp').value),
        baseline: $('lcTbModel').value,
      }),
    });
    const d = await r.json();
    if (!ofThisMode(mine)) return;
    if (!r.ok) { lcFail('lcTransferError', d.error || 'The sweep was refused.'); return; }
    state.lcTransfer = d;
    state.lcTransferredWith = lcScopeStamp(scope);
    markLcTransferStale(false);
    renderLcTransfer();
    renderLcClosure();
  } catch (e) {
    lcFail('lcTransferError', String(e));
  } finally {
    btn.disabled = false; btn.textContent = 'Measure the transfer function';
  }
};

function renderLcTransfer() {
  const d = state.lcTransfer;
  if (!d) return;
  $('lcTransferPanel').hidden = false;
  $('lcTransferNote').textContent =
    `${d.band} · ${fmt.num(d.injectedDepthPpm, 0)} ppm over ${fmt.num(d.durationHours, 2)} h `
    + `in a ${fmt.num(d.windowHours, 2)} h window`;

  $('lcTransferVerdict').hidden = false;
  $('lcTfWorst').textContent = `${fmt.num(d.worstPeriodHours, 2)} h`;
  $('lcTfPeak').textContent = `${fmt.num(d.worstPpmPerMm, 0)} ppm/mm`;
  $('lcTfSlow').textContent = d.slowestRmsPpm === null ? 'n/a' : `${fmt.num(d.slowestRmsPpm, 1)} ppm`;

  $('lcTfBaselineRows').innerHTML = (d.constantColumn || []).map((c) =>
    `<tr>`
    + `<td>${lcEsc(c.baseline)}</td>`
    + `<td>${fmt.num(c.depthPpm, 1)}</td>`
    + `<td class="${Math.abs(c.biasPpm) > 100 ? 'tag below' : ''}">${fmt.num(c.biasPpm, 1)}</td>`
    + `<td>${c.correlatedWith ? `${fmt.num(c.profileCorrelation, 3)} with ${lcEsc(c.correlatedWith)}` : 'n/a'}</td>`
    + `</tr>`).join('');

  const timeOnly = (d.constantColumn || []).find((c) => c.baseline === 'Time');
  const withX = (d.constantColumn || []).find((c) => c.baseline === 'TimeAirmass');
  $('lcTransferCaption').textContent =
    `Bias on the fitted depth against the timescale the column moves on, over every phase. `
    + (timeOnly && withX
        ? `At a constant column a time-only baseline still leaves ${fmt.num(timeOnly.biasPpm, 0)} ppm; `
          + `an airmass regressor brings it to ${fmt.num(withX.biasPpm, 0)} ppm.`
        : '');

  $('lcTransferNotes').innerHTML = (d.notes || []).map((n) => `<li>${lcEsc(n)}</li>`).join('');
  drawLcTransfer(d);
}

function drawLcTransfer(d) {
  const cv = $('lcTransferChart');
  const rows = (d.sweep || []).filter((s) => s.ppmPerMm !== null && s.periodHours > 0);
  if (!cv || !cv.clientWidth || rows.length < 2) return;
  const { g, w, h } = setupCanvas(cv);

  const L = 78, R = 16, T = 16, B = 44;
  const px = rows.map((s) => Math.log10(s.periodHours));
  const xlo = Math.min(...px), xhi = Math.max(...px);
  const vals = rows.map((s) => s.ppmPerMm).filter((v) => v > 0);
  const sc = logScale(vals);
  if (!sc) return;

  const X = (lp) => L + (lp - xlo) / (xhi - xlo || 1) * (w - L - R);
  const Y = (v) => h - B - (Math.log10(Math.max(v, sc.lo)) - Math.log10(sc.lo))
                          / (Math.log10(sc.hi) - Math.log10(sc.lo)) * (h - T - B);

  g.font = '10px ui-monospace, Menlo, monospace';
  g.textBaseline = 'middle'; g.textAlign = 'right';
  for (let e = Math.floor(Math.log10(sc.lo)); e <= Math.ceil(Math.log10(sc.hi)); e++) {
    const v = Math.pow(10, e);
    if (v < sc.lo || v > sc.hi) continue;
    g.strokeStyle = '#141a22';
    g.beginPath(); g.moveTo(L, Y(v)); g.lineTo(w - R, Y(v)); g.stroke();
    g.fillStyle = '#4d5867'; g.fillText(v >= 1000 ? (v / 1000) + 'k' : String(v), L - 8, Y(v));
  }
  g.textAlign = 'center'; g.textBaseline = 'top';
  for (const p of [0.25, 1, 3, 12, 24, 72]) {
    const lp = Math.log10(p);
    if (lp < xlo || lp > xhi) continue;
    g.strokeStyle = '#131920';
    g.beginPath(); g.moveTo(X(lp), T); g.lineTo(X(lp), h - B); g.stroke();
    g.fillStyle = '#4d5867'; g.fillText(p < 1 ? `${p * 60}m` : `${p}h`, X(lp), h - B + 7);
  }

  // The transit's own duration and the window, marked: the peak sits near one of them and the
  // reader should be able to see which without being told.
  const mark = (hours, colour, label) => {
    const lp = Math.log10(hours);
    if (!(hours > 0) || lp < xlo || lp > xhi) return;
    g.strokeStyle = colour; g.setLineDash([4, 4]); g.lineWidth = 1;
    g.beginPath(); g.moveTo(X(lp), T); g.lineTo(X(lp), h - B); g.stroke();
    g.setLineDash([]);
    g.save();
    g.translate(X(lp) + 4, T + 4);
    g.textAlign = 'left'; g.textBaseline = 'top';
    g.fillStyle = colour; g.fillText(label, 0, 0);
    g.restore();
  };
  mark(d.durationHours, 'rgba(185,138,255,.7)', 'the event');
  mark(d.windowHours, 'rgba(255,180,84,.7)', 'the window');

  g.strokeStyle = 'rgba(94,207,255,.95)'; g.lineWidth = 1.8;
  g.beginPath();
  rows.forEach((s, i) => (i ? g.lineTo(X(px[i]), Y(s.ppmPerMm)) : g.moveTo(X(px[i]), Y(s.ppmPerMm))));
  g.stroke();
  g.fillStyle = 'rgba(94,207,255,.9)';
  rows.forEach((s, i) => { g.beginPath(); g.arc(X(px[i]), Y(s.ppmPerMm), 2.4, 0, Math.PI * 2); g.fill(); });

  g.textAlign = 'left'; g.textBaseline = 'top';
  g.fillStyle = '#3d4757';
  g.fillText('ppm of depth per mm of PWV excursion', L, 2);
  g.textAlign = 'right';
  g.fillText('timescale the column varies on', w - R, h - B + 22);
}

// ---------------------------------------------------------------------- step 5: fit the depth

$('lcDepthFit').onclick = () => fitLcDepth();

async function fitLcDepth() {
  const mine = modeReceipt();
  const seq = state.sequence;
  if (!seq) return;
  const btn = $('lcDepthFit');
  btn.disabled = true; btn.textContent = 'Fitting…';
  try {
    const q = new URLSearchParams({
      baseline: $('lcDepthModel').value,
      correct: $('lcDepthCorrect').checked ? 'true' : 'false',
    });
    const r = await fetch(`/api/sequences/${seq.id}/depth?${q}`);
    const d = await r.json();
    if (!ofThisMode(mine)) return;
    $('lcDepthPanel').hidden = false;
    if (!r.ok) {
      state.lcDepth = null;
      $('lcDepthVerdict').hidden = true;
      $('lcDepthCaption').textContent = d.error || 'The depth could not be fitted.';
      $('lcDepthNumbers').innerHTML = '';
      $('lcDepthNotes').innerHTML = '';
      // THE EXPORT ROW GOES WITH THE FIT. The panel is shown so the refusal can be read, and the
      // export row inside it used to come up with it: two download links pointing at "#" after a
      // first refusal, or at the previous run's files after a later one.
      lcExportLinks(null);
      renderLcChain();
      return;
    }
    state.lcDepth = d;
    renderLcDepth();
    renderLcClosure();
    renderLcChain();
  } finally {
    btn.disabled = false; btn.textContent = 'Fit';
  }
}

/** The export row: shown and live for the run a fit came back for, hidden and inert otherwise. */
function lcExportLinks(seq) {
  $('lcExportBox').hidden = !seq;
  for (const [id, tail] of [['lcExportCsv', '/export.csv'], ['lcExportJson', '']]) {
    const a = $(id);
    if (seq) { a.href = `/api/sequences/${seq.id}${tail}`; a.removeAttribute('aria-disabled'); }
    else { a.href = '#'; a.setAttribute('aria-disabled', 'true'); }
  }
  $('lcExportCsv').textContent = 'CSV of the series';
}

function renderLcDepth() {
  const d = state.lcDepth;
  if (!d) return;
  $('lcDepthPanel').hidden = false;
  $('lcDepthNote').textContent =
    `${d.points} epochs · ${d.inTransit} in transit, ${d.outOfTransit} out · baseline ${d.baseline}`
    + (d.waterCorrected ? ' · water corrected' : '');

  $('lcDepthVerdict').hidden = false;
  $('lcDepthVal').textContent = `${fmt.num(d.depthPpm, 0)} ppm`;
  $('lcDepthErr').textContent = `± ${fmt.num(d.depthErrorPpm, 0)} ppm formal`;
  $('lcDepthTruth').textContent = `${fmt.num(d.injectedPpm, 0)} ppm`;
  $('lcDepthBias').textContent = `${d.biasPpm > 0 ? '+' : ''}${fmt.num(d.biasPpm, 0)} ppm`;
  const nSigma = d.depthErrorPpm > 0 ? Math.abs(d.biasPpm) / d.depthErrorPpm : NaN;
  $('lcDepthSig').textContent = isFinite(nSigma)
    ? (nSigma < 1
        ? `${fmt.num(nSigma, 1)}σ, consistent with no bias`
        : `${fmt.num(nSigma, 1)}σ from zero`)
    : 'recovered minus injected';

  $('lcDepthNumbers').innerHTML = [
    ['depth over its own error', `${fmt.num(d.significanceSigma, 1)} σ`],
    ['residual scatter about the model', `${fmt.num(d.residualPpm, 0)} ppm`],
    ['target B−V', fmt.num(d.targetBv, 3)],
    ['ensemble B−V', fmt.num(d.ensembleBv, 3)],
    ['profile against the baseline', d.correlatedWith
      ? `${fmt.num(d.profileCorrelation, 3)} with ${lcEsc(d.correlatedWith)}` : 'no baseline regressor'],
    ...(d.coefficients || []).map((c) => [`coefficient · ${lcEsc(c.name)}`,
      `${fmt.num(c.value, 6)} ± ${fmt.num(c.error, 6)}`]),
  ].map(([k, v]) => `<dt>${k}</dt><dd class="mono">${v}</dd>`).join('');

  $('lcDepthNotes').innerHTML = (d.notes || []).map((n) =>
    `<li class="${n.startsWith('UNRELIABLE') ? 'warn' : ''}">${lcEsc(n)}</li>`).join('');

  $('lcDepthCaption').textContent =
    `Points are the measured ratio, dashed is the fitted baseline, solid is the baseline with the `
    + `transit. The shaded band is where the event was injected.`;

  lcExportLinks(state.sequence);
  // ONE CURVE, NOT TWO. The fitted model is drawn over the light curve in its own panel rather
  // than into a second canvas here, so the points a reader is looking at and the model over them
  // are the same picture.
  if (state.sequence) drawLcCurve(state.sequence);
}

// ---------------------------------------------------------------------- two conditions

function refreshLcRunPickers() {
  const runs = state.lcRuns || [];
  for (const id of ['lcPairA', 'lcPairB']) {
    const sel = $(id);
    if (!sel) continue;
    const was = sel.value;
    sel.innerHTML = runs.map((r) =>
      `<option value="${r.id}">${lcEsc(r.label)}</option>`).join('');
    if (was && runs.some((r) => r.id === was)) sel.value = was;
  }
  $('lcPairPanel').hidden = runs.length < 2;
  if (runs.length >= 2 && $('lcPairA').value === $('lcPairB').value) {
    $('lcPairB').value = runs[runs.length - 1].id;
    if ($('lcPairA').value === $('lcPairB').value) $('lcPairA').value = runs[0].id;
  }
}

/** Remember a finished run so two conditions can be subtracted later. */
function rememberLcRun(seq) {
  if (!seq || seq.state !== 'finished') return;
  state.lcRuns = (state.lcRuns || []).filter((r) => r.id !== seq.id);
  state.lcRuns.push({
    id: seq.id,
    label: `${seq.id} · ${seq.pwv ? seq.pwv.description : 'no water'} · seed ${seq.seed}`,
  });
  // The server holds eight sequences; holding more here would offer runs it has already dropped.
  state.lcRuns = state.lcRuns.slice(-8);
  refreshLcRunPickers();
}

$('lcPairRun').onclick = async () => {
  const mine = modeReceipt();
  const a = $('lcPairA').value, b = $('lcPairB').value;
  lcFail('lcPairError', '');
  if (!a || !b || a === b) {
    lcFail('lcPairError', 'Pick two different runs. A difference needs two conditions.');
    return;
  }
  const btn = $('lcPairRun');
  btn.disabled = true; btn.textContent = 'Comparing…';
  try {
    const q = new URLSearchParams({
      a, b, baseline: $('lcDepthModel').value,
      correct: $('lcDepthCorrect').checked ? 'true' : 'false',
    });
    const r = await fetch('/api/sequences/compare?' + q);
    const d = await r.json();
    if (!ofThisMode(mine)) return;
    if (!r.ok) { lcFail('lcPairError', d.error || 'The comparison failed.'); $('lcPairVerdict').hidden = true; return; }

    $('lcPairVerdict').hidden = false;
    $('lcPairNote').textContent = `baseline ${d.baseline}${d.waterCorrected ? ' · water corrected' : ''}`;
    $('lcPairDiff').textContent = `${d.differencePpm > 0 ? '+' : ''}${fmt.num(d.differencePpm, 0)} ppm`;
    $('lcPairErr').textContent = `± ${fmt.num(d.differenceErrorPpm, 0)} ppm`;
    $('lcPairSig').textContent = `${fmt.num(d.significanceSigma, 1)} σ`;
    $('lcPairVerdictSub').textContent = d.significanceSigma >= 3
      ? 'the conditions are distinguished'
      : 'not distinguished by these two runs';
    $('lcPairNotes').innerHTML = [d.verdict, ...(d.warnings || [])]
      .filter(Boolean).map((n) => `<li class="${n.startsWith('A shared base seed') ? 'warn' : ''}">${lcEsc(n)}</li>`).join('');
  } catch (e) {
    lcFail('lcPairError', String(e));
  } finally {
    btn.disabled = false; btn.textContent = 'Compare';
  }
};

// ---------------------------------------------------------------------- the closure
//
// THE ONLY PLACE EITHER HALF CAN BE CHECKED AGAINST ANYTHING. The analytic half predicts a bias in
// ppm of depth per mm of column, from the passband integral and a synthetic light curve. The
// measured half recovers a depth out of real frames with real photon noise. Neither has anything to
// be compared with on its own; together, on the same field and the same stars, they do.

function renderLcClosure() {
  const tf = state.lcTransfer, fit = state.lcDepth, seq = state.sequence;
  if (!tf || !fit || !seq) { $('lcClosurePanel').hidden = true; return; }
  if (!seq.pwv) { $('lcClosurePanel').hidden = true; return; }

  $('lcClosurePanel').hidden = false;
  $('lcClosureNote').textContent = `${tf.band} · run ${seq.id}`;

  // The excursion the RUN actually had, as the server reported it, times the transfer function at
  // the timescale that run spans. Both numbers come from the server; the multiplication is the
  // only arithmetic here and it is the definition of a transfer function.
  const swing = (seq.pwv.maxMm !== null && seq.pwv.minMm !== null)
    ? (seq.pwv.maxMm - seq.pwv.minMm) / 2 : null;
  const predicted = swing !== null ? tf.worstPpmPerMm * swing : null;
  const measured = Math.abs(fit.biasPpm);

  $('lcClosureVerdict').hidden = false;
  $('lcClosurePred').textContent = predicted === null ? 'n/a' : `${fmt.num(predicted, 0)} ppm`;
  $('lcClosurePredSub').textContent = predicted === null
    ? 'this run reports no water range'
    : `${fmt.num(tf.worstPpmPerMm, 0)} ppm/mm × ${fmt.num(swing, 2)} mm of swing, at the worst timescale`;
  $('lcClosureMeas').textContent = `${fmt.num(measured, 0)} ppm`;
  $('lcClosureMeasSub').textContent = `|recovered − injected|, ± ${fmt.num(fit.depthErrorPpm, 0)} ppm`;

  const ratio = predicted > 0 ? measured / predicted : null;
  $('lcClosureRatio').textContent = ratio === null ? 'n/a' : fmt.num(ratio, 2);
  $('lcClosureSub').textContent = ratio === null ? '' : 'measured over predicted';

  const notes = [];
  notes.push('The predicted figure is an UPPER BOUND, and deliberately so: it uses the transfer '
    + 'function at its worst timescale, which is the column turning over about once inside the '
    + 'visit. A real column that drifts monotonically across the night lands far below it.');
  notes.push('The measured figure carries this run\'s whole photon error, '
    + `± ${fmt.num(fit.depthErrorPpm, 0)} ppm. If that exceeds the predicted bias, the run cannot `
    + 'test the prediction: it can only fail to contradict it. Compare two runs, or raise the water swing.');
  if (fit.depthErrorPpm > (predicted || 0)) {
    notes.push('UNRELIABLE: the error bar on the recovered depth exceeds the predicted bias here, so '
      + 'the agreement below is not evidence either way.');
  }
  $('lcClosureNotes').innerHTML = notes.map((n) =>
    `<li class="${n.startsWith('UNRELIABLE') ? 'warn' : ''}">${lcEsc(n)}</li>`).join('');
}

// ======================================================================================
// DEFINE AN INSTRUMENT  (the form the API has always been waiting for)
//
// `POST /api/instruments/custom` has accepted a whole instrument since it was written, and
// `grep -c "instruments/custom" web/app.js` returned 0: nothing in the browser called it. So the
// first step of the water experiment - describe the instrument you actually want to know about -
// was reproducible from a shell and not from the site, which is the one rule this project does
// not bend.
//
// THE FORM DOES NOT INVENT ANYTHING, and neither does the server behind it. A quantity that is not
// given is derived by a stated relation, or declared unmodelled, or refused with the reason. The
// three lists come back with the instrument and are shown here, because a frame from an instrument
// with an unmodelled optical train is not the same evidence as one from a characterised telescope,
// and the page that defined it is where that has to be said.
// ======================================================================================

/** A CSV of `wavelength_nm,value`, parsed to the shape the endpoint takes. Never evaluated here. */
function parseCurveCsv(text) {
  const points = [];
  const bad = [];
  for (const raw of String(text).split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('#') || /^[a-z_ ]*wave/i.test(line)) continue;
    const parts = line.split(/[,;\t ]+/).filter(Boolean);
    if (parts.length < 2) { bad.push(line); continue; }
    const nm = Number(parts[0]), v = Number(parts[1]);
    if (!isFinite(nm) || !isFinite(v)) { bad.push(line); continue; }
    points.push({ wavelengthNm: nm, value: v });
  }
  return { points, bad };
}

/** The band list from the textarea: `name, centre_nm, width_A, peak`. */
function parseInstrumentBands() {
  const out = [], bad = [];
  for (const raw of ($('ciBands').value || '').split('\n')) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    const p = line.split(',').map((t) => t.trim());
    if (p.length < 3) { bad.push(line); continue; }
    const centre = Number(p[1]), width = Number(p[2]);
    if (!p[0] || !isFinite(centre) || !isFinite(width) || centre <= 0 || width <= 0) { bad.push(line); continue; }
    const band = { label: p[0], centralWavelengthNm: centre, bandwidthAngstrom: width };
    if (p.length > 3 && p[3] !== '') {
      const peak = Number(p[3]);
      if (isFinite(peak)) band.peakTransmission = peak;
      else bad.push(line);
    }
    const curve = state.lcCurves[p[0]];
    if (curve && curve.length) band.transmissionCurve = curve;
    out.push(band);
  }
  return { bands: out, bad };
}

function refreshCurveBandPicker() {
  const sel = $('ciCurveBand');
  if (!sel) return;
  const { bands } = parseInstrumentBands();
  const was = sel.value;
  sel.innerHTML = bands.map((b) => `<option value="${lcEsc(b.label)}">${lcEsc(b.label)}</option>`).join('')
                || '<option value="">add a band first</option>';
  if (was && bands.some((b) => b.label === was)) sel.value = was;

  const attached = Object.entries(state.lcCurves);
  $('ciCurveList').innerHTML = attached.length
    ? attached.map(([name, pts]) =>
        `<li>${lcEsc(name)}: ${pts.length} measured points. The curve is integrated directly and the `
        + `published peak is not applied on top of it.</li>`).join('')
    : '';
}

$('ciBands').addEventListener('input', refreshCurveBandPicker);

$('ciCurveFile').addEventListener('change', async () => {
  const file = $('ciCurveFile').files[0];
  const band = $('ciCurveBand').value;
  $('ciCurveFile').value = '';
  if (!file) return;
  if (!band) { lcFail('ciError', 'Name a band first, then attach its curve to it.'); return; }
  const { points, bad } = parseCurveCsv(await file.text());
  if (points.length < 2) {
    lcFail('ciError', `${file.name} gave ${points.length} usable point(s). A curve needs at least two `
                    + `lines of "wavelength_nm,value"; one point is a flat value, which the peak field `
                    + `already expresses.`);
    return;
  }
  lcFail('ciError', bad.length ? `${bad.length} line(s) of ${file.name} were not read as numbers.` : '');
  state.lcCurves[band] = points;
  refreshCurveBandPicker();
});

$('ciQeFile').addEventListener('change', async () => {
  const file = $('ciQeFile').files[0];
  if (!file) return;
  const { points } = parseCurveCsv(await file.text());
  if (points.length < 2) {
    lcFail('ciError', `${file.name} gave ${points.length} usable point(s) for the QE curve.`);
    $('ciQeFile').value = '';
    return;
  }
  state.lcQeCurve = points;
  $('ciQeHint').textContent = `${file.name}: ${points.length} points, evaluated per wavelength inside `
                            + `the passband integral rather than as one number.`;
});

$('ciClear').onclick = () => {
  for (const id of ['ciName', 'ciCamera', 'ciAperture', 'ciFocal', 'ciObstruction', 'ciOptics',
                    'ciWidth', 'ciHeight', 'ciPixel', 'ciQe', 'ciWell', 'ciRead', 'ciDark',
                    'ciTemp', 'ciBits', 'ciEpa', 'ciSeeing', 'ciBands']) $(id).value = '';
  state.lcCurves = {};
  state.lcQeCurve = null;
  $('ciQeHint').textContent = 'CSV of wavelength_nm,value.';
  lcFail('ciError', '');
  $('ciNotes').innerHTML = '';
  refreshCurveBandPicker();
};

$('ciSubmit').onclick = async () => {
  const mine = modeReceipt();
  lcFail('ciError', '');
  const num = (id) => {
    const v = $(id).value.trim();
    return v === '' ? undefined : Number(v);
  };

  const { bands, bad } = parseInstrumentBands();
  if (bad.length) {
    lcFail('ciError', `Could not read ${bad.length} band line(s): ${bad.slice(0, 2).map(lcEsc).join(' | ')}. `
                    + `Each is "name, centre_nm, width_A" with an optional peak transmission.`);
    return;
  }

  const body = {
    name: $('ciName').value.trim(),
    cameraName: $('ciCamera').value.trim() || undefined,
    apertureMeters: num('ciAperture'),
    focalLengthMeters: num('ciFocal'),
    secondaryObstructionFraction: num('ciObstruction'),
    opticsTransmission: num('ciOptics'),
    sensorWidthPx: num('ciWidth'),
    sensorHeightPx: num('ciHeight'),
    pixelSizeMicrons: num('ciPixel'),
    quantumEfficiency: num('ciQe'),
    quantumEfficiencyCurve: state.lcQeCurve || undefined,
    fullWellElectrons: num('ciWell'),
    readNoiseElectrons: num('ciRead'),
    darkCurrentElectronsPerSecond: num('ciDark'),
    detectorTemperatureCelsius: num('ciTemp'),
    adcBits: num('ciBits'),
    electronsPerAduAtUnityGain: num('ciEpa'),
    zenithSeeingFwhmArcsec: num('ciSeeing'),
    siteId: $('ciSite').value,
    filters: bands.length ? bands : undefined,
  };

  const btn = $('ciSubmit');
  btn.disabled = true; btn.textContent = 'Building…';
  try {
    const r = await fetch('/api/instruments/custom', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    });
    const d = await r.json();
    if (!ofThisMode(mine)) return;
    if (!r.ok) {
      // THE SERVER'S REFUSAL, SHOWN AS IT CAME. Every one of them names the quantity and says why
      // a frame would be meaningless without it; paraphrasing them here would lose the reason,
      // and swallowing them would be worse than not having the form.
      lcFail('ciError', d.error || 'The instrument was refused.');
      return;
    }

    // The instrument is now pointable, so the list has to be rebuilt before it can be selected.
    state.telescopes = await (await fetch('/api/telescopes')).json();
    const inst = $('instrument');
    const offered = state.mode === 'lc'
      ? state.telescopes.filter((t) => !t.isSpaceBased) : state.telescopes;
    inst.innerHTML = offered.map((t) => `<option value="visual:${t.name}">${t.displayName}</option>`).join('');
    inst.value = `visual:${d.name}`;
    onInstrumentChange();

    $('ciNotes').innerHTML =
      `<li>Built as <b>${lcEsc(d.name)}</b> and selected. It survives a restart: the definition is `
      + `written beside the catalogue and rebuilt through this same path when the server starts, so `
      + `an instrument that would be refused today is refused today rather than living on.</li>`
      + (d.bands || []).map((b) =>
          `<li>${lcEsc(b.name)}: ${fmt.num(b.centralWavelengthNm, 0)} nm, `
          + `${fmt.num(b.bandwidthAngstrom, 0)} Å, `
          + (b.measuredCurve ? `<b>measured curve, ${b.curvePoints} points</b>` : 'top-hat') + `.</li>`).join('')
      + (d.derived || []).map((n) => `<li>${lcEsc(n)}</li>`).join('')
      + (d.assumptions || []).map((n) => `<li class="warn">${lcEsc(n)}</li>`).join('');
    renderLcChain();
  } catch (e) {
    lcFail('ciError', String(e));
  } finally {
    btn.disabled = false; btn.textContent = 'Define it';
  }
};

// The colour matrix is a property of ONE band; moving it is a deliberate act, so it re-derives.
$('lcColourPick').addEventListener('change', () => {
  state.lcGridBand = $('lcColourPick').value;
  // Everything in the analytic half is about one band, so moving it re-derives all of it rather
  // than leaving three panels describing the band before and one the band after.
  state.lcTransfer = null;
  $('lcTransferPanel').hidden = true;
  $('lcPredict').click();
});

// ---------------------------------------------------------------------- the run, made visible
//
// TWO THINGS THIS PANEL COULD NOT DO, AND HAD TO.
//
// A run whose frames were mostly refused produced no analysis, and with no analysis there was no
// curve, no table and no picture: a summary line and nothing else. The frames themselves are still
// there, with their conditions and their reasons, and they are what a reader needs when a run goes
// wrong. They are drawn now whether or not the analysis succeeded.
//
// And the summary GUESSED. It said "the field was down or the sky was not dark", which it had no
// evidence for: every refused frame carries its own reason and the summary threw them away. The
// commonest reason in practice was neither of those two, it was a transit aimed at a position with
// no star on it, and the reader was told something false about their own run.

state.lcAxis = 'time';

for (const chip of document.querySelectorAll('#lcCurveAxis .chip')) {
  chip.onclick = () => {
    state.lcAxis = chip.dataset.axis;
    for (const c of document.querySelectorAll('#lcCurveAxis .chip')) c.classList.toggle('on', c === chip);
    renderLcRun();
  };
}

/** The frames and the curve, for whatever the run produced. */
function renderLcRun() {
  const s = state.sequence;
  if (state.mode !== 'lc' || !s) return;
  renderLcFrames(s);
  drawLcCurve(s);
}

function renderLcFrames(s) {
  const frames = s.frames || [];
  $('lcFramesPanel').hidden = frames.length === 0;
  if (!frames.length) return;

  const refused = frames.filter((f) => f.error);
  const measured = frames.length - refused.length;
  $('lcFramesNote').textContent =
    `${fmt.int(measured)} taken, ${fmt.int(refused.length)} refused, of ${fmt.int(s.total)}`;

  // THE REASONS AS THE SERVER GAVE THEM, grouped and counted. Never a guess.
  $('lcFrameReasons').innerHTML = groupRefusals(frames)
    .map(({ reason, count }) => `<li class="warn">${fmt.int(count)} frame(s): ${lcEsc(reason)}</li>`).join('')
    + (s.ladderNote ? `<li>${lcEsc(s.ladderNote)}</li>` : '')
    + (s.stopReason ? `<li class="warn">${lcEsc(s.stopReason)}</li>` : '');

  const shown = frames.slice(0, 400);
  $('lcFrameRows').innerHTML = shown.map((f) => {
    if (f.error) {
      return `<tr class="refused">`
        + `<td>${f.index}</td><td>${lcEsc(f.observedUtc)}</td>`
        + `<td colspan="6" class="reason">${lcEsc(f.error)}</td>`
        + `<td><span class="tag below">refused</span></td></tr>`;
    }
    const inTransit = f.transitFactor !== null && f.transitFactor < 1;
    return `<tr>`
      + `<td>${f.index}</td>`
      + `<td>${lcEsc(f.observedUtc)}</td>`
      + `<td>${fmt.num(f.airmass, 3)}</td>`
      + `<td>${f.pwvMm === null || f.pwvMm === undefined ? 'n/a' : fmt.num(f.pwvMm, 2)}</td>`
      + `<td>${inTransit ? fmt.num((1 - f.transitFactor) * 1e6, 0) + ' ppm' : 'out'}</td>`
      + `<td>${fmt.num(f.seeingArcsec, 2)}</td>`
      + `<td>${fmt.num(f.fwhmPx, 2)}</td>`
      + `<td>${fmt.int(f.stars)}</td>`
      + `<td>${f.reliable === false ? '<span class="tag alias">unreliable</span>'
                                    : '<span class="tag detected">measured</span>'}</td>`
      + `</tr>`;
  }).join('');

  $('lcFramesCaption').textContent = frames.length > shown.length
    ? `First ${shown.length} of ${fmt.int(frames.length)} rows.`
    : '';

  if (s.previewUrl) {
    $('lcFramePreview').hidden = false;
    if ($('lcFramePreview').getAttribute('src') !== s.previewUrl) $('lcFramePreview').src = s.previewUrl;
  }
}

/**
 * The light curve itself, drawn from the run's own series.
 *
 * Independent of the depth fit on purpose: a run can produce a perfectly good curve and still have
 * no depth to recover, and a reader who cannot see the curve cannot tell which of the two happened.
 * The fitted model is drawn over it when there is one.
 */
function drawLcCurve(s) {
  const a = s.analysis;
  const cv = $('lcCurve');
  $('lcCurvePanel').hidden = false;

  const rows = (a && a.series ? a.series : []).filter((p) => p.ratio !== null && isFinite(p.ratio));
  if (rows.length < 3) {
    $('lcCurveNote').textContent = s.state === 'running' ? `${s.done} of ${s.total}` : 'no series';
    lcFail('lcCurveError',
      (a && a.notes && a.notes.length)
        ? a.notes.join(' ')
        : 'This run produced no differential series, so there is no curve. The frame table below '
          + 'gives each frame and the reason it was refused.');
    $('lcCurveCaption').textContent = '';
    if (cv && cv.clientWidth) setupCanvas(cv);
    return;
  }
  lcFail('lcCurveError', '');

  $('lcCurveNote').textContent =
    `${rows.length} epochs · ${s.filter} · ${fmt.num(s.exposureSeconds, 0)} s · binning ${s.binning}`;

  if (!cv || !cv.clientWidth) return;
  const { g, w, h } = setupCanvas(cv);

  const t0 = rows[0].ut;
  const axis = state.lcAxis || 'time';
  const xOf = (p) => axis === 'airmass' ? p.airmass
                   : axis === 'pwv' ? p.pwvMm
                   : (p.ut - t0) / 3600;
  const label = axis === 'airmass' ? 'airmass' : axis === 'pwv' ? 'PWV mm' : 'hours from the first frame';

  const usable = rows.filter((p) => xOf(p) !== null && isFinite(xOf(p)));
  if (usable.length < 3) {
    $('lcCurveCaption').textContent = axis === 'pwv'
      ? 'This run carried no water-vapour term, so there is no column to plot against.'
      : 'Not enough epochs carry that quantity.';
    return;
  }

  const xs = usable.map(xOf);
  const ys = usable.map((p) => p.ratio);
  const [xlo, xhi] = extent(xs);
  const [ylo, yhi] = extent(ys);
  const { X, Y } = axes(g, w, h, xlo, xhi, ylo, yhi, label, 'target / ensemble');

  // Where the injected event is, shaded, so the depth being measured is visible rather than stated.
  if (axis === 'time') {
    g.fillStyle = 'rgba(185,138,255,.08)';
    let runStart = null;
    usable.forEach((p, i) => {
      const inn = p.transitFactor !== null && p.transitFactor < 1;
      if (inn && runStart === null) runStart = i;
      if ((!inn || i === usable.length - 1) && runStart !== null) {
        const x0 = X(xs[runStart]), x1 = X(xs[Math.max(runStart, inn ? i : i - 1)]);
        g.fillRect(x0, PAD.t, Math.max(2, x1 - x0), h - PAD.t - PAD.b);
        runStart = null;
      }
    });
  }

  // The photon prediction as an error bar per point, where the run supplied one.
  g.strokeStyle = 'rgba(94,207,255,.28)'; g.lineWidth = 1;
  usable.forEach((p, i) => {
    if (!(p.photonPpt > 0)) return;
    const e = p.photonPpt / 1000;
    g.beginPath(); g.moveTo(X(xs[i]), Y(p.ratio - e)); g.lineTo(X(xs[i]), Y(p.ratio + e)); g.stroke();
  });

  // The fitted model, when a depth has been fitted on this run.
  const fit = state.lcDepth;
  if (fit && fit.sequence === s.id && axis === 'time' && (fit.curve || []).length) {
    const draw = (key, colour, width, dash) => {
      g.strokeStyle = colour; g.lineWidth = width; g.setLineDash(dash || []);
      g.beginPath();
      let started = false;
      fit.curve.forEach((c) => {
        const v = c[key];
        if (v === null || !isFinite(v)) return;
        const x = (c.ut - t0) / 3600;
        if (!started) { g.moveTo(X(x), Y(v)); started = true; } else g.lineTo(X(x), Y(v));
      });
      g.stroke(); g.setLineDash([]);
    };
    draw('baseline', 'rgba(255,180,84,.85)', 1.4, [5, 4]);
    draw('model', 'rgba(126,231,135,.95)', 1.6);
  }

  g.fillStyle = 'rgba(94,207,255,.85)';
  usable.forEach((p, i) => { g.beginPath(); g.arc(X(xs[i]), Y(p.ratio), 2.2, 0, Math.PI * 2); g.fill(); });

  const bits = [];
  if (a.detrendedPpt) bits.push(`scatter ${fmt.num(a.detrendedPpt, 2)} ppt after the airmass drift is removed`);
  if (a.photonPpt) bits.push(`photon limit ${fmt.num(a.photonPpt, 2)} ppt`);
  if (s.transient) bits.push(`injected ${fmt.num(s.transient.depthPpt, 2)} ppt`);
  $('lcCurveCaption').textContent = bits.join(' · ')
    + (fit && fit.sequence === s.id && axis === 'time' ? '. Dashed is the fitted baseline, solid the baseline with the transit.' : '.');
}

/**
 * Redraw every light-curve canvas from the answers already in hand.
 *
 * A canvas here sizes its backing store from its own clientWidth, so anything that changes the
 * column's width leaves the previous drawing stretched over the new one. Nothing is re-fetched:
 * these all draw from state, so this is cheap enough to run on every resize tick.
 */
function redrawLcCanvases() {
  if (state.mode !== 'lc') return;
  if (state.lcReq) renderLcPrediction();
  if (state.lcTransfer) drawLcTransfer(state.lcTransfer);
  if (state.sequence) drawLcCurve(state.sequence);
}

// A ResizeObserver on the run column sat here for a while. It went, for two reasons: showing or
// hiding a panel does NOT change the column's width (the grid column is 1fr either way, so there
// was no case for it to catch), and it delivered no callbacks at all in the embedded browser this
// was checked in. The window's own resize event does fire and is enough.

/**
 * Refusals grouped by what actually distinguishes them.
 *
 * A refusal often carries the offending VALUE, at full precision, inside its own sentence: "the
 * water-vapour table covers 0.5 to 20 mm and was asked for 22.179499325733897 mm". Grouping on the
 * sentence therefore made one group per frame, and a run whose column left the table printed
 * sixteen near-identical lines that said one thing. The numbers are folded out to form the key and
 * folded back in as a range, so the reader gets the sentence once with the span it covered.
 */
function groupRefusals(frames) {
  const groups = new Map();
  for (const f of (frames || [])) {
    if (!f.error) continue;
    const sentence = f.error.split(/(?<=\.)\s/)[0];
    const key = sentence.replace(/-?\d+(?:\.\d+)?/g, '#');
    const values = (sentence.match(/-?\d+\.\d{3,}/g) || []).map(Number);
    const g = groups.get(key) || { reason: sentence, count: 0, values: [] };
    g.count++;
    g.values.push(...values);
    groups.set(key, g);
  }
  return [...groups.values()]
    .map((g) => {
      if (g.values.length < 2) return { reason: g.reason, count: g.count };
      // One sentence, with the values it varied over rather than one arbitrary instance of them.
      const lo = Math.min(...g.values), hi = Math.max(...g.values);
      const reason = g.reason.replace(/-?\d+\.\d{3,}/,
        `${fmt.num(lo, 2)} to ${fmt.num(hi, 2)}`).replace(/\s*-?\d+\.\d{3,}/g, '');
      return { reason, count: g.count };
    })
    .sort((a, b) => b.count - a.count);
}
