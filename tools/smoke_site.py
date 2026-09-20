#!/usr/bin/env python3
"""
Does the SITE work? Verify cannot answer that, and this is why it exists.

`cd Verify && dotnet run` calls the engine's classes directly. It never issues an HTTP request and
never loads a page, so a green run says nothing about whether the server serves or the interface
reaches it. Two breakages shipped past a fully green Verify on 2026-08-27:

  * `/api/forecast` returned 500 whenever it was called with ra+dec and no instrument, because a
    null reached a field access. Every page load hits that endpoint. Verify never does.
  * A photometric sequence could not find its own instrument, because it stored the display name
    "PlaneWave RC20" where every lookup matches the key "RC20". Both names are strings and both
    compile.

Neither is a physics fault, and no amount of physics checking would have found either. What finds
them is asking the server the questions the browser asks, and checking the answers are shaped the
way the browser expects. That is all this does, and it does it in about a minute.

    python3 tools/smoke_site.py --port 5227

Exit 0 means every check passed. Exit 1 names what did not.
"""

import argparse
import json
import math
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

PASS, FAIL = "  ok  ", "  FAIL"
failures = []
checks = 0


def check(what, ok, detail=""):
    global checks
    checks += 1
    if not ok:
        failures.append(what)
    print(f"{PASS if ok else FAIL}  {what}" + (f"   [{detail}]" if detail else ""))
    return ok


def section(title):
    print(f"\n{title}\n" + "-" * len(title))


def get(port, path, timeout=600):
    url = f"http://127.0.0.1:{port}{path}"
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            body = r.read()
            ctype = r.headers.get("content-type", "")
            return r.status, (json.loads(body) if "json" in ctype else body), None
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw), None
        except Exception:
            return e.code, raw, None
    except Exception as e:
        return None, None, str(e)


def delete(port, path, timeout=300):
    url = f"http://127.0.0.1:{port}{path}"
    req = urllib.request.Request(url, method="DELETE")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, json.loads(r.read()), None
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw), None
        except Exception:
            return e.code, raw, None
    except Exception as e:
        return None, None, str(e)


def post(port, path, payload, timeout=900, raw=False):
    # raw=True hands back the UNPARSED body as the third element, so a check can grep the wire for
    # the quoted strings "NaN" and "Infinity" - which json.loads would happily turn into strings and
    # hide. The whole point of that check is to look at the bytes a browser would see.
    url = f"http://127.0.0.1:{port}{path}"
    data = json.dumps(payload).encode()
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            body = r.read()
            return r.status, json.loads(body), (body.decode("utf8", "replace") if raw else None)
    except urllib.error.HTTPError as e:
        body = e.read()
        try:
            return e.code, json.loads(body), (body.decode("utf8", "replace") if raw else None)
        except Exception:
            return e.code, body, None
    except Exception as e:
        return None, None, str(e)


def post_raw(port, path, payload, timeout=900):
    """A POST whose body is BYTES, not JSON: the FITS bundle is a ZIP. Returns (status, bytes, content-type)."""
    url = f"http://127.0.0.1:{port}{path}"
    req = urllib.request.Request(url, data=json.dumps(payload).encode(), headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read(), r.headers.get("Content-Type", "")
    except urllib.error.HTTPError as e:
        return e.code, e.read(), e.headers.get("Content-Type", "")
    except Exception as e:
        return None, str(e).encode(), ""


def has(d, *keys):
    """Every key present and not None. The browser reads these by name; a rename is a break."""
    return isinstance(d, dict) and all(k in d and d[k] is not None for k in keys)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5227)
    ap.add_argument("--sequence", action="store_true",
                    help="also run a short photometric sequence end to end (a few minutes)")
    a = ap.parse_args()
    p = a.port

    print(f"Smoke test against http://127.0.0.1:{p}\n"
          f"Asking the server the questions the browser asks, and checking the answers are shaped\n"
          f"the way the browser reads them.")

    # ---------------------------------------------------------------- the page itself
    section("1. The page and its static assets")
    for path, needle in (("/", b"<html"), ("/app.js", b"const $ ="), ("/style.css", b":root")):
        st, body, err = get(p, path)
        check(f"GET {path} serves", st == 200 and err is None and needle in (body or b""),
              err or f"HTTP {st}")

    # Every id the JS reaches for must exist in the markup. A renamed id is a silent no-op in
    # JavaScript - $('typo') is null and the handler throws only when a user presses the button.
    st, html, _ = get(p, "/")
    st2, js, _ = get(p, "/app.js")
    if isinstance(html, bytes) and isinstance(js, bytes):
        html_s, js_s = html.decode("utf-8", "replace"), js.decode("utf-8", "replace")
        import re
        wanted = sorted(set(re.findall(r"\$\('([A-Za-z0-9_]+)'\)", js_s)))
        # Some ids are created by the script itself, in an innerHTML template rather than in the
        # markup. Those are not missing, so they are subtracted rather than reported - the check
        # is for an id NOBODY creates, which is the one that makes $('typo') silently null.
        made_here = set(re.findall(r'id="([A-Za-z0-9_]+)"', js_s))
        missing = [w for w in wanted if f'id="{w}"' not in html_s and w not in made_here]
        check(f"every id the script reaches for is created somewhere ({len(wanted)} ids)",
              not missing, ", ".join(missing[:6]) if missing else "")

        # THE WEB PROBE'S NINE. Each of these is a text assertion on the page as served, because the
        # smoke suite never runs JavaScript; the browser check for the same items is in the fixer's
        # report, not here. A pin that fails means the mechanism came back, not just the wording.
        # W1: the light-curve "Exposure s" control that nothing read is gone, and the request's
        # exposure is the sequence block's own.
        check("W1 no #lcExp control in the markup (the sequence's seqExp is the exposure)",
              'id="lcExp"' not in html_s and 'id="seqExp"' in html_s)
        check("W1 nothing in app.js reads an lcExp control",
              "$('lcExp')" not in js_s)
        # W2: a reopened run's record does not carry the depth uncertainty, and the page must not
        # invent one. The literal "depthUncertaintyPpm: 0" was the fabricated error bar.
        check("W2 rsOpenRun does not fabricate a depth uncertainty",
              "depthUncertaintyPpm: 0" not in js_s
              and "depthUncertaintyPpm: typeof d.result.DepthUncertaintyPpm === 'number'" in js_s)
        check("W2 rsOpenRun reads the could-not-check register lines from the log, else null",
              "unavailable: couldNotCheck.length ? couldNotCheck : null" in js_s
              and "uncertainty not recorded with this run" in js_s)
        # W3: a run whose record is gone is reported, not swallowed as an unhandled rejection.
        check("W3 rsOpenRun handles a non-OK answer before calling .json()",
              "d = r.ok ? await r.json() : null;" in js_s
              and "is no longer on disk" in js_s)
        # W4: an instrument change marks every light-curve panel measured through another one.
        check("W4 onInstrumentChange re-judges the light-curve panels against the new instrument",
              "if (lc) refreshLcInstrumentStaleness(scope);" in js_s
              and "function refreshLcInstrumentStaleness(scope)" in js_s
              and 'id="lcStaleNote"' in html_s and 'id="lcStarsStale"' in html_s
              and 'id="lcTransferStale"' in html_s)
        # W5: the page names a filter the search left out, and prefers the server's own list.
        check("W5 pointingSearch names the filters that were not understood",
              "NOT understood and left out" in js_s
              and "function pointingFiltersNotUnderstood(q, served)" in js_s
              and "if (Array.isArray(served)) return served;" in js_s)
        # W6: a null magnitude from /api/sky is skipped, not drawn as a magnitude 0 star.
        check("W6 the sky chart skips a star whose magnitude is null",
              "if (!Number.isFinite(v)) continue;" in js_s)
        # W7: the six requirement-table headers styled as sortable now have the handler.
        check("W7 #lcReqTable headers are bound to a sort handler",
              "document.querySelectorAll('#lcReqTable th[data-sort]')" in js_s
              and "function lcSortedReqRows(rows)" in js_s
              and html_s.count('<th data-sort="requiredSigmaMm">') == 1)
        # W8: the export row is hidden and its links inert until a fit has come back.
        check("W8 the export row starts hidden with inert links and one function owns both states",
              '<div id="lcExportBox" hidden>' in html_s
              and 'id="lcExportCsv" href="#" download aria-disabled="true"' in html_s
              and "function lcExportLinks(seq)" in js_s
              and "lcExportLinks(state.sequence);" in js_s
              and js_s.count("lcExportLinks(null);") >= 2)
        # W9: the water curve is sized like every other chart, from data-h and the element's
        # width, not a fixed 640 by 180 bitmap stretched by the page's canvas rule.
        check("W9 #pwvCurve carries data-h and no fixed width/height attributes",
              '<canvas id="pwvCurve" data-h="180"></canvas>' in html_s)
        check("W9 paintPwv draws through setupCanvas and is redrawn on resize",
              "const { g: ctx, w: W, h: H } = setupCanvas(cv);" in js_s
              and "redrawPwvCurve();" in js_s)
        # The one the browser found while checking the nine: the token was declared with `let`
        # below the top-level pwvModeChanged() call that reaches it, so the first draw on every
        # load threw in the temporal dead zone and the water panel came up empty.
        check("pwvCurveToken and pwvLastPlot are declared above the top-level pwvModeChanged() call",
              0 < js_s.find("let pwvCurveToken = 0;") < js_s.find("\npwvModeChanged();")
              and 0 < js_s.find("let pwvLastPlot = null;") < js_s.find("\npwvModeChanged();"))

    # ---------------------------------------------------------------- what the page loads
    section("2. The calls the page makes on load")
    st, boot, err = get(p, "/api/bootstrap")
    check("GET /api/bootstrap", st == 200, err or f"HTTP {st}")
    st, scopes, err = get(p, "/api/telescopes")
    ok = check("GET /api/telescopes", st == 200 and isinstance(scopes, list) and scopes, err or f"HTTP {st}")
    ground = None
    if ok:
        ground = next((t for t in scopes if not t.get("isSpaceBased")), None)
        check("at least one ground astrograph is offered", ground is not None)
        check("each telescope carries the name every lookup matches on",
              all("name" in t for t in scopes))

    for path in ("/api/capture/data", "/api/sky", "/api/gaia"):
        st, d, err = get(p, path)
        check(f"GET {path}", st == 200, err or f"HTTP {st}")

    st, d, err = get(p, "/api/pointing-search?q=M+51&site=ohp&limit=10")
    check("GET /api/pointing-search", st == 200, err or f"HTTP {st}")

    # W5 (server half): the filters the search left out come back by name. This passes only once
    # the PointingSearchService/Program.cs snippet in handoff_web_round1.md is applied.
    st, d, err = get(p, "/api/pointing-search?q=typ:nebula&site=ohp&limit=1")
    check("GET /api/pointing-search forwards Core's Unrecognised list",
          st == 200 and isinstance(d, dict) and d.get("unrecognised") == ["typ:nebula"],
          err or f"HTTP {st}: unrecognised={d.get('unrecognised') if isinstance(d, dict) else d!r}")
    st, d, err = get(p, "/api/pointing-search?q=type:nebula&site=ohp&limit=1")
    check("GET /api/pointing-search with an understood filter reports nothing unrecognised",
          st == 200 and isinstance(d, dict) and d.get("unrecognised") == [],
          err or f"HTTP {st}")

    # W6 (server half): /api/sky writes null, never the string "NaN", for a star with no magnitude,
    # and the page skips those rows. Two label rows carry null today.
    st, d, err = get(p, "/api/sky")
    check("GET /api/sky carries no quoted NaN in any magnitude",
          st == 200 and isinstance(d, dict)
          and all(s[2] is None or isinstance(s[2], (int, float)) for s in d.get("stars", []))
          and all(l.get("v") is None or isinstance(l.get("v"), (int, float)) for l in d.get("labels", []))
          and all(h.get("v") is None or isinstance(h.get("v"), (int, float)) for h in d.get("hosts", [])),
          err or f"HTTP {st}")

    # W3 (server half): a run id that is not on disk is a 404 the page can name, not a 500.
    st, d, err = get(p, "/api/research/runs/does-not-exist")
    check("GET /api/research/runs/{gone} is a 404, which rsOpenRun reports as a removed record",
          st == 404, err or f"HTTP {st}")

    # THE ONE THAT SHIPPED BROKEN. The page calls the forecast with ra+dec and no instrument on
    # every load, and that branch had a null dereference that Verify could never see.
    st, d, err = get(p, "/api/forecast?site=ohp&nights=30&cols=96&ra=202.4695&dec=47.1952")
    check("GET /api/forecast with ra+dec and NO instrument", st == 200, err or f"HTTP {st}")
    st, d, err = get(p, "/api/forecast?site=ohp&nights=8&cols=24&target=51+Peg+b&instrument=HARPS")
    check("GET /api/forecast with target+instrument", st in (200, 400), err or f"HTTP {st}")

    # THE AIRMASS OF EVERY CELL, because the water panel plots at it. A transmission drawn at a
    # different airmass than the frame will be exposed through is a plot of a different night, and
    # the page must not compute the airmass itself - Kasten & Young lives in Core.
    st, fc, err = get(p, "/api/forecast?site=orm&nights=30&cols=96&ra=252.5&dec=36.4613")
    if check("the forecast carries an airmass per cell, not just an altitude",
             st == 200 and isinstance((fc or {}).get("airmass"), list)
             and len(fc["airmass"]) == len(fc["altitude"]),
             err or f"HTTP {st}"):
        pairs = [(a, x) for a, x in zip(fc["altitude"], fc["airmass"]) if x is not None]
        check("it is null exactly where the field is below the altitude limit, and a number above",
              all(a > fc["altitudeLimitDeg"] for a, _ in pairs)
              and all(x is None for a, x in zip(fc["altitude"], fc["airmass"])
                      if a <= fc["altitudeLimitDeg"]),
              f"{len(pairs)} observable of {len(fc['airmass'])}")
        # Kasten & Young is monotone in altitude: higher in the sky is always less air.
        ordered = sorted(pairs)
        check("and it falls monotonically as the field rises",
              all(ordered[i][1] >= ordered[i + 1][1] - 1e-9 for i in range(len(ordered) - 1)),
              f"airmass {ordered[-1][1]:.3f} at {ordered[-1][0]:.1f} deg to "
              f"{ordered[0][1]:.3f} at {ordered[0][0]:.1f} deg")
        best = int((fc["bestUt"] - fc["startUt"]) / fc["cellSeconds"])
        check("the slot the server would schedule is one the panel can price the water at",
              fc["airmass"][best] is not None, f"airmass {fc['airmass'][best]}")
        # SIX DECIMALS, NOT FOUR. The panel feeds this number straight back to the transmission
        # endpoint, whose airmass floor is 0.999712; four decimals gives 0.9997, which is BELOW the
        # floor, so the water panel refused itself over every field near the zenith.
        overhead = [x for x in fc["airmass"] if x is not None and x < 1.0]
        check("an airmass near the zenith survives the round trip the panel puts it through",
              not overhead or min(overhead) >= 0.999711,
              f"lowest served {min(overhead):.6f}" if overhead else "none this high")

    # ---------------------------------------------------------------- a frame, end to end
    section("3. A capture, and the reproducibility handle")
    if not ground:
        check("a ground astrograph to capture with", False)
    else:
        # THE EPOCH COMES FROM THE SERVER, not from the clock. Reproducibility is a claim about
        # the same request, and the instant is part of the request - so the pair has to be BOOKED.
        # But a booked instant picked arithmetically ("now plus six hours") lands in daylight
        # whenever the test happens to run in the wrong half of the day, and the server rightly
        # refuses it. A check that passes or fails by the hour is worse than no check, so the first
        # capture is SCHEDULED, its chosen epoch is read back, and the pair is booked at that.
        st, first, err = post(p, "/api/capture",
                              dict(telescope=ground["name"], site="orm", raDeg=252.5, decDeg=36.4613,
                                   filter="Luminance", exposureSeconds=10, binning=8, seed=990000))
        if not check("POST /api/capture, scheduled by the server",
                     st == 200 and has(first, "observedUt"),
                     "" if st == 200 else (err or str(first)[:90])):
            return 1
        booked = time.strftime("%Y-%m-%dT%H:%M:%SZ",
                               time.gmtime(946727935.816 + first["observedUt"]))
        body = dict(telescope=ground["name"], site="orm", raDeg=252.5, decDeg=36.4613,
                    filter="Luminance", exposureSeconds=10, binning=8, seed=990001,
                    atUtc=booked)
        st, cap, err = post(p, "/api/capture", body)
        ok = check("POST /api/capture, booked at that same instant", st == 200 and has(cap, "id", "png"),
                   "" if st == 200 else (err or str(cap)[:90]))
        if ok:
            check("the response carries the seed back, so a run can be repeated",
                  cap.get("seed") == 990001, str(cap.get("seed")))
            check("and the exact epoch as a number, not only minute-resolution text",
                  isinstance(cap.get("observedUt"), (int, float)), str(cap.get("observedUt")))

            st2, cap2, _ = post(p, "/api/capture", body)
            check("the same request and seed reproduce the frame exactly",
                  st2 == 200 and cap2.get("png") == cap.get("png"),
                  "" if st2 == 200 and cap2.get("png") == cap.get("png") else str(cap2)[:70])

            st3, bad, _ = post(p, "/api/capture", dict(body, seed=0))
            check("seed 0 is refused, not silently accepted", st3 == 400 and has(bad, "error"))

            # SATURATION, AND WHICH CEILING CAUSED IT. The engine counted only pixels that filled
            # the WELL and clipped the CONVERTER in silence, so a VLT FORS2 frame with 62 % of its
            # pixels railed at 65535 ADU reported saturatedFraction = 0.0004. Binning is what makes
            # this bite: it multiplies the well by bin*bin and leaves the converter where it was.
            check("a capture reports its saturation, and names which ceiling caused it",
                  isinstance(cap.get("saturatedFraction"), (int, float))
                  and isinstance(cap.get("saturatedByConverterFraction"), (int, float)),
                  f"total={cap.get('saturatedFraction')!r} "
                  f"converter={cap.get('saturatedByConverterFraction')!r}")
            check("and the converter's share cannot exceed the total",
                  (cap.get("saturatedByConverterFraction") or 0.0)
                  <= (cap.get("saturatedFraction") or 0.0) + 1e-12)

            # THE ONE THAT WOULD HAVE CAUGHT IT: a long exposure on the largest aperture in the
            # roster, binned, is the configuration where the converter rails first. It must report
            # saturation - and before the fix it reported four ten-thousandths.
            big = next((t["name"] for t in (scopes or [])
                        if not t.get("isSpaceBased") and " " in t.get("name", "")), None)
            if big:
                # NO atUtc. `body` is booked for a northern field seen from La Palma, and that
                # instant is broad daylight at Paranal - the server refuses it, correctly and with
                # the Sun's altitude in the message. Let the scheduler pick the night instead.
                satreq = {k: v for k, v in body.items() if k != "atUtc"}
                st_s, satcap, _ = post(p, "/api/capture",
                    dict(satreq, telescope=big, site="paranal", filter="Red",
                         exposureSeconds=60, binning=2, seed=990031, raDeg=252.5, decDeg=-25.0))
                if st_s == 200:
                    check("a large aperture binned at a long exposure reports itself saturated",
                          (satcap.get("saturatedFraction") or 0.0) > 0.002,
                          f"{big}: {(satcap.get('saturatedFraction') or 0)*100:.2f} % saturated, "
                          f"{(satcap.get('saturatedByConverterFraction') or 0)*100:.2f} % at the converter")
                else:
                    check(f"a {big} frame can be captured at Paranal", False, str(satcap)[:80])

            st4, phot, err = get(p, f"/api/captures/{cap['id']}/photometry")
            okp = check("GET /api/captures/{id}/photometry", st4 == 200, err or f"HTTP {st4}")
            if okp:
                m = (phot.get("matches") or [None])[0]
                need = ("trueMagnitude", "colourBv", "raDeg", "decDeg",
                        "fluxElectrons", "trueElectrons", "snr")
                absent = [k for k in need if not (m and k in m)]
                check("the per-star rows carry what a light curve needs",
                      m is not None and not absent,
                      ", ".join(absent) if absent else f"{len(phot.get('matches') or [])} stars")

            st5, cal, err = post(p, f"/api/captures/{cap['id']}/calibration",
                                 dict(kind="Bias", count=2, seed=990002))
            okc = check("POST /api/captures/{id}/calibration", st5 == 200,
                            "" if st5 == 200 else (err or str(cal)[:80]))
            if okc:
                # A BIAS NEVER LOOKED THROUGH AN ATMOSPHERE. The master's header is cloned from the
                # light, so it was inheriting that light's PWV, PWVSRC, AIRMASS and SEEING - a dark
                # carrying a water column it never saw, and a reduction reading those cards off a
                # master would be reading the science frame's sky by accident.
                hdr = ""
                stf = 0
                try:
                    with urllib.request.urlopen(
                            f"http://127.0.0.1:{p}/api/captures/{cal['id']}/fits", timeout=600) as r:
                        stf = r.status
                        hdr = r.read(8000).decode("ascii", "replace")
                except Exception:
                    pass
                stray = [c for c in ("PWV ", "PWVSRC", "AIRMASS", "SEEING") if c in hdr]
                check("a master carries no sky it never looked through",
                      stf == 200 and not stray,
                      f"header still has {stray}" if stray else "no PWV/AIRMASS/SEEING cards")
            check("a master reports the seed it was drawn from", cal.get("seed") == 990002,
                      str(cal.get("seed")))

            st6, lim, err = get(p, f"/api/instruments/{urllib.parse.quote(ground['name'])}/limits?site=orm&exposure=60")
            check("GET /api/instruments/{name}/limits", st6 == 200, err or f"HTTP {st6}")

    # ---------------------------------------------------------------- the two noise models
    section("4. The light-curve noise model")
    if ground:
        q = urllib.parse.urlencode(dict(telescope=ground["name"], site="orm", magnitude=13.0,
                                        airmass=1.4, exposure=120, binning=1, colourBv=0.7))
        st, nm, err = get(p, f"/api/noise-model?{q}")
        ok = check("GET /api/noise-model", st == 200 and has(nm, "totalSigma"),
                   "" if st == 200 else (err or str(nm)[:80]))
        if ok:
            check("it reports the terms behind the answer, not only the answer",
                  has(nm.get("budget", {}), "encircledEnergy", "skyElectronsPerPixel",
                      "aperturePixels", "psfFwhmArcsec"))
            ee = nm["budget"]["encircledEnergy"]
            # The Gaussian assumption CcdEquation carries is 0.7226 and its own comment calls it
            # optimistic; the real profile integrates lower. A regression to the assumption would
            # put this back at 0.72 and quietly make every light curve a third too clean.
            check("the aperture correction comes from the real profile, not the Gaussian",
                  0.3 < ee < 0.68, f"{ee:.4f} against the Gaussian's 0.7226")

    # ---------------------------------------------------------------- water vapour
    section("5. Water vapour")
    st, data, _ = get(p, "/api/capture/data")
    installed = any("water-vapour transmission:" in f and "not installed" not in f
                    for f in (data or {}).get("files", []))
    check("the transmission table's state is declared either way",
          any("water-vapour" in f for f in (data or {}).get("files", [])),
          "installed" if installed else "not installed")

    if ground and installed:
        wbody = dict(telescope=ground["name"], site="orm", raDeg=252.5, decDeg=36.4613,
                     filter="Red", exposureSeconds=10, binning=8, seed=990010, atUtc=booked)
        st, dry, _ = post(p, "/api/capture", wbody)
        st2, wet, _ = post(p, "/api/capture", dict(wbody, seed=990011,
                                                   pwv=dict(mode="constant", mm=10.0)))
        ok = check("a frame can be taken with a water column", st2 == 200 and has(wet, "pwvMm"),
                   "" if st2 == 200 else str(wet)[:80])
        if ok:
            check("the frame reports the column it was taken through and which series gave it",
                  abs(wet["pwvMm"] - 10.0) < 1e-9 and wet.get("pwvSeriesId"),
                  f"{wet['pwvMm']} mm, series {wet.get('pwvSeriesId')}")
            check("and a frame with no series reports none, which is not the same as zero",
                  st == 200 and dry.get("pwvMm") is None)

        st3, bad, _ = post(p, "/api/capture", dict(wbody, pwv=dict(mode="constant", mm=50.0)))
        check("a column outside the table is refused with its range, not extrapolated",
              st3 == 400 and has(bad, "error"), (bad or {}).get("error", "")[:70])

        st4, meas, _ = post(p, "/api/capture", dict(
            wbody, seed=990012,
            pwv=dict(mode="measured", series="2026-08-27T22:00:00Z 2.0\n2026-08-28T02:00:00Z 6.0\n")))
        check("a measured record can drive a frame", st4 == 200 and has(meas, "pwvMm"),
              f"{(meas or {}).get('pwvMm')} mm")

        # AN INSTRUMENT WHOSE NAME HAS A SPACE IN IT, which is the whole roster except the three
        # small astrographs. Every tool here reached the API by pasting the name straight into an
        # f-string, and Python's http client REFUSES a request line containing a raw space - so
        # tools/pwv_pair.py crashed on "VLT FORS2" with an InvalidURL before it sent anything, and
        # had therefore never once been run on either Paranal instrument or on Hubble.
        #
        # THE REASON THIS SURVIVED IS THIS FILE. Every check below picks `ground`, the FIRST ground
        # instrument, which is RedCat51 - the one name on the roster with no space in it. The
        # harness was shaped so that the defect could not appear in it. So the fix is not only the
        # quoting; it is asking the question with a name that would have failed.
        spacey = next((t["name"] for t in (scopes or []) if " " in t.get("name", "")), None)
        if spacey:
            st_sp, spr, _ = get(p, f"/api/pwv/transmission?pwv=5&airmass=1.5"
                                   f"&telescope={urllib.parse.quote(spacey)}&filter=Red&points=32")
            check(f"an instrument whose name contains a space can be asked for its water curve",
                  st_sp == 200 and (spr or {}).get("telescope") == spacey,
                  f"{spacey!r} -> HTTP {st_sp}, echoed {(spr or {}).get('telescope')!r}")
        else:
            check("the roster carries an instrument with a space in its name to test quoting with",
                  False, "none found - this check has gone blind")

        # THE CURVE ITSELF, which is what turns the header card into something checkable. The
        # panel plots exactly this, so a break here is a break the observer would see.
        st8, cur, _ = get(p, f"/api/pwv/transmission?pwv=10&airmass=1.5"
                             f"&telescope={urllib.parse.quote(ground['name'])}&filter=Luminance&points=200")
        if check("GET /api/pwv/transmission", st8 == 200 and has(cur or {}, "curve", "fromNm", "toNm"),
                 "" if st8 == 200 else str(cur)[:80]):
            rows = cur["curve"]
            check("it is served over the span the passband is actually integrated over",
                  abs(rows[0]["nm"] - cur["fromNm"]) < 5 and abs(rows[-1]["nm"] - cur["toNm"]) < 5,
                  f"{cur['fromNm']}-{cur['toNm']} nm, {len(rows)} points")
            check("and the product is the water times the filter, not one of them relabelled",
                  all(abs(r["product"] - r["water"] * r["filter"]) < 2e-5 for r in rows))

            # THE ONE THAT WOULD HAVE CAUGHT THE OZONE. ESO's library is the whole molecular
            # atmosphere, and its ozone and oxygen do not move with the water - so if the term
            # still carried them, a site extinction coefficient that was MEASURED would count them
            # twice. Referenced to its driest column they are gone, and the site says so.
            st_r, refc, _ = get(p, f"/api/pwv/transmission?pwv={cur['referencePwvMm']}&airmass=1.5"
                                   f"&telescope={urllib.parse.quote(ground['name'])}&filter=Luminance&points=64")
            check("at the reference column the term is exactly one, so the frame is unchanged",
                  st_r == 200 and refc["meanTransmission"] == 1.0
                  and all(r["water"] == 1.0 for r in refc["curve"]),
                  f"reference {cur['referencePwvMm']} mm")
            # AND NOTHING NON-NUMERIC IN A NUMERIC FIELD. A band narrower than the 0.02 nm grid
            # used to average nothing, and the NaN reached the wire as a JSON *string* - which a
            # plot would have drawn as a break or a zero without ever erroring.
            st_n, fine, _ = get(p, f"/api/pwv/transmission?pwv=20&airmass=1"
                                   f"&telescope={urllib.parse.quote(ground['name'])}&filter=Luminance"
                                   f"&fromNm=759.9&toNm=760.1&points=64")
            check("a span finer than the table's own bins still returns numbers",
                  st_n == 200 and all(isinstance(r[k], (int, float))
                                      for r in fine["curve"] for k in ("water", "library", "product")),
                  "" if st_n == 200 else str(fine)[:70])

            st_o, oxy, _ = get(p, f"/api/pwv/transmission?pwv=20&airmass=1"
                                  f"&telescope={urllib.parse.quote(ground['name'])}&filter=Luminance"
                                  f"&fromNm=758&toNm=763&points=32")
            check("and the 760 nm oxygen band is not in it, though the raw library transmits 0.68 there",
                  st_o == 200 and oxy["meanTransmission"] > 0.99,
                  f"{(oxy or {}).get('meanTransmission')}")
            st9, dryc, _ = get(p, f"/api/pwv/transmission?pwv=1&airmass=1.5"
                                  f"&telescope={urllib.parse.quote(ground['name'])}&filter=Luminance&points=200")
            check("more water transmits less of the band",
                  st9 == 200 and dryc["meanTransmission"] > cur["meanTransmission"],
                  f"{dryc['meanTransmission']:.5f} at 1 mm, {cur['meanTransmission']:.5f} at 10 mm")
        st10, badc, _ = get(p, f"/api/pwv/transmission?pwv=50&airmass=1.5"
                               f"&telescope={urllib.parse.quote(ground['name'])}&filter=Luminance")
        check("and the curve is refused outside the table too, not extrapolated for a plot",
              st10 == 400 and has(badc or {}, "error"))

        # ONE PARSER, and this is the endpoint that makes it one. The panel used to read a pasted
        # record itself and take the LAST token of each line where PwvSeries.Parse takes the
        # second - so the three-column GNSS record the panel's own placeholder advertises had its
        # UNCERTAINTY column plotted while the frame was exposed through its water column.
        gnss = "2026-08-28T03:00:00Z 2.1 0.30\n2026-08-28T05:00:00Z 2.6 0.40\n"
        st_s, ser, _ = post(p, "/api/pwv/series", dict(mode="measured", series=gnss))
        if check("POST /api/pwv/series resolves a record with the code that will drive the frame",
                 st_s == 200 and has(ser or {}, "meanMm", "id", "notes"),
                 "" if st_s == 200 else str(ser)[:80]):
            check("a three-column record reads the water column, not the last one",
                  abs(ser["meanMm"] - 2.35) < 1e-9, f"{ser['meanMm']} mm")
            st_c, cap_g, _ = post(p, "/api/capture",
                                  dict(wbody, seed=990014,
                                       pwv=dict(mode="measured", series=gnss)))
            check("and the frame it drives agrees with it, which is the whole point",
                  st_c == 200 and ser["minMm"] <= cap_g["pwvMm"] <= ser["maxMm"],
                  f"series {ser['minMm']}-{ser['maxMm']} mm, frame {(cap_g or {}).get('pwvMm')} mm")
            st_j, junk, _ = post(p, "/api/pwv/series", dict(
                mode="measured",
                series="2026-08-28T03:00:00Z 2.1\nrubbish\n9.9\n2026-08-28T05:00:00Z 2.6\n"))
            check("a record that half loaded says so instead of looking like one that loaded",
                  st_j == 200 and junk["notes"] and "skipped" in junk["notes"][0],
                  (junk or {}).get("notes", ["none"])[0][:60])

        # A PASSBAND THE TABLE CANNOT COVER IS REFUSED, not silently left dry. VLT FORS2's measured
        # curves run to 1200 nm; with a table stopping at 1100 the term was dropped and the frame
        # still recorded PWV and PWVSRC - a frame lying about its own provenance, bit-identical to
        # the dry one. The table now reaches 1300 nm, so the honest outcome is that the water is
        # APPLIED and the pixels move.
        fors = next((t for t in scopes if "FORS2" in t["name"]), None)
        if fors:
            fbody = dict(telescope=fors["name"], site="paranal", raDeg=300, decDeg=-24.6,
                         filter="Red", exposureSeconds=5, binning=8, seed=880808,
                         atUtc="2026-09-01T04:27:27Z")
            st_d, fdry, _ = post(p, "/api/capture", fbody)
            st_w, fwet, _ = post(p, "/api/capture", dict(fbody, pwv=dict(mode="constant", mm=20)))
            if check("a wide measured passband can carry the water term",
                     st_d == 200 and st_w == 200, (fwet or {}).get("error", "")[:70]):
                check("and a frame that records a water column really went through it",
                      fdry["png"] != fwet["png"] and fwet.get("pwvMm") == 20,
                      "pixels differ" if fdry["png"] != fwet["png"] else "PIXELS IDENTICAL")

        # A WAVELENGTH SPAN OUTSIDE THE TABLE IS REFUSED. The narrow-band fallback that stopped the
        # NaN also, for a while, answered questions about bands the line list was never computed
        # over - returning the edge bin as a plottable number no caller could tell from a
        # measurement. That traded a loud failure for a silent one.
        for lo, hi in ((2000, 2001), (200, 201)):
            st_x, xr, _ = get(p, f"/api/pwv/transmission?pwv=5&airmass=1.5"
                                 f"&telescope={urllib.parse.quote(ground['name'])}&filter=Luminance"
                                 f"&fromNm={lo}&toNm={hi}&points=32")
            check(f"a {lo}-{hi} nm span is refused, not answered with the nearest edge bin",
                  st_x == 400 and has(xr or {}, "error"),
                  (xr or {}).get("error", "")[:60] if st_x == 400 else f"SERVED {xr}")

        # AND ON THE CURVE ENDPOINT, WHERE THE ANSWER IS A NULL RATHER THAN A REFUSAL, the null
        # travels with its reason. Returning a bare null for a temperature the caller SUPPLIED is a
        # silent refusal: the reader asked for a number, got nothing, and was told neither that it
        # was refused nor why. Omitting the temperature stays silent, because nothing was asked.
        tspan = (f"?pwv=5&airmass=1.5&telescope={urllib.parse.quote(ground['name'])}"
                 f"&filter=Luminance&fromNm=750&toNm=1000&points=16")
        for lab, extra, want in (("zero", "&teffK=0", True),
                                 ("negative", "&teffK=-500", True),
                                 ("a B-V outside the relation", "&colourBv=9", True),
                                 ("omitted", "", False)):
            st_t, td, _ = get(p, "/api/pwv/transmission" + tspan + extra)
            note = (td or {}).get("teffNote")
            check(f"an empty per-temperature column says why, with the temperature {lab}",
                  st_t == 200 and (td or {}).get("lossMmagForTeff") is None
                  and (bool(note) == want),
                  f"note={str(note)[:70]!r}")

        # A TEMPERATURE THAT WAS GIVEN AND IS IMPOSSIBLE IS REFUSED, not quietly replaced. TeffFrom
        # folded a bad value into null and every caller wrote `?? 2600.0`, so teffK = 0 came back
        # 200 with a whole table computed at 2600 K. The guard that was meant to catch it,
        # `if (!(target > 0.0))`, sat AFTER the coalesce and could never fire. A control the server
        # ignores is a lie, and this one echoed a different number back while ignoring it.
        one_band = [dict(name="I+z'", fromNm=750, toNm=1000)]
        rq_base = dict(telescope=ground["name"], bands=one_band, airmass=1.5, pwvMm=2.5,
                       stepMm=0.5, budgetPpm=100)
        for lab, extra in (("zero", dict(targetTeffK=0, compTeffK=5800)),
                           ("negative", dict(targetTeffK=-300, compTeffK=5800)),
                           ("a comparison of zero", dict(targetTeffK=2600, compTeffK=0)),
                           ("a B-V outside the relation",
                            dict(targetColourBv=4.0, compTeffK=5800))):
            st_b, bad, _ = post(p, "/api/pwv/requirement", dict(rq_base, **extra))
            check(f"a temperature given as {lab} is refused with the reason",
                  st_b == 400 and has(bad or {}, "error"),
                  (bad or {}).get("error", "")[:80] if st_b == 400
                  else f"SERVED 200 with target={(bad or {}).get('targetTeffK')}")
        # AND OMITTING IT STILL TAKES THE DEFAULT, so the refusal did not break the ordinary path.
        st_ok, okd, _ = post(p, "/api/pwv/requirement", rq_base)
        check("omitting the temperatures still takes the documented defaults",
              st_ok == 200 and abs((okd or {}).get("targetTeffK", 0) - 2600.0) < 1.0,
              f"target={(okd or {}).get('targetTeffK')}")

        # A REQUIREMENT ROW THAT CONTAINS NO DETECTOR HAS TO SAY SO. SpectralCurve.At clamps to
        # the endpoint past a quantum-efficiency curve's range, and a CONSTANT MULTIPLIER CANCELS
        # EXACTLY out of a loss ratio, so a band beyond the curve is a top-hat on the sky with no
        # instrument in it. Served without a flag, J and Hs came back at 201 and 211 umag/mm
        # identically on a deep-depletion curve ending at 1100 nm and on a flat-response roster
        # instrument - not similar, identical - and a caption listed J among the bands that
        # "already fit", as though the number described that detector.
        rq_bands = [dict(name=n, fromNm=a, toNm=b) for n, a, b in
                    (("z'", 850, 1000), ("Y", 970, 1070), ("YJ", 970, 1330),
                     ("J", 1170, 1330), ("Hs", 1500, 1650))]
        st_r, rq, _ = post(p, "/api/pwv/requirement",
                           dict(telescope="smoke-fixedcooler", bands=rq_bands, airmass=1.5,
                                pwvMm=2.5, stepMm=0.5, targetTeffK=2600, compTeffK=5800,
                                budgetPpm=100, achievedMm=0.53, specMm=0.1))
        if st_r == 200:
            byname = {b["band"]: b for b in (rq.get("bands") or [])}
            # This instrument was built with a flat quantum efficiency, so EVERY row is blind.
            check("a flat-response instrument says every requirement row carries no detector",
                  all(byname.get(n, {}).get("detectorNote") for n in ("z'", "J", "Hs")),
                  f"z'={bool(byname.get(chr(122)+chr(39), {}).get('detectorNote'))} "
                  f"J={bool(byname.get('J', {}).get('detectorNote'))}")
            # AND THE TELL that proves the point: two bands past the curve return the same number.
            check("two bands beyond any detector response return the identical differential",
                  abs((byname.get("J", {}).get("differentialUmagPerMm") or 0)
                      - 201.0) < 3.0,
                  f"J = {byname.get('J', {}).get('differentialUmagPerMm')}")
        else:
            check("the requirement table can be asked for out-of-range bands", False, str(rq)[:90])

        # WHICH AIRMASS LADDER THE SWEEP FLEW, and it is not a detail. The class offered only a
        # symmetric parabola with the event at the airmass minimum, which is the shape a straight
        # line in time absorbs worst; PhotometricSequence actually flies a monotonic climb. A
        # -1062 ppm figure from the parabola was published as agreeing with -1100 +/- 653 measured
        # on a rising ladder. On the rising ladder the class predicts +61 ppm, so the two numbers
        # were never comparable. The response now names the ladder it flew.
        biasbody = dict(telescope=ground["name"],
                        band=dict(name="I+z'", fromNm=750.0, toNm=1000.0),
                        targetTeffK=2600, compTeffK=5800, depthPpm=6920,
                        durationHours=1.0, baselineHours=1.0, cadenceSeconds=60,
                        pwvMm=2.5, amplitudeMm=2.0, phases=8,
                        airmassMin=1.02, airmassMax=1.82)
        got = {}
        for geo in ("meridian", "rising"):
            st_g, gd, _ = post(p, "/api/pwv/transit-bias", dict(biasbody, airmassGeometry=geo))
            if st_g != 200:
                check(f"the transit-bias sweep accepts the {geo} ladder", False, str(gd)[:90])
                continue
            check(f"the sweep names the {geo} ladder it flew", gd.get("airmassGeometry") == geo,
                  repr(gd.get("airmassGeometry")))
            row = next((c for c in (gd.get("constantColumn") or [])
                        if c.get("baseline") == "Time"), None)
            got[geo] = row.get("biasPpm") if row else None
        if got.get("meridian") is not None and got.get("rising") is not None:
            check("the two airmass ladders give materially different constant-column biases",
                  abs(got["meridian"]) > 500.0 and abs(got["rising"]) < 300.0,
                  f"meridian {got['meridian']:.0f} ppm against rising {got['rising']:.0f} ppm")

        # MANY FRAMES, ONE DOWNLOAD. The single capture yields one FITS and the store keeps 24, so
        # there was no way to leave with twenty sub-exposures for stacking. The bundle exposes each
        # frame and writes it straight into a ZIP with a manifest; frames never enter the store.
        # Pinned: a real ZIP, one entry per frame plus the manifest, distinct seeds, real FITS bytes.
        import io as _io, zipfile as _zf
        st_z, zbytes, zct = post_raw(p, "/api/captures/bundle", dict(
            count=2, filters=["Luminance"],
            capture=dict(telescope=ground["name"], site="ohp", raDeg=83.82, decDeg=-5.39,
                         objectName="smoke bundle", exposureSeconds=1, binning=8, seed=990061)))
        ok_zip = st_z == 200 and "zip" in (zct or "")
        check("a bundle of frames comes back as a ZIP", ok_zip, f"HTTP {st_z}, {zct}, {len(zbytes)} bytes")
        if ok_zip:
            try:
                z = _zf.ZipFile(_io.BytesIO(zbytes)); names = z.namelist()
                man = json.loads(z.read("manifest.json"))
                fits0 = z.read([n for n in names if n.endswith(".fits")][0])
                seeds = [f["seed"] for f in man["frames"]]
                check("with one FITS per frame and a manifest beside them",
                      len(names) == 3 and "manifest.json" in names and len(man["frames"]) == 2, str(names))
                check("every frame in the bundle drew from its own seed",
                      len(set(seeds)) == len(seeds) and seeds[1] - seeds[0] == 7919, str(seeds))
                check("and each entry is a real FITS, not a renamed PNG",
                      fits0[:9] == b"SIMPLE  =", repr(fits0[:20]))
            except Exception as e:
                check("the bundle can be opened as a ZIP", False, str(e)[:80])
        st_o, over, _ = post(p, "/api/captures/bundle", dict(count=64, filters=["Red", "Green"],
            capture=dict(telescope=ground["name"], site="ohp", raDeg=83.82, decDeg=-5.39,
                         exposureSeconds=1, binning=8, seed=990062)))
        check("a bundle over 64 frames is refused with the arithmetic",
              st_o == 400 and "at most 64" in (over or {}).get("error", ""), (over or {}).get("error", "")[:70])

        # ONE CAMPAIGN AT A HUGE WARP MUST NOT MONOPOLISE THE TICKER. Tick held the lock for a whole
        # slice, and at warp 1e7 a slice advanced hours of simulated time under it: the campaign's
        # own reads hung 90 s and every other campaign waited behind it. The simulated time per tick
        # is now capped, the effective warp is reported, and a read has to come back promptly.
        st_w, camp, _ = post(p, "/api/campaigns", dict(target="51 Peg b", site="ohp",
                                                       instrument="HARPS", warp=1e7))
        if st_w == 200:
            time.sleep(3)
            t_read = time.time()
            st_r, cr, _ = get(p, f"/api/campaigns/{camp['id']}")
            took = time.time() - t_read
            check("a read of a campaign running at warp 1e7 returns promptly",
                  st_r == 200 and took < 3.0, f"{took:.2f} s, state {(cr or {}).get('state')}")
            ew = (cr or {}).get("effectiveWarpRate")
            check("and the campaign reports the warp it actually delivered",
                  isinstance(ew, (int, float)) and 0 < ew <= 1e7 + 1, f"effectiveWarpRate={ew!r}")
            post(p, f"/api/campaigns/{camp['id']}/stop", {})
        else:
            check("a 51 Peg b HARPS campaign can be started", False, str(camp)[:80])

        # THE ROOT OF EVERY "NaN"-AS-A-STRING DEFECT, closed at the serialiser. The app enabled
        # AllowNamedFloatingPointLiterals, so any NaN anywhere left as the quoted string "NaN". It was
        # patched by hand three times and found a fourth time on detectorTemperatureC (SPHERE declares
        # no detector temperature) and targetAltitudeDeg (an orbital instrument has no altitude). A
        # converter now writes null for every non-finite double, so no endpoint can leak one again.
        import re as _re
        _bad = _re.compile(r'"(NaN|-?Infinity)"')
        st_s, sph, raw_s = post(p, "/api/capture", dict(
            telescope="VLT SPHERE", site="paranal", filter="Luminance", raDeg=83.82, decDeg=-30,
            exposureSeconds=1, binning=8, seed=990051), raw=True)
        check("a detector with no declared temperature serialises it as null, never as the string NaN",
              st_s == 200 and not _bad.search(raw_s or "") and (sph or {}).get("detectorTemperatureC") is None,
              f"detectorTemperatureC={(sph or {}).get('detectorTemperatureC')!r}")
        hst = next((t["name"] for t in (scopes or []) if t.get("isSpaceBased")), None)
        if hst:
            st_h, hub, raw_h = post(p, "/api/capture", dict(
                telescope=hst, site="ohp", filter="Luminance", raDeg=83.82, decDeg=-5.39,
                exposureSeconds=1, binning=4, seed=990052), raw=True)
            check("an orbital frame's target altitude is null, never the string NaN",
                  st_h == 200 and not _bad.search(raw_h or ""),
                  f"targetAltitudeDeg={(hub or {}).get('targetAltitudeDeg')!r}")
            if st_h == 200:
                st_l, lg, _ = get(p, f"/api/captures/{hub['id']}/render?stretch=log")
                check("a stretch the renderer does not know is refused, not drawn as asinh",
                      st_l == 400 and "raw, asinh, zscale, extended" in (lg or {}).get("error", ""),
                      (lg or {}).get("error", "")[:60])
                st_n, nn, _ = get(p, f"/api/captures/{hub['id']}/photometry?thresholdSigma=NaN")
                check("a NaN threshold is refused before it reaches the reduction",
                      st_n == 400 and has(nn or {}, "error"), (nn or {}).get("error", "")[:60])

        # AN UNKNOWN SITE IS REFUSED WITH THE LIST, on every route that takes one. ById used to end in
        # `?? Ohp`, so "atlantis" - or a display name pasted back from /api/telescopes - became
        # Haute-Provence and the FITS header asserted a site the caller never named.
        for lab, method, path, body in (
                ("capture", "POST", "/api/capture", dict(telescope=ground["name"], site="atlantis",
                    filter="Luminance", raDeg=83.82, decDeg=-5.39, exposureSeconds=1, binning=8, seed=990053)),
                ("capture by display name", "POST", "/api/capture", dict(telescope=ground["name"],
                    site="La Silla", filter="Luminance", raDeg=0, decDeg=70, exposureSeconds=1, binning=8, seed=990054)),
                ("forecast", "GET", "/api/forecast?ra=83.8&dec=-5.4&site=atlantis", None),
                ("pointing-search", "GET", "/api/pointing-search?q=m&site=atlantis", None),
                ("limits", "GET", f"/api/instruments/{urllib.parse.quote(ground['name'])}/limits?site=atlantis", None)):
            st_x, xr, _ = (post(p, path, body) if method == "POST" else get(p, path))
            check(f"an unknown site is refused on {lab}, with the valid ids",
                  st_x == 400 and "ohp, lasilla, paranal, orm, maunakea" in (xr or {}).get("error", ""),
                  (xr or {}).get("error", "")[:70] if st_x == 400 else f"SERVED {st_x}")
        st_f, fc, _ = get(p, "/api/forecast?ra=83.8&dec=-5.4")
        check("omitting the site on the forecast still takes the default", st_f == 200, f"HTTP {st_f}")

        # A MASTER IS CHECKED, NOT TRUSTED: unknown id, wrong kind and wrong size are each refused
        # with the reason, where before all three returned 200 with a note claiming a calibration
        # that had not happened - and a mismatched bias beside a flat threw a bare 500 out of the
        # flatMean loop, which indexed bias[i] without the length guard pass two already had.
        st_a, la, _ = post(p, "/api/capture", dict(telescope=ground["name"], site="ohp", filter="Luminance",
            raDeg=83.82, decDeg=-5.39, exposureSeconds=2, binning=8, seed=990055))
        st_b, lb, _ = post(p, "/api/capture", dict(telescope=ground["name"], site="ohp", filter="Luminance",
            raDeg=83.82, decDeg=-5.39, exposureSeconds=2, binning=4, seed=990056))
        if st_a == 200 and st_b == 200:
            st_c1, fl, _ = post(p, f"/api/captures/{la['id']}/calibration", dict(kind="Flat", count=3, seed=11))
            st_c2, bi, _ = post(p, f"/api/captures/{lb['id']}/calibration", dict(kind="Bias", count=3, seed=12))
            if st_c1 == 200 and st_c2 == 200:
                st_m, mm, _ = get(p, f"/api/captures/{la['id']}/photometry?bias={bi['id']}&flat={fl['id']}")
                check("a bias master from another binning beside a flat is refused, not a bare 500",
                      st_m == 400 and "Check the binning" in (mm or {}).get("error", ""),
                      (mm or {}).get("error", "")[:70] if mm else f"HTTP {st_m}")
                st_k, kk, _ = get(p, f"/api/captures/{la['id']}/photometry?bias={lb['id']}")
                check("a light frame passed as a bias master is refused by its kind",
                      st_k == 400 and "not a bias master" in (kk or {}).get("error", ""),
                      (kk or {}).get("error", "")[:70])
                st_u, uu, _ = get(p, f"/api/captures/{la['id']}/photometry?bias=nosuchid")
                check("a master id the store never held is refused, not reported as applied",
                      st_u == 400 and "nosuchid" in (uu or {}).get("error", ""),
                      (uu or {}).get("error", "")[:70])
                st_g, gg, _ = get(p, f"/api/captures/{la['id']}/photometry?flat={fl['id']}")
                check("and the right flat alone still calibrates",
                      st_g == 200 and any("Calibrated with flat" in n for n in (gg or {}).get("notes", [])),
                      str((gg or {}).get("notes", [""])[:1])[:70])

        # A DETECTOR WITH NO ADJUSTABLE COOLER KEEPS ITS OWN TEMPERATURE. /api/noise-model clamped
        # the setpoint to the site's ambient range unconditionally, while DetectionLimits.cs and
        # DeepSkyCamera.cs both guard that clamp with HasAdjustableCooler. The consequence was not
        # subtle: a custom instrument declaring -60 C and 0.2 e-/s/px came back at 1118 e-/s/px, a
        # factor of 5589, so a read-noise-limited device read as dark-dominated. The capture path
        # was never wrong; only the endpoint an outside user reaches first.
        st_i, inst = post(p, "/api/instruments/custom", dict(
            name="smoke-fixedcooler", cameraName="smoke", apertureMeters=0.5,
            focalLengthMeters=2.5, sensorWidthPx=512, sensorHeightPx=512,
            pixelSizeMicrons=9.0, quantumEfficiency=0.8, fullWellElectrons=50000.0,
            readNoiseElectrons=3.0, darkCurrentElectronsPerSecond=0.2,
            detectorTemperatureCelsius=-60.0, siteId="paranal",
            filters=[dict(position="Luminance", centralWavelengthNm=650.0,
                          bandwidthAngstrom=2000.0)]))[:2]
        if st_i == 200:
            st_n, nm, _ = get(p, "/api/noise-model?telescope=smoke-fixedcooler&site=paranal"
                                 "&magnitude=15&exposure=100&binning=1&filter=Luminance")
            dark = ((nm or {}).get("budget") or {}).get("darkElectronsPerPixel")
            check("a detector with no adjustable cooler keeps its own dark current",
                  st_n == 200 and dark is not None and abs(dark / 100.0 - 0.2) < 0.02,
                  f"{dark / 100.0:.3f} e-/s/px against the 0.200 declared"
                  if dark is not None else f"HTTP {st_n}")
        else:
            check("a fixed-cooler instrument can be defined", False, str(inst)[:80])

        # THE PWV ACCURACY A PROGRAMME NEEDS, over HTTP. Verify computes this against the library
        # directly and never issues a request; this asserts the wire carries the same physics, on
        # the one path an outside user would actually reach it by.
        #
        # An ETH Zurich thesis (Meier 2026, supervised by P. Pihlmann Pedersen) measures low-cost
        # GNSS PWV at SPECULOOS against the Paranal radiometer, targets 0.1 mm and reaches 0.53 mm.
        # The requirement is not one number for an observatory - it is one per band, and for I+z',
        # which is the band SPECULOOS observes in, 0.1 mm is about three times too loose.
        def _loss(pwv_mm, lo, hi, teff):
            st, d, _ = get(p, f"/api/pwv/transmission?pwv={pwv_mm}&airmass=1.5"
                              f"&telescope={urllib.parse.quote(ground['name'])}&filter=Luminance"
                              f"&fromNm={lo}&toNm={hi}&points=64&teffK={teff}")
            return d.get("lossMmagForTeff") if st == 200 else None

        def _slope_umag_per_mm(lo, hi, target_k, comp_k):
            """d(differential loss)/d(PWV) at a Paranal-like 2.5 mm, central difference."""
            vals = [_loss(pv, lo, hi, tk) for tk in (target_k, comp_k) for pv in (2.25, 2.75)]
            if any(v is None for v in vals):
                return None
            return ((vals[1] - vals[0]) - (vals[3] - vals[2])) / 0.5 * 1000.0

        iz = _slope_umag_per_mm(750, 1000, 2600, 5800)
        rp = _slope_umag_per_mm(550, 700, 2600, 5800)
        # 100 ppm OF FLUX in micromagnitudes. Not the same unit; they differ by 8.6 %.
        budget_umag = -2.5 * math.log10(1.0 - 100e-6) * 1e6
        check("the served passband integral gives the same water slope Verify computes offline",
              iz is not None and abs(iz - 3544.0) < 60.0,
              f"I+z' {iz:.0f} umag/mm against 3544" if iz is not None else "no answer")
        check("and over HTTP too, I+z' needs the water column far tighter than the 0.1 mm goal",
              iz is not None and 0.015 < budget_umag / iz < 0.06,
              f"{budget_umag / iz:.4f} mm" if iz else "no answer")
        check("while r' does not, so the verdict is per band and not per observatory",
              rp is not None and budget_umag / rp > 0.53,
              f"{budget_umag / rp:.2f} mm needed, 0.53 mm achieved" if rp else "no answer")

        # A COLOUR-MATCHED ENSEMBLE CANCELS IT EXACTLY, which is the cheapest fix available and
        # costs no hardware at all. Exactly zero on the wire, not nearly zero.
        flat = _slope_umag_per_mm(750, 1000, 3000, 3000)
        check("a comparison ensemble at the target's own temperature cancels the water exactly",
              flat == 0.0, f"{flat!r} umag/mm")

        # AND NOTHING NON-NUMERIC ANYWHERE. lossMmagFlat is -2500*log10(meanT) and meanT can be
        # exactly 0 at a deep band core, which reached the wire as the JSON string "Infinity" -
        # the same class the narrow-band fix was written to remove, one line away from it.
        st_i, deep, _ = get(p, f"/api/pwv/transmission?pwv=20&airmass=3"
                               f"&telescope={urllib.parse.quote(ground['name'])}&filter=Luminance"
                               f"&fromNm=931.9299&toNm=931.9301&points=32")
        check("an opaque band reports no finite cost instead of the string Infinity",
              st_i == 200 and deep.get("lossMmagFlat") is None and deep.get("opaque") is True,
              f"lossMmagFlat={deep.get('lossMmagFlat')!r} opaque={deep.get('opaque')}")

        # WATER ABOVE THE ATMOSPHERE IS REFUSED, not dropped in silence. It used to be dropped here
        # while the series identifier was still stamped into the frame's FITS header - a
        # water-vapour provenance card on photons that never crossed an atmosphere.
        space = next((t for t in scopes if t.get("isSpaceBased")
                      and "OTA" in t["name"]), None)
        if space:
            sbody = dict(telescope=space["name"], raDeg=90, decDeg=66, filter="Luminance",
                         exposureSeconds=2, binning=8, seed=990015)
            st_sp, sp, _ = post(p, "/api/capture", dict(sbody, pwv=dict(mode="constant", mm=9.5)))
            check("a water series on an orbital instrument is refused, not silently ignored",
                  st_sp == 400 and has(sp or {}, "error"), (sp or {}).get("error", "")[:70])
            st_sp2, sp2, _ = post(p, "/api/capture", sbody)
            check("and the same frame without one is still taken, carrying no water provenance",
                  st_sp2 == 200 and sp2.get("pwvMm") is None and sp2.get("pwvSeriesId") is None,
                  "" if st_sp2 == 200 else str(sp2)[:70])

        # THE ZENITH IS NOT OUT OF RANGE. Kasten and Young returns 0.99971 overhead, so a strict
        # airmass >= 1 refused the best-placed fields at a site with a message that rounded the
        # offending value to "1" and said 1 was outside 1 to 3.
        st_z, zen, _ = post(p, "/api/capture", dict(
            telescope=ground["name"], site="paranal", raDeg=300, decDeg=-24.6,
            filter="Luminance", exposureSeconds=2, binning=8, seed=990016,
            pwv=dict(mode="constant", mm=5.0)))
        check("a field passing overhead is photographed, not refused for being too high",
              st_z == 200 and zen.get("pwvMm") == 5.0,
              f"airmass {round((zen or {}).get('airmass', 0), 5)}" if st_z == 200
              else (zen or {}).get("error", "")[:70])

        # A DRIFT WITH NOWHERE TO DRIFT FROM IS REFUSED. On an unbooked capture the epoch was the
        # moment the request arrived while the frame is exposed at an instant the scheduler picks up
        # to 25 hours later, so the drift charged a whole day's water to one sub-exposure: the same
        # request with and without atUtc came back 2.257 mm apart for the identical instant.
        drifting = dict(mode="analytic", meanMm=4.0, amplitudeMm=0.0, periodHours=6.0,
                        driftMmPerDay=5.0)
        st_dr, dr, _ = post(p, "/api/capture", dict(wbody, seed=990017, atUtc=None, pwv=drifting))
        check("a drifting column with no booked slot is refused, not run from the request's arrival",
              st_dr == 400 and has(dr or {}, "error"), (dr or {}).get("error", "")[:70])
        st_dr2, dr2, _ = post(p, "/api/capture", dict(wbody, seed=990017, pwv=drifting))
        check("and the same drift IS accepted once a slot is booked",
              st_dr2 == 200 and dr2.get("pwvMm") is not None,
              f"{(dr2 or {}).get('pwvMm')} mm" if st_dr2 == 200 else (dr2 or {}).get("error", "")[:60])

        # AN IDENTIFIER THAT CHANGES WHEN NOTHING CHANGED IS NOT ONE. Without a drift the epoch does
        # not touch the values, and it used to be hashed anyway.
        steady = dict(mode="analytic", meanMm=4.0, amplitudeMm=1.5, periodHours=6.0, driftMmPerDay=0)
        ids = []
        for k in range(3):
            st_k, ck, _ = post(p, "/api/capture", dict(wbody, seed=990018, atUtc=None, pwv=steady))
            if st_k == 200:
                ids.append(ck.get("pwvSeriesId"))
        check("three identical unbooked captures name the same series",
              len(ids) == 3 and len(set(ids)) == 1, f"{sorted(set(ids))}")

        # THE PANEL PLOTS THE COLUMN THE FRAME GETS, so the endpoint must hand it that column and
        # say when the instant falls outside a pasted record.
        st_e, ep, _ = post(p, "/api/pwv/series?atUtc=2026-09-01T00:00:00Z", dict(
            mode="measured", series="2020-01-01T00:00:00Z 1.0\n2020-01-01T01:00:00Z 9.0\n"))
        check("the series endpoint reports the column at the instant, not only the mean",
              st_e == 200 and ep.get("mmAtEpoch") == 9.0 and ep.get("meanMm") == 5.0,
              f"mmAtEpoch={(ep or {}).get('mmAtEpoch')} meanMm={(ep or {}).get('meanMm')}")
        check("and says when that instant is outside the record it was given",
              (ep or {}).get("coversEpoch") is False)

        # THE PANEL AND THE FRAME MUST PRICE THE SAME NIGHT. With nothing booked the panel used the
        # forecast's 30-night best cell while the capture ran a 25-hour scan: measured, 1.53 against
        # 2.60 airmass, an 18-64 % error in the quoted loss, captioned "the moment the server will
        # schedule". Only a request can see this - the two searches live in different files.
        st_f, fcs, _ = get(p, f"/api/forecast?ra=83.8&dec=-5.4&site=ohp&nights=30&cols=96")
        if check("the forecast publishes the instant an unbooked capture will actually use",
                 st_f == 200 and (fcs or {}).get("scheduledUtc") and fcs.get("scheduledAirmass"),
                 f"{(fcs or {}).get('scheduledUtc')}"):
            st_u, unb, _ = post(p, "/api/capture", dict(
                telescope="RedCat51", site="ohp", raDeg=83.8, decDeg=-5.4, filter="Luminance",
                exposureSeconds=5, binning=8, seed=990019))
            check("and the frame it takes is exposed at exactly that air column",
                  st_u == 200 and abs(unb.get("airmass", 0) - fcs["scheduledAirmass"]) < 5e-4,
                  f"panel {fcs['scheduledAirmass']} vs frame {round((unb or {}).get('airmass', 0), 6)}")

        # AN INSTANT THAT DOES NOT PARSE IS REFUSED, not discarded. It used to fall through to "no
        # slot booked", moving the frame to a different night without a word.
        st_b, bad_at, _ = post(p, "/api/capture", dict(wbody, seed=990020, atUtc="28/08/2026 20:09"))
        check("a malformed atUtc is refused rather than silently rescheduled",
              st_b == 400 and has(bad_at or {}, "error"), (bad_at or {}).get("error", "")[:60])

        # A SPAN THE INSTRUMENT DOES NOT CARRY SAYS SO.
        st_sn, sn, _ = get(p, f"/api/pwv/transmission?pwv=10&airmass=1.5"
                              f"&telescope={urllib.parse.quote(ground['name'])}&filter=Luminance"
                              f"&fromNm=750&toNm=950&points=8")
        check("pricing water across a band the instrument has not says which band that is",
              st_sn == 200 and sn.get("spanNote"), (sn or {}).get("spanNote", "none")[:60])

        # NOTHING NON-NUMERIC FROM THE SERIES ENDPOINT EITHER.
        st_nf, nf, _ = post(p, "/api/pwv/series",
                            dict(mode="analytic", meanMm=4.0, amplitudeMm=1.0, periodHours=6.0))
        check("the series endpoint returns numbers in its numeric fields",
              st_nf == 200 and all(isinstance(nf.get(k), (int, float))
                                   for k in ("meanMm", "minMm", "maxMm", "mmAtEpoch")),
              str({k: nf.get(k) for k in ("meanMm", "minMm", "maxMm")}) if st_nf == 200 else "")

        # A DRIFTING COLUMN, ASKED FOR TWICE. This is the one Verify cannot see: the series was
        # anchored to the moment the REQUEST arrived, so the same booked night came back with a
        # different column and a different identifier on every submission. The frames are seeded
        # and booked identically, so anything that differs here is the anchor moving.
        drift = dict(mode="analytic", meanMm=4.0, amplitudeMm=1.5, periodHours=6.0,
                     driftMmPerDay=0.8)
        st5, d1, _ = post(p, "/api/capture", dict(wbody, seed=990013, pwv=drift))
        st6, d2, _ = post(p, "/api/capture", dict(wbody, seed=990013, pwv=drift))
        if check("a varying column can drive a frame", st5 == 200 and st6 == 200 and has(d1, "pwvMm"),
                 "" if st5 == 200 else str(d1)[:80]):
            check("the same instant booked twice gives the same column, not the column of the moment",
                  d1["pwvMm"] == d2["pwvMm"] and d1.get("pwvSeriesId") == d2.get("pwvSeriesId"),
                  f"{d1['pwvMm']:.4f} / {d2['pwvMm']:.4f} mm, {d1.get('pwvSeriesId')}")

            # And it must not be the same column at every hour either, or the check above would
            # pass on a series that had quietly become a constant.
            later = time.strftime("%Y-%m-%dT%H:%M:%SZ",
                                  time.gmtime(946727935.816 + first["observedUt"] + 5400.0))
            st7, d3, _ = post(p, "/api/capture", dict(wbody, seed=990013, atUtc=later, pwv=drift))
            if st7 == 200:
                check("and an hour and a half later is a different column",
                      abs(d3["pwvMm"] - d1["pwvMm"]) > 0.05,
                      f"{d1['pwvMm']:.3f} then {d3['pwvMm']:.3f} mm")
            else:
                print("      (90 min later is not observable tonight; the hour check was skipped)")
    elif not installed:
        print("      (no table installed; build it with tools/fetch_pwv_grid.py to check the term)")

    # ---------------------------------------------------------------- yield
    section("5b. The yield engine")
    st, y, err = post(p, "/api/yield", dict(
        instrument="RC20", site="orm", baselineDays=45, cadenceSeconds=600, nightFraction=0.35,
        periodBins=3, depthBins=3, perCell=6, minPeriodDays=1, maxPeriodDays=12,
        minDepth=0.0009, maxDepth=0.02, hostVMag=11))
    if check("POST /api/yield", st == 200 and has(y or {}, "cells", "assumptions"),
             err or str(y)[:80]):
        # THE THREE COUNTS MUST NOT MERGE. A system that never transits is not one the programme
        # failed to find, and a system with no curve is a hole in the experiment, not a miss.
        check("every system is accounted for exactly once",
              y["searched"] + y["withoutCurve"] == y["transiting"] <= y["systems"],
              f"{y['systems']} seen, {y['transiting']} transiting, {y['searched']} searched, "
              f"{y['withoutCurve']} without a curve")
        check("the transiting fraction is a geometric probability, not everything",
              0.0 < y["transitingFraction"] < 0.5, f"{y['transitingFraction']*100:.1f} %")
        # A yield without its assumptions is a decoration: the number's meaning is entirely in what
        # was assumed to get it.
        check("the map carries what its source cannot represent",
              isinstance(y.get("assumptions"), list) and len(y["assumptions"]) >= 3,
              f"{len(y.get('assumptions') or [])} stated")
        deep = [c for c in y["cells"] if c["recoveredFraction"] is not None
                and c["depthLow"] > 0.005]
        shallow = [c for c in y["cells"] if c["recoveredFraction"] is not None
                   and c["depthLow"] < 0.0015]
        if deep and shallow:
            dm = sum(c["recoveredFraction"] for c in deep) / len(deep)
            sm = sum(c["recoveredFraction"] for c in shallow) / len(shallow)
            check("a deep planet is not harder to find than a shallow one",
                  dm >= sm, f"deep {dm*100:.0f} % against shallow {sm*100:.0f} %")
        # Seeded, so a map can be compared against another one at all.
        st2, y2, _ = post(p, "/api/yield", dict(
            instrument="RC20", site="orm", baselineDays=45, cadenceSeconds=600, nightFraction=0.35,
            periodBins=3, depthBins=3, perCell=6, minPeriodDays=1, maxPeriodDays=12,
            minDepth=0.0009, maxDepth=0.02, hostVMag=11))
        check("the same request twice gives the same map",
              st2 == 200 and y2["detected"] == y["detected"] and y2["searched"] == y["searched"],
              f"{y['detected']}/{y['searched']} against {(y2 or {}).get('detected')}/{(y2 or {}).get('searched')}")

        # THE WINDOW FUNCTION IS NOT A PLANET. Folded at one day, a 5 % diurnal window puts every
        # night in one phase band; a box that swallows the band once "recovered" three planets of
        # 500 to 1495 ppm from 216 samples of a V = 15 star, with 228 of 240 points in the box and
        # twelve outside it as the reference. The exact request that did it, pinned.
        dets = [d for c in y["cells"] for d in c.get("detections", [])]
        check("every detection says what it found, so the map can be audited and not believed",
              "detections" in y["cells"][0] and all(
                  has(d, "injectedPeriodDays", "foundPeriodDays", "foundDepthPpm", "snr",
                      "inTransitPoints", "distinctEpochs", "samples") for d in dets),
              f"{len(dets)} detections described")
        st3, y3, err3 = post(p, "/api/yield", dict(
            periodBins=4, depthBins=4, perCell=8, hostVMag=15, nightFraction=0.05, seed=11))
        dets3 = [d for c in (y3 or {}).get("cells", []) for d in c.get("detections", [])]
        check("a V = 15 star seen 5 % of each day yields nothing under 1495 ppm",
              st3 == 200 and not any(d["injectedDepthPpm"] < 1495 for d in dets3),
              err3 or f"{len(dets3)} detections from {y3['searched']} searched")
        check("no detection's box holds the majority of the points",
              all(2 * d["inTransitPoints"] <= d["samples"] for d in dets + dets3),
              f"{len(dets) + len(dets3)} boxes inspected")
        st4, y4, _ = post(p, "/api/yield", dict(
            periodBins=4, depthBins=4, perCell=8, hostVMag=15, nightFraction=1.0, seed=11))
        check("the same planets seen twenty times more are not found less",
              st4 == 200 and y4["detected"] >= (y3 or {}).get("detected", 0),
              f"{(y3 or {}).get('detected')} at 5 % against {(y4 or {}).get('detected')} at 100 %")

    # ---------------------------------------------------------------- sequences
    # ---------------------------------------------------------------- refusals the probe found missing
    section("5c. What is refused with a reason, where it used to be clamped, substituted or 500")
    # THE YIELD GRID IS LOGARITHMIC: minDepth=0 used to serialise its cell edges as the string NaN.
    st_y0, y0, _ = post(p, "/api/yield", dict(minDepth=0, periodBins=2, depthBins=2, perCell=1))
    check("a zero depth edge on the yield grid is refused, not logged into NaN",
          st_y0 == 400 and has(y0 or {}, "error"), (y0 or {}).get("error", "")[:70])
    st_yi, yi, _ = post(p, "/api/yield", dict(instrument="No Such Scope", periodBins=2, depthBins=2, perCell=1))
    check("an unknown yield instrument is refused rather than printed back as if used",
          st_yi == 400 and "Unknown instrument" in (yi or {}).get("error", ""), (yi or {}).get("error", "")[:70])
    # THE REPORTED SEED REPRODUCES THE MAP. The population and the curves used to be seeded apart
    # and only the first seed was reported, so sending it back gave a different map.
    st_ya, ya, _ = post(p, "/api/yield", dict(periodBins=3, depthBins=3, perCell=4, hostVMag=13, nightFraction=0.35))
    st_yb, yb, _ = post(p, "/api/yield", dict(periodBins=3, depthBins=3, perCell=4, hostVMag=13, nightFraction=0.35,
                                             seed=(ya or {}).get("seed", 0)))
    check("the seed a yield map reports reproduces that map",
          st_ya == 200 and st_yb == 200 and ya["detected"] == yb["detected"] and ya["searched"] == yb["searched"]
          and [c["detected"] for c in ya["cells"]] == [c["detected"] for c in yb["cells"]],
          f"seed {(ya or {}).get('seed')}: {(ya or {}).get('detected')} against {(yb or {}).get('detected')}")
    check("and the map says the instrument and site are labels only",
          st_ya == 200 and any("name the programme and nothing else" in a for a in ya.get("assumptions", [])),
          f"{len((ya or {}).get('assumptions') or [])} assumptions")

    # A NAME WITH A SLASH. "Hubble Space Telescope (OTA/IR)" could not reach /api/instruments/{name}/limits
    # under any encoding; the query form takes any roster name.
    st_hl, hl, _ = get(p, "/api/instrument-limits?name=" + urllib.parse.quote("Hubble Space Telescope (OTA/IR)") + "&filter=Luminance")
    check("the limits of an instrument whose name carries a slash are reachable",
          st_hl == 200 and isinstance(hl, dict) and "error" not in hl, (hl or {}).get("error", "")[:70] if st_hl != 200 else "HTTP 200")
    st_am, am, _ = get(p, "/api/instrument-limits?name=RC20&filter=Luminance&airmass=0.5")
    check("and an airmass below the zenith is refused", st_am == 400, (am or {}).get("error", "")[:60])

    # R8: the review route says which of two failures happened.
    st_rv1, rv1, _ = post(p, "/api/research/runs/no-such-run/review", dict(verdict="real"))
    check("reviewing a run that does not exist says so, with its own status",
          st_rv1 == 404 and "no run is recorded" in (rv1 or {}).get("error", ""), f"{st_rv1} {(rv1 or {}).get('error', '')[:60]}")
    # A verdict that is not a verdict needs a run that exists: the newest record, if there is one.
    st_rl, rl, _ = get(p, "/api/research/runs")
    if st_rl == 200 and rl:
        st_rv2, rv2, _ = post(p, f"/api/research/runs/{rl[0]['id']}/review", dict(verdict="maybe"))
        check("and a verdict that is not a verdict lists the verdicts",
              st_rv2 == 400 and "eclipsing-binary" in (rv2 or {}).get("error", ""), f"{st_rv2} {(rv2 or {}).get('error', '')[:60]}")
    # R7: search-file with one coordinate and not the other is refused, not searched at a guess.
    st_sf, sf, _ = post(p, "/api/research/search-file", dict(file="nothing.fits", raDeg=10.0))
    check("search-file with a right ascension and no declination is refused before the file is looked for",
          st_sf in (400, 404), f"{st_sf} {(sf or {}).get('error', '')[:60]}")

    # A COLOUR THE FIT CANNOT TAKE was silently dropped and the no-colour answer returned. The site
    # is named because the route has always required one (an omitted site is refused as unknown,
    # not defaulted); without it this check tripped the site refusal and never reached the colour.
    st_bv, bv, _ = get(p, "/api/noise-model?telescope=RC20&site=orm&magnitude=12&colourBv=5")
    check("a B-V outside the temperature fit is refused, not silently dropped",
          st_bv == 400 and "colourBv" in (bv or {}).get("error", ""), (bv or {}).get("error", "")[:70])

    if ground:
        # OFF THE SKY. dec=500 used to return an empty frame with HTTP 200 and a FITS header for it.
        st_off, off, _ = post(p, "/api/capture", dict(telescope=ground["name"], site="ohp", filter="Luminance",
                                                      raDeg=83.8, decDeg=500, exposureSeconds=1, binning=8, seed=5))
        check("a declination of 500 degrees is refused, not photographed",
              st_off == 400 and "off the sky" in (off or {}).get("error", ""), (off or {}).get("error", "")[:70])
        st_b16, b16, _ = post(p, "/api/capture", dict(telescope=ground["name"], site="ohp", filter="Luminance",
                                                      raDeg=83.8, decDeg=-5.4, exposureSeconds=1, binning=16, seed=5))
        check("binning 16 is refused with the bounds, not clamped to 8 in silence",
              st_b16 == 400 and "1 to 8" in (b16 or {}).get("error", ""), (b16 or {}).get("error", "")[:70])
        st_sq, sq, _ = post(p, "/api/sequences", dict(telescope=ground["name"], site="ohp", raDeg=83.8, decDeg=-5.4,
                                                      filter="Luminance", frames=3, exposureSeconds=1, binning=8))
        check("a three-frame sequence is refused with the bounds, not stretched to five",
              st_sq == 400 and "5 to 400" in (sq or {}).get("error", ""), (sq or {}).get("error", "")[:70])
        # A SETPOINT WITH NOWHERE TO GO is named, not swallowed.
        nocooler = next((t for t in scopes if not t.get("isSpaceBased") and t.get("hasAdjustableCooler") is False), None)
        if nocooler:
            st_nc, nc, _ = post(p, "/api/capture", dict(telescope=nocooler["name"], site="ohp", filter="Luminance",
                                                        raDeg=83.8, decDeg=-5.4, exposureSeconds=1, binning=8, seed=5,
                                                        detectorTemperatureCelsius=-40))
            check("a cooler setpoint on a detector with no cooler is reported as not applied",
                  st_nc == 200 and "not applied" in ((nc or {}).get("detectorNote") or ""),
                  ((nc or {}).get("detectorNote") or (nc or {}).get("error") or "")[:80])
        else:
            print("      (every ground instrument has an adjustable cooler; the setpoint note was not exercised)")

    st_fc, fcx, _ = get(p, "/api/forecast?ra=83.8&dec=95")
    check("a forecast for a declination beyond the pole is refused",
          st_fc == 400 and "off the sky" in (fcx or {}).get("error", ""), (fcx or {}).get("error", "")[:70])

    # CAMPAIGNS: a 404 with a reason, a bad start refused, an empty warp body refused.
    st_c4, c4, _ = get(p, "/api/campaigns/no-such-campaign")
    check("an unknown campaign answers 404 with the reason", st_c4 == 404 and has(c4 or {}, "error"), (c4 or {}).get("error", "")[:70])
    st_cw, cw, _ = post(p, "/api/campaigns/no-such-campaign/warp", None)
    check("an empty warp body is not a 500", st_cw in (400, 404) and has(cw or {}, "error"), f"HTTP {st_cw}")
    st_cs, cs, _ = post(p, "/api/campaigns", dict(target="51 Peg b", site="ohp", instrument="HARPS", startUtc="yesterday-ish"))
    check("a start date that does not parse is refused, not replaced by now",
          st_cs == 400 and "not a date" in (cs or {}).get("error", ""), (cs or {}).get("error", "")[:70])

    # ---- server round 1 pins: the exact requests that used to pass silently ----
    from urllib.parse import quote

    # S1: a logarithmic grid has no zero edge.
    st1, y1, _ = post(p, "/api/yield", dict(minDepth=0, periodBins=2, depthBins=2, perCell=1))
    check("S1 /api/yield minDepth=0 is refused with the bounds",
          st1 == 400 and "0 < minDepth" in str((y1 or {}).get("error", "")), str(y1)[:120])
    st1b, y1b, _ = post(p, "/api/yield", dict(minPeriodDays=-1, periodBins=2, depthBins=2, perCell=1))
    check("S1 /api/yield minPeriodDays=-1 is refused", st1b == 400, str(y1b)[:120])

    # S2: the echoed seed reproduces the run.
    ya = post(p, "/api/yield", dict(periodBins=2, depthBins=2, perCell=2, seed=7))
    yb = post(p, "/api/yield", dict(periodBins=2, depthBins=2, perCell=2, seed=ya[1]["seed"]))
    check("S2 /api/yield: sending the reported seed back gives the same map",
          ya[0] == 200 and yb[0] == 200 and ya[1]["seed"] == 7
          and ya[1]["cells"] == yb[1]["cells"], f"{ya[0]} {yb[0]}")

    # S3: instrument and site are labels, and the description and the assumptions say so.
    check("S3 /api/yield says its instrument and site are labels",
          ya[0] == 200 and "labels" in ya[1]["programme"]
          and any("name the programme and nothing else" in a for a in ya[1]["assumptions"]),
          ya[1]["programme"] if ya[0] == 200 else str(ya[1]))
    st3, y3, _ = post(p, "/api/yield", dict(periodBins=2, depthBins=2, perCell=1, instrument="No Such Scope"))
    check("S3 /api/yield refuses an unknown instrument instead of printing it", st3 == 400, str(y3)[:120])

    # S8 pattern in /api/yield: bins, perCell and the programme are refused, not clamped.
    st8y, y8y, _ = post(p, "/api/yield", dict(periodBins=2, depthBins=2, perCell=200))
    check("/api/yield perCell=200 is refused with the bounds", st8y == 400 and "1 to 64" in str(y8y), str(y8y)[:120])
    st8n, y8n, _ = post(p, "/api/yield", dict(periodBins=2, depthBins=2, perCell=1, nightFraction=0))
    check("/api/yield nightFraction=0 is refused", st8n == 400, str(y8n)[:120])

    # S4: the roster name with a slash reaches the limits route in both spellings.
    hubble = "Hubble Space Telescope (OTA/IR)"
    st4a, l4a, _ = get(p, "/api/instrument-limits?name=" + quote(hubble) + "&filter=Luminance")
    st4b, l4b, _ = get(p, "/api/instruments/" + quote(hubble, safe="") + "/limits?filter=Luminance")
    check("S4 /api/instrument-limits?name=Hubble... answers", st4a == 200, str(l4a)[:120])
    check("S4 /api/instruments/Hubble%20...%28OTA%2FIR%29/limits answers the same instrument",
          st4b == 200 and (l4b or {}).get("instrument") == (l4a or {}).get("instrument"), str(l4b)[:120])

    # S5: the site's air reaches the FORS2 limit through the airmass.
    st5a, l5a, _ = get(p, "/api/instruments/VLT%20FORS2/limits?site=paranal&airmass=1.0")
    st5b, l5b, _ = get(p, "/api/instruments/VLT%20FORS2/limits?site=paranal&airmass=2.5")
    check("S5 VLT FORS2 limits change with airmass",
          st5a == 200 and st5b == 200 and l5a != l5b and l5b.get("airmass") == 2.5, f"{st5a} {st5b}")
    st5c, l5c, _ = get(p, "/api/instruments/VLT%20FORS2/limits?site=paranal&airmass=20")
    check("S5 airmass 20 is refused with the bounds", st5c == 400, str(l5c)[:120])

    # S6: a colour outside the fit is refused, not dropped.
    st6, n6, _ = get(p, "/api/noise-model?telescope=RC20&site=orm&magnitude=12&colourBv=5")
    check("S6 /api/noise-model colourBv=5 is refused", st6 == 400 and "colourBv" in str(n6), str(n6)[:120])

    # S7 and S8: off the sky, binning and exposure are refused with the bounds.
    base = dict(telescope="RC20", site="orm", raDeg=10, decDeg=20, filter="Luminance", binning=4, exposureSeconds=5)
    st7, c7, _ = post(p, "/api/capture", dict(base, decDeg=500))
    check("S7 /api/capture dec=500 is refused", st7 == 400 and "off the sky" in str(c7), str(c7)[:120])
    st8a, c8a, _ = post(p, "/api/capture", dict(base, binning=16))
    check("S8 /api/capture binning=16 is refused with the bounds", st8a == 400 and "1 to 8" in str(c8a), str(c8a)[:120])
    st8b, c8b, _ = post(p, "/api/capture", dict(base, exposureSeconds=0))
    check("S8 /api/capture exposureSeconds=0 is refused with the bounds", st8b == 400 and "0.1 to 3600" in str(c8b), str(c8b)[:120])
    st8c, s8c, _ = post(p, "/api/sequences", dict(telescope="RC20", site="orm", raDeg=10, decDeg=20, filter="Luminance", binning=16))
    check("S8 /api/sequences binning=16 is refused", st8c == 400 and "1 to 8" in str(s8c), str(s8c)[:120])
    st8d, s8d, _ = post(p, "/api/sequences", dict(telescope="RC20", site="orm", raDeg=10, decDeg=20, filter="Luminance", airmassFrom=0.5))
    check("/api/sequences airmassFrom=0.5 is refused, not clamped to 1", st8d == 400 and "airmassFrom" in str(s8d), str(s8d)[:120])

    # S9: the forecast does not grade a pointing off the sky.
    st9a, f9a, _ = get(p, "/api/forecast?ra=10&dec=95&site=orm")
    st9b, f9b, _ = get(p, "/api/forecast?ra=400&dec=10&site=orm")
    check("S9 /api/forecast dec=95 and ra=400 are refused", st9a == 400 and st9b == 400, f"{st9a} {st9b}")

    # S10, S11, S12: campaigns.
    st11, k11, _ = post(p, "/api/campaigns", dict(target="51 Peg b", instrument="HARPS", site="ohp", startUtc="2026-13-40"))
    check("S11 /api/campaigns refuses a startUtc that does not parse", st11 == 400 and "startUtc" in str(k11), str(k11)[:120])
    stk, kk, _ = post(p, "/api/campaigns", dict(target="51 Peg b", instrument="HARPS", site="ohp"))
    if check("a campaign to test the warp route on", stk == 200 and "id" in (kk or {}), str(kk)[:120]):
        camp = kk["id"]
        for label, body in (("empty", b""), ("null", b"null"), ("{}", b"{}")):
            r10 = urllib.request.Request(f"http://127.0.0.1:{p}/api/campaigns/{camp}/warp", data=body,
                                         headers={"Content-Type": "application/json"}, method="POST")
            try:
                with urllib.request.urlopen(r10, timeout=60) as resp:
                    code10, body10 = resp.status, resp.read()
            except urllib.error.HTTPError as e:
                code10, body10 = e.code, e.read()
            check(f"S10 POST warp with a {label} body is a 400 with a reason",
                  code10 == 400 and b"rate" in body10, f"{code10} {body10[:80]!r}")
        post(p, f"/api/campaigns/{camp}/stop", {})
    for path in ("/api/campaigns/nope", "/api/campaigns/nope/series", "/api/campaigns/nope/stream"):
        st12, b12, _ = get(p, path)
        check(f"S12 GET {path} is a 404 that says why", st12 == 404 and "No campaign" in str(b12), str(b12)[:120])
    st12p, b12p, _ = post(p, "/api/campaigns/nope/warp", dict(rate=1))
    check("S12 POST warp on a missing campaign says why", st12p == 404 and "No campaign" in str(b12p), str(b12p)[:120])

    # S13 and S14: the render's ceiling and the cooler setpoint.
    st14, c14, _ = post(p, "/api/capture", dict(base, telescope="VLT FORS2", site="paranal", raDeg=83.8, decDeg=-5.4, detectorTemperatureCelsius=-50))
    check("S14 a setpoint on a detector with no cooler is reported as not applied",
          st14 == 200 and "no adjustable cooler" in str((c14 or {}).get("detectorNote")), str((c14 or {}).get("detectorNote"))[:120])
    st14b, c14b, _ = post(p, "/api/capture", dict(base, detectorTemperatureCelsius=-100))
    check("S14 a setpoint below the cooler floor reports the bounds and the applied value",
          st14b == 200 and c14b.get("detectorTemperatureC") is not None and c14b.get("detectorTemperatureC") > -100
          and "was applied" in str(c14b.get("detectorNote")), str((c14b or {}).get("detectorNote"))[:120])
    if st14b == 200:
        cid14 = c14b["id"]
        stg, g, _ = post(p, f"/api/captures/{cid14}/calibration", dict(kind="Bias", count=3))
        if check("a generated master for the render pin", stg == 200 and "id" in (g or {}), str(g)[:120]):
            str13, r13, _ = get(p, f"/api/captures/{g['id']}/render?stretch=raw")
            check("S13 a generated master's raw render quotes the light frame's converter, not 65535",
                  str13 == 200 and r13["maxAdu"] < 65535 and "converter" in r13["note"] and "assumed" not in r13["note"],
                  f"{(r13 or {}).get('maxAdu')} {str((r13 or {}).get('note'))[:80]}")

    section("6. Photometric sequences")
    if ground:
        # A field at dec +80 from Roque de los Muchachos never gets lower than about 19 degrees,
        # so airmass 4.9 is genuinely out of reach - unlike dec +36, which sets and passes through
        # every airmass on the way. Asking for the impossible must be refused with the reason
        # rather than quietly clipped or run short.
        st, bad, _ = post(p, "/api/sequences", dict(
            telescope=ground["name"], site="orm", raDeg=252.5, decDeg=80.0,
            filter="Luminance", frames=6, airmassFrom=1.05, airmassTo=4.9))
        check("an unreachable airmass is refused with a reason, not attempted",
              st == 400 and has(bad, "error"), (bad or {}).get("error", "")[:80])

        st, seq, err = post(p, "/api/sequences", dict(
            telescope=ground["name"], site="orm", raDeg=252.5, decDeg=36.4613,
            filter="Luminance", objectName="smoke", exposureSeconds=10, binning=8,
            frames=6, airmassFrom=1.05, airmassTo=1.6, seed=990003, calibrate=True))
        ok = check("POST /api/sequences", st == 200 and has(seq, "id", "telescopeKey"),
                   "" if st == 200 else (err or str(seq)[:90]))
        if ok:
            # THE OTHER ONE THAT SHIPPED BROKEN. The sequence stored the display name where the
            # lookup wanted the key, so it could not find its own instrument afterwards.
            check("the sequence keeps the instrument KEY, not only its display name",
                  seq["telescopeKey"] in [t["name"] for t in scopes],
                  f"{seq['telescopeKey']} / {seq.get('telescope')}")

            # THE TRANSIT CONTRACT THE PANEL DEPENDS ON. `web/app.js` carries an "inject a
            # transit" box and NO HARNESS CAN SEE IT: this file never executes JavaScript. So what
            # is pinned here is the server side the panel talks to, because a silent change to it
            # would break the browser with nothing failing.
            #
            # A HOST THAT IS NOT A REAL STAR IS REFUSED AT THE REQUEST NOW, not one frame at a
            # time. This block used to POST the run, poll it, and hunt for the refusal among the
            # frame errors, which meant the check could only pass after the engine had exposed its
            # way through most of a doomed run. Section 9 owns that case now and asserts it against
            # the POST itself; what remains here is the shape the panel sends.
            st_t, tseq0, _ = post(p, "/api/sequences", dict(
                telescope=ground["name"], site="orm", raDeg=252.5, decDeg=36.4613,
                filter="Luminance", objectName="smoke-transit", exposureSeconds=2, binning=8,
                frames=6, airmassFrom=1.05, airmassTo=1.3, seed=990041,
                transient=dict(depth=0.0064, durationHours=12.0, periodDays=30.0,
                               raDeg=252.5, decDeg=36.4613, matchRadiusArcsec=0.001)))
            check("a transit aimed at empty sky is refused without taking a single frame",
                  st_t == 400 and "empty sky" in str((tseq0 or {}).get("error", "")),
                  str((tseq0 or {}).get("error", ""))[:90])

            # AND THE PANEL'S OWN FIELD NAMES. depth is a FRACTION on the wire while the panel
            # shows parts per thousand; a mismatch here would inject a transit a thousand times
            # too deep and nothing would complain.
            st_t2, tseq, _ = post(p, "/api/sequences", dict(
                telescope=ground["name"], site="orm", raDeg=252.5, decDeg=36.4613,
                filter="Luminance", objectName="smoke-transit-ok", exposureSeconds=2, binning=8,
                frames=6, airmassFrom=1.05, airmassTo=1.3, seed=990042,
                transient=dict(depth=0.0064, durationHours=1.0, periodDays=3.5,
                               matchRadiusArcsec=60.0)))
            check("a sequence carries its injected transit back in the response",
                  st_t2 == 200 and (tseq or {}).get("transient") is not None,
                  f"transient={(tseq or {}).get('transient')!r}")
            if st_t2 == 200:
                post(p, f"/api/sequences/{tseq['id']}/stop", {})

            # AND IT REPORTS ITS OWN WATER. The panel has always had a line for this and could
            # never fill it - the DTO emitted no pwv - so every run, wet or dry, was captioned
            # "no water-vapour term" and a screenshot of a 20 mm run documented it as a control.
            st_w, wseq, _ = post(p, "/api/sequences", dict(
                telescope=ground["name"], site="orm", raDeg=252.5, decDeg=36.4613,
                filter="Luminance", objectName="smoke-wet", exposureSeconds=2, binning=8,
                frames=5, airmassFrom=1.05, airmassTo=1.3, seed=990004,
                pwv=dict(mode="constant", mm=9.5)))
            if check("a sequence run through water says so", st_w == 200 and (wseq or {}).get("pwv"),
                     "" if st_w == 200 else str(wseq)[:70]):
                check("and names the series a reduction would need to correct for it",
                      wseq["pwv"].get("id") and wseq["pwv"].get("description"),
                      f"{wseq['pwv'].get('description')} ({wseq['pwv'].get('id')})")
                post(p, f"/api/sequences/{wseq['id']}/stop", {})
            check("while a dry one still reports none, which is not the same as zero",
                  seq.get("pwv") is None)

            if a.sequence:
                print("      running it to completion…")
                deadline = time.time() + 900
                while time.time() < deadline:
                    st2, cur, _ = get(p, f"/api/sequences/{seq['id']}")
                    if cur and cur.get("state") != "running":
                        break
                    time.sleep(5)
                check("the sequence finishes", cur.get("state") == "finished",
                      f"{cur.get('state')} at {cur.get('done')}/{cur.get('total')}")
                an = (cur or {}).get("analysis")
                check("it produces an analysis with the gate's numbers",
                      has(an or {}, "detrendedPpt", "photonPpt", "ratio"),
                      "no analysis" if not an else f"ratio {an.get('ratio')}")
                check("and the curve the interface plots", bool((an or {}).get("curve")))
                st3, prev, err3 = get(p, f"/api/sequences/{seq['id']}/preview")
                check("a preview frame is served", st3 == 200 and isinstance(prev, bytes) and prev[:4] == b"\x89PNG")
                st4, br, err4 = get(p, f"/api/sequences/{seq['id']}/noise-bridge")
                check("the two noise models can be compared on it",
                      st4 == 200 and has(br, "curveOverMeasured", "imagingOverMeasured"),
                      err4 or str(br)[:80])
            else:
                post(p, f"/api/sequences/{seq['id']}/stop", {})
                print("      (stopped; pass --sequence to run one to completion)")


    # ---------------------------------------------------------------- the water term, derived
    #
    # THE HALF OF THE WATER WORK THAT NEEDS NO FRAMES. Three endpoints answer the three questions
    # an observer actually has - what the column costs, how well it must be known, and whether any
    # of that reaches a fitted depth - and all three were Python scripts before they were
    # endpoints. tools/pwv_requirement.py and tools/pwv_transit_bias.py still exist as the
    # independent check; what is pinned here is that the WIRE agrees with them, because a
    # measurement that only exists as a tools/ script is not delivered.
    section("7. The water term as a programme meets it")

    st_d, data, _ = get(p, "/api/capture/data")
    has_water = st_d == 200 and any("water-vapour" in f for f in (data or {}).get("files", []))
    if not has_water:
        print("      (no water-vapour grid installed; build it with tools/fetch_pwv_grid.py)")
    else:
        DUET = [("g'", 400, 550), ("r'", 550, 700), ("i'", 700, 850), ("z'", 850, 1000),
                ("I+z'", 750, 1000), ("Y", 970, 1070), ("YJ", 970, 1330),
                ("J", 1170, 1330), ("Hs", 1500, 1650)]
        st_r, req, _ = post(p, "/api/pwv/requirement", dict(
            telescope="RC20", airmass=1.5, pwvMm=2.5, stepMm=0.5,
            targetTeffK=2600, compTeffK=5800, budgetPpm=100, achievedMm=0.53, specMm=0.10,
            bands=[dict(name=n, fromNm=a, toNm=b) for n, a, b in DUET],
            gridTargetTeffK=[2000, 2600, 3200, 4000], gridCompTeffK=[3000, 4000, 5000, 5800]))
        if check("POST /api/pwv/requirement derives a table", st_r == 200 and (req or {}).get("bands"),
                 "" if st_r == 200 else str(req)[:90]):
            by = {b["band"]: b for b in req["bands"]}
            # Named once, because an apostrophe inside an f-string is not worth fighting.
            iz, zp, gp = by["I+z'"], by["z'"], by["g'"]

            # THE NUMBER THE WHOLE ARGUMENT RESTS ON. Measured independently by
            # tools/pwv_requirement.py against the same server: 3540.81 umag/mm and 0.0307 mm.
            # The two paths meet here, and if they ever stop meeting, one of them has moved.
            check("and reaches the published I+z' differential to 0.1 %",
                  abs(by["I+z'"]["differentialUmagPerMm"] - 3540.81) / 3540.81 < 1e-3,
                  f"{iz['differentialUmagPerMm']:.2f} against 3540.81 umag/mm")
            check("and the sigma_PWV it inverts to",
                  abs(by["I+z'"]["requiredSigmaMm"] - 0.030665) < 5e-5,
                  f"{iz['requiredSigmaMm']:.6f} against 0.030665 mm")

            # ABSORBED AND DIFFERENTIAL ARE UNCORRELATED, which is the finding the page exists to
            # show. z' absorbs MORE water than I+z' and suffers LESS of it in the ratio; a change
            # that made one track the other would erase the result and nothing else would fail.
            check("z' absorbs more than I+z' and yet suffers less in the ratio",
                  by["z'"]["absorbedUmagPerMm"] > by["I+z'"]["absorbedUmagPerMm"]
                  and by["z'"]["differentialUmagPerMm"] < by["I+z'"]["differentialUmagPerMm"],
                  f"absorbed {zp['absorbedUmagPerMm']:.0f} > {iz['absorbedUmagPerMm']:.0f}, "
                  f"differential {zp['differentialUmagPerMm']:.0f} < {iz['differentialUmagPerMm']:.0f}")

            # THE VERDICT DOES NOT GENERALISE, which is the second finding: the same 0.53 mm that
            # is three times too loose for I+z' is already better than g' needs.
            check("the achieved 0.53 mm is enough for g' and not for I+z'",
                  by["g'"]["requiredSigmaMm"] > 0.53 and by["I+z'"]["requiredSigmaMm"] < 0.53,
                  f"g' needs {gp['requiredSigmaMm']:.2f} mm, "
                  f"I+z' needs {iz['requiredSigmaMm']:.3f} mm")

            grid = (req or {}).get("colourGrid") or {}
            if check("a comparison-ensemble colour matrix comes with it", bool(grid.get("sigmaMm")),
                     f"band {grid.get('band')}"):
                # A COLOUR-MATCHED ENSEMBLE CANCELS THE WATER EXACTLY, and the diagonal must say
                # so as an absence of a limit rather than as a very large number. The JSON must
                # carry null there: System.Text.Json writes Infinity as the quoted string
                # "Infinity", a non-number in a numeric field, which is the defect class this
                # codebase has now found four times.
                ti = grid["targetTeffK"].index(4000.0) if 4000.0 in grid["targetTeffK"] else None
                ci = grid["compTeffK"].index(4000.0) if 4000.0 in grid["compTeffK"] else None
                if ti is not None and ci is not None:
                    check("and a colour-matched ensemble needs no accuracy at all, as null not Infinity",
                          grid["sigmaMm"][ti][ci] is None and grid["unlimited"][ti][ci] is True,
                          f"cell 4000/4000 = {grid['sigmaMm'][ti][ci]!r}")
                flat = [v for row in grid["sigmaMm"] for v in row if v is not None]
                check("and the ensemble's colour moves the requirement by a large factor",
                      max(flat) / min(flat) > 4.0,
                      f"{max(flat)/min(flat):.1f}x across the grid")

                # THE TWO TABLES MUST AGREE ON THE CELL THEY SHARE. The handoff's own §1b tables do
                # not: its per-band row for I+z' reads 0.031 mm and its colour matrix reads 0.028
                # for the same band, temperatures and budget - a factor of 1.0857, which is exactly
                # ppm-of-flux read as micromagnitudes. tools/pwv_requirement.py warns about that
                # confusion in its own comment. This check exists so the two cannot drift again.
                if 2600.0 in grid["targetTeffK"] and 5800.0 in grid["compTeffK"]:
                    cell = grid["sigmaMm"][grid["targetTeffK"].index(2600.0)][grid["compTeffK"].index(5800.0)]
                    band_row = by.get(grid["band"], {})
                    check("the colour matrix and the per-band table agree on the cell they share",
                          cell is not None and band_row.get("requiredSigmaMm") is not None
                          and abs(cell - band_row["requiredSigmaMm"]) / cell < 1e-6,
                          f"{cell} against {band_row.get('requiredSigmaMm')}")

        # A BAND THE INSTRUMENT DOES NOT CARRY IS STILL DRAWABLE when its span is given, and both
        # endpoints must resolve it the same way. They did not: the transmission endpoint refused
        # the NAME before it looked at the span, so a sigma could be derived for a band that could
        # not then be plotted.
        st_t1, tr1, _ = get(p, "/api/pwv/transmission?pwv=3&airmass=1.5&telescope=RC20"
                               "&filter=I%2Bz%27&fromNm=750&toNm=1000&points=64&teffK=2600")
        st_t2, tr2, _ = get(p, "/api/pwv/transmission?pwv=2&airmass=1.5&telescope=RC20"
                               "&filter=I%2Bz%27&fromNm=750&toNm=1000&points=64&teffK=2600")
        st_t3, tr3, _ = get(p, "/api/pwv/transmission?pwv=3&airmass=1.5&telescope=RC20"
                               "&filter=I%2Bz%27&fromNm=750&toNm=1000&points=64&teffK=5800")
        st_t4, tr4, _ = get(p, "/api/pwv/transmission?pwv=2&airmass=1.5&telescope=RC20"
                               "&filter=I%2Bz%27&fromNm=750&toNm=1000&points=64&teffK=5800")
        if check("a band the instrument does not carry is served when its span is given",
                 st_t1 == 200 and tr1.get("lossMmagForTeff") is not None,
                 (tr1 or {}).get("error", "")[:80]):
            check("and it is labelled with the name that was asked for, not the internal slot",
                  tr1.get("filter") == "I+z'" and tr1.get("slot") == "Luminance",
                  f"filter={tr1.get('filter')!r} slot={tr1.get('slot')!r}")
            if st_r == 200 and all(x == 200 for x in (st_t2, st_t3, st_t4)):
                wire = ((tr1["lossMmagForTeff"] - tr2["lossMmagForTeff"])
                        - (tr3["lossMmagForTeff"] - tr4["lossMmagForTeff"])) * 1000.0
                derived = by["I+z'"]["differentialUmagPerMm"]
                check("and the two endpoints agree on it to a part in ten thousand",
                      abs(wire - derived) / derived < 1e-4,
                      f"{wire:.3f} against {derived:.3f} umag/mm")

        # A NAME THAT IS NOT A BAND AND CARRIES NO SPAN IS STILL REFUSED, with the list. Loosening
        # the resolver must not have loosened that.
        st_bad, bad_band, _ = get(p, "/api/pwv/transmission?pwv=3&airmass=1.5&telescope=RC20"
                                     "&filter=Zorblax&points=32")
        check("a band that exists nowhere is refused with the list of the ones that do",
              st_bad == 400 and "not a band on this instrument" in str(bad_band.get("error", "")),
              str(bad_band.get("error", ""))[:80])

        st_l, loss, _ = get(p, "/api/pwv/loss-curve?telescope=RC20&filter=Luminance"
                               "&fromNm=750&toNm=1000&airmass=1.5&points=8"
                               "&teffK=2600,5800&compTeffK=5800")
        if check("GET /api/pwv/loss-curve draws the cost against the column",
                 st_l == 200 and len((loss or {}).get("curve", [])) == 8,
                 "" if st_l == 200 else str(loss)[:80]):
            # MONOTONIC IN THE COLUMN, which is the whole physical content of an absorbing gas.
            losses = [r["loss"][0] for r in loss["curve"]]
            check("and more water always costs more, for the cool star",
                  all(b >= a - 1e-9 for a, b in zip(losses, losses[1:])),
                  f"{losses[0]:.2f} to {losses[-1]:.2f} mmag")
            # THE DIFFERENTIAL AGAINST ITSELF IS EXACTLY ZERO. A star compared with an ensemble of
            # its own temperature loses exactly what the ensemble loses, so this is not "small",
            # it is zero, and asserting the weaker thing would let a real drift through.
            same = [r["differential"][1] for r in loss["curve"]]
            check("and a star against comparisons of its own temperature cancels exactly",
                  all(v == 0 for v in same), f"max |differential| = {max(abs(v) for v in same)}")

        st_b, bias, _ = post(p, "/api/pwv/transit-bias", dict(
            telescope="RC20", band=dict(name="I+z'", fromNm=750, toNm=1000),
            targetTeffK=2600, compTeffK=5800, depthPpm=6920, durationHours=1.0,
            baselineHours=1.0, cadenceSeconds=60, pwvMm=2.5, amplitudeMm=0.53,
            phases=8, baseline="Time", periodsHours=[0.61, 2.53, 23.7, 71.0]))
        if check("POST /api/pwv/transit-bias measures the transfer function",
                 st_b == 200 and len((bias or {}).get("sweep", [])) == 4,
                 "" if st_b == 200 else str(bias)[:90]):
            sweep = {round(s["periodHours"], 2): s["ppmPerMm"] for s in bias["sweep"]}

            # THE ESTIMATOR'S OWN FLOOR IS ZERO. With no water at all it must recover exactly what
            # was injected: an estimator that averaged the in-transit points instead returned
            # -1520 ppm on a 6400 ppm injection with nothing whatever to bias it, because the
            # injected event has ramps and the ramp frames are not at full depth.
            check("and the estimator recovers a clean injection exactly, with no water present",
                  abs(bias["cleanBiasPpm"]) < 1.0, f"{bias['cleanBiasPpm']:+.3f} ppm")

            # THE SHAPE OF THE ANSWER: a slow column is absorbed by the detrend, a column moving on
            # the visit's own timescale is not. Without this the residual table reads as twenty to
            # three hundred times more alarming than it is.
            check("a column drifting on three days is absorbed almost entirely",
                  sweep[71.0] < 20.0, f"{sweep[71.0]:.1f} ppm/mm at 71 h")
            check("and one moving on the visit's own timescale is not",
                  sweep[2.53] > 50 * sweep[71.0],
                  f"{sweep[2.53]:.0f} ppm/mm at 2.53 h against {sweep[71.0]:.1f} at 71 h")

            # AND THE AIRMASS TERM IS NOT OPTIONAL. At a perfectly constant column the water still
            # moves with the slant path, and a baseline linear in time alone cannot absorb the
            # symmetric parabola that produces.
            byb = {c["baseline"]: c for c in bias["constantColumn"]}
            check("a constant column still biases a time-only baseline, and an airmass term fixes it",
                  abs(byb["Time"]["biasPpm"]) > 100 and abs(byb["TimeAirmass"]["biasPpm"]) < 20,
                  f"{byb['Time']['biasPpm']:+.0f} ppm falls to {byb['TimeAirmass']['biasPpm']:+.1f} ppm")

    # ---------------------------------------------------------------- the observer's own instrument
    #
    # THE FEATURE THAT MAKES THIS A TOOL RATHER THAN A DEMONSTRATION, and until now it lost
    # everything on restart and had no interface at all.
    section("8. An instrument the observer defined, and whether it survives")

    duet = dict(
        name="smoke-duet-blue", cameraName="Andor iKon-L 936 BEX2-DD",
        apertureMeters=1.0, focalLengthMeters=8.0, secondaryObstructionFraction=0.3,
        sensorWidthPx=2048, sensorHeightPx=2048, pixelSizeMicrons=13.5,
        quantumEfficiency=0.9, fullWellElectrons=66529, readNoiseElectrons=5.96,
        darkCurrentElectronsPerSecond=0.2, detectorTemperatureCelsius=-60,
        electronsPerAduAtUnityGain=1.077, siteId="orm",
        filters=[dict(label="g'", centralWavelengthNm=475, bandwidthAngstrom=1500),
                 dict(label="z'", centralWavelengthNm=925, bandwidthAngstrom=1500),
                 # A MEASURED CURVE ON A BAND THAT IS NOT RED, GREEN OR BLUE. This was refused
                 # outright until bands carried their own curves, and the refusal was the honest
                 # thing at the time - there were three curve fields and nowhere to put a fourth.
                 dict(label="I+z'", centralWavelengthNm=875, bandwidthAngstrom=2500,
                      transmissionCurve=[dict(wavelengthNm=w, value=v) for w, v in
                                         [(740, 0.01), (760, 0.55), (800, 0.92), (900, 0.95),
                                          (980, 0.90), (1000, 0.40), (1010, 0.02)]])])
    st_i, built, _ = post(p, "/api/instruments/custom", duet)
    if check("POST /api/instruments/custom builds a nine-band instrument", st_i == 200,
             "" if st_i == 200 else str(built)[:110]):
        bands = {b["name"]: b for b in built.get("bands", [])}
        IZ = "I+z'"
        check("and a measured curve rides on a band that is not Red, Green or Blue",
              bands.get(IZ, {}).get("measuredCurve") is True and bands[IZ]["curvePoints"] == 7,
              f"{sorted(bands)} -> {bands.get(IZ, {}).get('curvePoints')} curve points on I+z'")

        # THE CURVE MUST REACH THE INTEGRAL, not merely be stored. Its own support defines the
        # span, which is how you can tell it was used rather than filed.
        st_c, curve, _ = get(p, "/api/pwv/transmission?pwv=5&airmass=1.5"
                                "&telescope=smoke-duet-blue&filter=I%2Bz%27&points=32&teffK=2600")
        check("and the passband integral runs over the curve's own support",
              st_c == 200 and curve.get("measuredFilterCurve") is True
              and abs(curve.get("fromNm", 0) - 740) < 1 and abs(curve.get("toNm", 0) - 1010) < 1,
              f"{curve.get('fromNm')}-{curve.get('toNm')} nm, measured={curve.get('measuredFilterCurve')}")

        st_list, listing, _ = get(p, "/api/instruments/custom")
        check("the store says where the definitions are kept",
              st_list == 200 and bool(listing.get("store")),
              str(listing.get("store"))[:80])
        check("and reports what it could not read rather than dropping it",
              isinstance(listing.get("refusals"), list),
              f"{len(listing.get('refusals') or [])} refusal(s)")

        # DELETION IS PART OF THE CONTRACT: a store you cannot remove from fills up with
        # experiments, and the smoke run would leave one behind on every pass.
        st_del, deleted, _ = delete(p, f"/api/instruments/custom/{built['id']}")
        check("an instrument can be removed again", st_del == 200,
              str(deleted)[:80])
        st_gone, gone, _ = get(p, "/api/instruments/custom")
        check("and it is gone from the list",
              not any(i["id"] == built["id"] for i in gone.get("imaging", [])))

    st_bad_i, bad_i, _ = post(p, "/api/instruments/custom", dict(
        name="smoke-no-aperture", focalLengthMeters=8.0, pixelSizeMicrons=13.5,
        sensorWidthPx=1024, sensorHeightPx=1024, quantumEfficiency=0.9, fullWellElectrons=1000))
    check("an instrument with no aperture is refused with the reason, not built",
          st_bad_i == 400 and "aperture" in str(bad_i.get("error", "")).lower(),
          str(bad_i.get("error", ""))[:90])


    # ---------------------------------------------------------------- a run that cannot work
    #
    # THE THREE WAYS A SEQUENCE USED TO WASTE TWENTY MINUTES BEFORE SAYING SO. Each of these was
    # refused one frame at a time, after the full exposure for each, and what the observer saw at
    # the end was a finished run with no depth, no curve, and a summary that named the wrong cause.
    section("9. A run that cannot work is refused before it takes a frame")

    if ground:
        # 1. A HOST THAT IS NOT A STAR. The transit position defaults to the field centre, which is
        #    almost never a star, so this is the case a first-time user hits by pressing Run.
        st_h, host_ref, _ = post(p, "/api/sequences", dict(
            telescope=ground["name"], site="orm", raDeg=252.5, decDeg=36.4613,
            filter="Luminance", exposureSeconds=1, binning=8, frames=100, seed=990061,
            calibrate=False, airmassFrom=1.05, airmassTo=2.0,
            transient=dict(depth=0.0064, durationHours=1.2, periodDays=3.5,
                           matchRadiusArcsec=3.0)))
        if check("a transit aimed at empty sky is refused at the request, not one frame at a time",
                 st_h == 400 and "empty sky" in str((host_ref or {}).get("error", "")),
                 str((host_ref or {}).get("error", ""))[:90]):
            # AND IT SAYS WHAT TO DO INSTEAD. A refusal that names the nearest star turns a dead end
            # into one click; without it the observer has no way to find a legal host from the page.
            check("and it names the nearest star and where to find a legal one",
                  "nearest star" in host_ref["error"] and "star list" in host_ref["error"],
                  host_ref["error"][-90:])

        # 2. AN AIRMASS RANGE THE FIELD NEVER REACHES. From Paranal a dec +36 field peaks at about
        #    airmass 2.07, so 1.05 is a geometry that does not exist. This used to collapse the
        #    bisection and produce a ladder of ZERO LENGTH: every frame at one instant.
        st_x, x_ref, _ = post(p, "/api/sequences", dict(
            telescope=ground["name"], site="paranal", raDeg=252.5, decDeg=36.4613,
            filter="Luminance", exposureSeconds=1, binning=8, frames=20, seed=990062,
            calibrate=False, airmassFrom=1.05, airmassTo=2.0))
        check("an airmass the field never rises to is refused, with the best it does reach",
              st_x == 400 and "never rises above airmass" in str((x_ref or {}).get("error", "")),
              str((x_ref or {}).get("error", ""))[:100])

        # 3. A WATER SERIES THAT LEAVES THE TABLE. This was refused per frame, after each frame's
        #    work, and each refusal quoted its own full-precision column: sixteen near-identical
        #    lines that between them said one thing. The series' range over the run is knowable
        #    before the first exposure.
        st_w1, wet_ref, _ = post(p, "/api/sequences", dict(
            telescope=ground["name"], site="orm", raDeg=252.5, decDeg=36.4613,
            filter="Luminance", exposureSeconds=1, binning=8, frames=100, seed=990064,
            calibrate=False, airmassFrom=1.05, airmassTo=2.0,
            pwv=dict(mode="analytic", meanMm=15.0, amplitudeMm=8.0, periodHours=2.5,
                     driftMmPerDay=0.0)))
        if check("a water series that leaves the table is refused at the request",
                 st_w1 == 400 and "water column runs" in str((wet_ref or {}).get("error", "")),
                 str((wet_ref or {}).get("error", ""))[:95]):
            # THE ADVICE HAS TO POINT THE RIGHT WAY. One sentence telling the observer to lower the
            # mean is wrong at the dry end, and a refusal that suggests the wrong fix is worse than
            # one that suggests none.
            # The detail shows the ADVICE clause, not whichever 80 characters happen to be last.
            advice = wet_ref["error"]
            advice = advice[advice.find("Lower the mean"):][:70] if "Lower the mean" in advice else advice[:70]
            check("and it says to lower the column when the series is too wet",
                  "Lower the mean" in wet_ref["error"], advice)
        st_w2, dry_ref, _ = post(p, "/api/sequences", dict(
            telescope=ground["name"], site="orm", raDeg=252.5, decDeg=36.4613,
            filter="Luminance", exposureSeconds=1, binning=8, frames=20, seed=990065,
            calibrate=False, airmassFrom=1.05, airmassTo=2.0,
            pwv=dict(mode="analytic", meanMm=1.0, amplitudeMm=2.0, periodHours=2.5,
                     driftMmPerDay=0.0)))
        check("and to raise it when the series is too dry",
              st_w2 == 400 and "Raise the mean" in str((dry_ref or {}).get("error", "")),
              str((dry_ref or {}).get("error", ""))[:95])

        # A series that stays inside the table is untouched by any of this.
        st_w3, wok, _ = post(p, "/api/sequences", dict(
            telescope=ground["name"], site="orm", raDeg=252.5, decDeg=36.4613,
            filter="Luminance", exposureSeconds=1, binning=8, frames=8, seed=990066,
            calibrate=False, airmassFrom=1.05, airmassTo=2.0,
            pwv=dict(mode="analytic", meanMm=6.0, amplitudeMm=4.0, periodHours=2.5,
                     driftMmPerDay=0.0)))
        if check("a series that stays inside the table is accepted", st_w3 == 200,
                 str(wok)[:80]):
            post(p, f"/api/sequences/{wok['id']}/stop", {})

        # 4. THE LADDER IS PLACED IN DARKNESS, not in geometry alone. Where only part of it is
        #    observable the run is clipped and says so, and the frames it does take are frames that
        #    will not be refused.
        st_n, night, _ = post(p, "/api/sequences", dict(
            telescope=ground["name"], site="ohp", raDeg=252.5, decDeg=36.4613,
            filter="Luminance", exposureSeconds=1, binning=8, frames=12, seed=990063,
            calibrate=False, airmassFrom=1.05, airmassTo=2.0))
        if check("a ladder is accepted and carries the window it will actually run in", st_n == 200,
                 str(night)[:80]):
            check("and it reports when the window is not the one that was asked for",
                  "ladderNote" in night,
                  str(night.get("ladderNote") or "the ladder was honoured as given")[:100])
            post(p, f"/api/sequences/{night['id']}/stop", {})

    # ---------------------------------------------------------------- verdict
    print()
    if failures:
        print(f"FAIL  {len(failures)} of {checks} checks")
        for f in failures:
            print(f"        {f}")
        return 1
    print(f"PASS  {checks} checks")
    return 0


if __name__ == "__main__":
    sys.exit(main())
