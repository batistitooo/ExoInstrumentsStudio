#!/usr/bin/env python3
"""
Draw the water-vapour term: what it costs, what survives a ratio, and what it demands of a sensor.

Every number comes from GET /api/pwv/transmission. This file contains NO physics - it asks, it
divides, and it draws. Output is plain SVG with no dependency, which is the same constraint web/
runs under, so the shapes here transfer to the site's canvas directly.

    python3 tools/pwv_plots.py --port 5228 --out figures/

WHAT THE THREE FIGURES ARE FOR

  1. loss against PWV, one line per stellar temperature. This is the figure a telluric-absorption
     paper prints, and reproducing it is how you know the term is right.
  2. the DIFFERENTIAL against PWV. Same axes, but each line is the target minus a comparison
     ensemble - which is the only part that survives the ratio a transit is measured in, and it is
     not proportional to the first plot. A band can absorb more and cost less.
  3. the required sensor accuracy per band, against what a real GNSS receiver achieves.
"""
import argparse, json, math, os, urllib.parse, urllib.request
from concurrent.futures import ThreadPoolExecutor

BANDS = [("g'", 400, 550), ("r'", 550, 700), ("i'", 700, 850), ("z'", 850, 1000),
         ("I+z'", 750, 1000), ("Y", 970, 1070), ("YJ", 970, 1330),
         ("J", 1170, 1330), ("Hs", 1500, 1650)]

# Colour-blind-safe, dark to light, so the lines stay distinguishable printed in grey.
SERIES = ["#0b3d91", "#1f77b4", "#2ca02c", "#bcbd22", "#ff7f0e", "#d62728"]
INK, MUTED, GRID, PAPER = "#16181d", "#6b7280", "#e3e6ea", "#ffffff"
FONT = "-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif"


def loss(base, pwv, lo, hi, teff, airmass=1.5, points=64):
    q = urllib.parse.urlencode(dict(pwv=pwv, airmass=airmass, telescope="RC20",
                                    filter="Luminance", fromNm=lo, toNm=hi,
                                    points=points, teffK=teff))
    with urllib.request.urlopen(f"{base}/api/pwv/transmission?{q}", timeout=600) as r:
        d = json.load(r)
    v = d.get("lossMmagForTeff")
    if v is None:
        raise SystemExit(f"refused: {lo}-{hi} nm at {pwv} mm, {teff} K: {d.get('spanNote')}")
    return float(v)


# ----------------------------------------------------------------- svg primitives

def esc(s):
    return (str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


class Svg:
    def __init__(self, w, h, title):
        self.w, self.h, self.o = w, h, []
        self.o.append(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" '
                      f'width="{w}" height="{h}" font-family="{FONT}">')
        self.o.append(f'<title>{esc(title)}</title>')
        self.o.append(f'<rect width="{w}" height="{h}" fill="{PAPER}"/>')

    def text(self, x, y, s, size=12, fill=INK, anchor="start", weight="400", style=""):
        st = f' font-style="{style}"' if style else ""
        self.o.append(f'<text x="{x:.1f}" y="{y:.1f}" font-size="{size}" fill="{fill}" '
                      f'text-anchor="{anchor}" font-weight="{weight}"{st}>{esc(s)}</text>')

    def line(self, x1, y1, x2, y2, stroke=GRID, width=1, dash=None):
        d = f' stroke-dasharray="{dash}"' if dash else ""
        self.o.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" '
                      f'stroke="{stroke}" stroke-width="{width}"{d}/>')

    def path(self, pts, stroke, width=2.2, dash=None):
        if not pts: return
        d = "M " + " L ".join(f"{x:.2f} {y:.2f}" for x, y in pts)
        da = f' stroke-dasharray="{dash}"' if dash else ""
        self.o.append(f'<path d="{d}" fill="none" stroke="{stroke}" stroke-width="{width}" '
                      f'stroke-linejoin="round" stroke-linecap="round"{da}/>')

    def rect(self, x, y, w, h, fill, opacity=1.0):
        self.o.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{max(0,w):.1f}" '
                      f'height="{max(0,h):.1f}" fill="{fill}" opacity="{opacity}"/>')

    def dot(self, x, y, r, fill):
        self.o.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{r}" fill="{fill}"/>')

    def save(self, path):
        self.o.append("</svg>")
        open(path, "w").write("\n".join(self.o))
        return path


def nice_ticks(lo, hi, n=5):
    if hi <= lo: return [lo]
    raw = (hi - lo) / n
    mag = 10 ** math.floor(math.log10(raw))
    step = min((m * mag for m in (1, 2, 2.5, 5, 10)), key=lambda s: abs(s - raw))
    t, out = math.ceil(lo / step) * step, []
    while t <= hi * 1.0001:
        out.append(round(t, 10)); t += step
    return out


def frame(sv, L, T, W, H, xlo, xhi, ylo, yhi, xlab, ylab, title, sub,
          logy=False, xticks=None, yticks=None, yfmt=None):
    sv.text(L, T - 34, title, size=15, weight="600")
    if sub: sv.text(L, T - 16, sub, size=11, fill=MUTED)
    fy = (lambda v: T + H - (math.log10(v) - math.log10(ylo)) /
          (math.log10(yhi) - math.log10(ylo)) * H) if logy else \
         (lambda v: T + H - (v - ylo) / (yhi - ylo) * H)
    fx = lambda v: L + (v - xlo) / (xhi - xlo) * W
    yt = yticks if yticks is not None else (
        [10 ** e for e in range(math.floor(math.log10(ylo)), math.ceil(math.log10(yhi)) + 1)]
        if logy else nice_ticks(ylo, yhi))
    for v in yt:
        if v < ylo * 0.999 or v > yhi * 1.001: continue
        y = fy(v)
        sv.line(L, y, L + W, y)
        sv.text(L - 8, y + 4, (yfmt(v) if yfmt else f"{v:g}"), size=11, fill=MUTED, anchor="end")
    for v in (xticks if xticks is not None else nice_ticks(xlo, xhi)):
        if v < xlo * 0.999 or v > xhi * 1.001: continue
        x = fx(v)
        sv.line(x, T, x, T + H)
        sv.text(x, T + H + 18, f"{v:g}", size=11, fill=MUTED, anchor="middle")
    sv.line(L, T + H, L + W, T + H, stroke=INK, width=1.4)
    sv.line(L, T, L, T + H, stroke=INK, width=1.4)
    sv.text(L + W / 2, T + H + 40, xlab, size=12, anchor="middle")
    sv.o.append(f'<text transform="translate({L-46},{T+H/2}) rotate(-90)" font-size="12" '
                f'fill="{INK}" text-anchor="middle">{esc(ylab)}</text>')
    return fx, fy


# ----------------------------------------------------------------- figure 1 and 2

def fig_loss_and_differential(base, out, band, lo, hi, temps, comp_teff, airmass, pwvs, points):
    """Peter's figure, and the one it does not have beside it."""
    jobs = [(t, p) for t in list(temps) + [comp_teff] for p in pwvs]
    with ThreadPoolExecutor(max_workers=6) as ex:
        vals = list(ex.map(lambda j: loss(base, j[1], lo, hi, j[0], airmass, points), jobs))
    L = {j: v for j, v in zip(jobs, vals)}

    made = []
    for mode in ("absorbed", "differential"):
        series = []
        for t in temps:
            ys = [L[(t, p)] - (L[(comp_teff, p)] if mode == "differential" else 0.0) for p in pwvs]
            series.append((t, ys))
        allv = [abs(y) for _, ys in series for y in ys]
        yhi = max(allv) * 1.08 or 1.0

        sv = Svg(790, 470, f"water vapour, {band}, {mode}")
        title = (f"{band}: what the water absorbs"
                 if mode == "absorbed" else
                 f"{band}: what SURVIVES the ratio, against {comp_teff:.0f} K comparisons")
        sub = (f"airmass {airmass}, referenced to the table's driest column (0.5 mm). "
               f"One line per stellar temperature."
               if mode == "absorbed" else
               "This is the part that limits a transit. A grey term cancels in a ratio; "
               "a colour-dependent one does not.")
        fx, fy = frame(sv, 92, 66, 560, 330, pwvs[0], pwvs[-1], 0, yhi,
                       "precipitable water vapour  (mm)", "loss  (mmag)", title, sub)
        # LABELS PUSHED APART. Six lines converge at the right edge and the top two were printed on
        # top of each other, which is a plot that lies about how many series it has.
        ends = sorted([(fy(max(0.0, ys[-1])), i, t, ys[-1]) for i, (t, ys) in enumerate(series)])
        placed, last = [], -1e9
        for y, i, t, v in ends:
            y = max(y, last + 14.0)
            placed.append((y, i, t, v)); last = y
        for i, (t, ys) in enumerate(series):
            c = SERIES[i % len(SERIES)]
            sv.path([(fx(p), fy(max(0.0, y))) for p, y in zip(pwvs, ys)], c)
            sv.dot(fx(pwvs[-1]), fy(max(0.0, ys[-1])), 3, c)
        for y, i, t, v in placed:
            c = SERIES[i % len(SERIES)]
            y0 = fy(max(0.0, v))
            if abs(y - y0) > 1.5:
                sv.line(fx(pwvs[-1]) + 4, y0, 656, y, stroke=c, width=0.8)
            sv.text(660, y + 4, f"{t:.0f} K", size=11, fill=c, weight="600")
            sv.text(660, y + 16, f"{v:.1f}", size=9.5, fill=MUTED)
        if mode == "differential":
            sv.text(100, 410, f"at {comp_teff:.0f} K the line would be exactly zero: a colour-matched "
                              f"ensemble cancels the water identically", size=10, fill=MUTED)
        made.append(sv.save(os.path.join(out, f"pwv_{mode}_{band.replace('+','').replace(chr(39),'')}.svg")))
    return made


# ----------------------------------------------------------------- figure 3

def fig_requirement(base, out, target_k, comp_k, airmass, p0, h, budget_ppm, points,
                    achieved_mm=0.53, goal_mm=0.10):
    """What each band demands of a water sensor, against what one actually delivers."""
    jobs = [(n, l, hh, t, p) for n, l, hh in BANDS for t in (target_k, comp_k) for p in (p0 - h, p0 + h)]
    with ThreadPoolExecutor(max_workers=6) as ex:
        vals = list(ex.map(lambda j: loss(base, j[4], j[1], j[2], j[3], airmass, points), jobs))
    G = {(j[0], j[3], j[4]): v for j, v in zip(jobs, vals)}
    budget_umag = -2.5 * math.log10(1.0 - budget_ppm * 1e-6) * 1e6

    rows = []
    for n, l, hh in BANDS:
        dT = G[(n, target_k, p0 + h)] - G[(n, target_k, p0 - h)]
        dC = G[(n, comp_k, p0 + h)] - G[(n, comp_k, p0 - h)]
        slope = abs(dT - dC) / (2 * h) * 1000.0                     # umag per mm
        rows.append((n, slope, budget_umag / slope if slope > 0 else float("inf")))

    lo, hi = 0.01, 10.0
    sv = Svg(760, 500, "required PWV accuracy per band")
    L, T, W, H = 92, 76, 560, 330
    fx = lambda v: L + (math.log10(v) - math.log10(lo)) / (math.log10(hi) - math.log10(lo)) * W
    sv.text(L, T - 44, "How well must the water column be known?", size=15, weight="600")
    sv.text(L, T - 26, f"{target_k:.0f} K target against {comp_k:.0f} K comparisons, airmass "
                       f"{airmass}, around {p0} mm, for a {budget_ppm:.0f} ppm budget", size=11, fill=MUTED)

    # THE TWO REFERENCE LINES ARE THE POINT of the figure: the band's demand against what a real
    # low-cost GNSS installation at this very site actually reaches.
    sv.rect(fx(lo), T, fx(achieved_mm) - fx(lo), H, "#d62728", 0.055)
    for mm, lab, col, dash in ((goal_mm, "0.10 mm  goal", "#6b7280", "5 4"),
                               (achieved_mm, "0.53 mm  achieved by GNSS", "#d62728", None)):
        sv.line(fx(mm), T - 6, fx(mm), T + H, stroke=col, width=1.6, dash=dash)
        sv.text(fx(mm), T - 11, lab, size=10, fill=col, anchor="middle", weight="600")

    step = H / len(rows)
    for i, (n, slope, need) in enumerate(rows):
        y = T + step * (i + 0.5)
        harder = need < achieved_mm
        c = "#d62728" if harder else "#2ca02c"
        sv.line(L, y, L + W, y, stroke="#f1f3f5", width=step * 0.82)
        x = fx(min(max(need, lo), hi))
        sv.line(L, y, x, y, stroke=c, width=3.2)
        sv.dot(x, y, 5, c)
        sv.text(L - 10, y + 4, n, size=12, anchor="end",
                weight="700" if harder else "500", fill=INK if harder else MUTED)
        sv.text(min(x + 12, L + W - 4), y + 4,
                f"{need:.3f} mm" if need < 1 else f"{need:.2f} mm", size=10.5, fill=c, weight="600")
    for v in (0.01, 0.03, 0.1, 0.3, 1, 3, 10):
        sv.text(fx(v), T + H + 18, f"{v:g}", size=11, fill=MUTED, anchor="middle")
        sv.line(fx(v), T + H, fx(v), T + H + 5, stroke=MUTED)
    sv.line(L, T + H, L + W, T + H, stroke=INK, width=1.4)
    sv.text(L + W / 2, T + H + 40, "required accuracy on the water column  (mm, log scale)",
            size=12, anchor="middle")
    sv.text(L, T + H + 66, "red: the band needs the column known better than a low-cost GNSS "
                           "delivers.  green: it does not.", size=10.5, fill=MUTED)
    sv.text(L, T + H + 82, "The verdict is per band. It is not a property of the observatory.",
            size=10.5, fill=INK, weight="600")
    return [sv.save(os.path.join(out, "pwv_requirement.svg"))], rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5228)
    ap.add_argument("--out", default="figures")
    ap.add_argument("--band", default="I+z'")
    ap.add_argument("--airmass", type=float, default=1.5)
    ap.add_argument("--comp-teff", type=float, default=5800.0)
    ap.add_argument("--target-teff", type=float, default=2600.0)
    ap.add_argument("--budget-ppm", type=float, default=100.0)
    ap.add_argument("--points", type=int, default=64)
    a = ap.parse_args()
    base = f"http://127.0.0.1:{a.port}"
    os.makedirs(a.out, exist_ok=True)

    band = next((b for b in BANDS if b[0] == a.band), None)
    if band is None:
        raise SystemExit(f"unknown band {a.band!r}; known: {', '.join(b[0] for b in BANDS)}")
    pwvs = [0.5 + i * (10.0 - 0.5) / 18 for i in range(19)]
    made = fig_loss_and_differential(base, a.out, band[0], band[1], band[2],
                                     [2000, 2600, 3200, 4000, 5000, 6000],
                                     a.comp_teff, a.airmass, pwvs, a.points)
    more, rows = fig_requirement(base, a.out, a.target_teff, a.comp_teff, a.airmass,
                                 2.5, 0.25, a.budget_ppm, a.points)
    for f in made + more:
        print("wrote", f)
    for n, slope, need in rows:
        print(f"  {n:5s} {slope:8.0f} umag/mm   needs {need:8.3f} mm")


if __name__ == "__main__":
    main()


# ----------------------------------------------------------------- figure 4: the transfer function

def fig_transfer(out, data, floor_ppm=None):
    """
    THE PLOT THAT ANSWERS THE QUESTION. Every per-band residual table is the amplitude of a
    perturbation on the light curve; this is how much of it reaches the FITTED depth after the
    baseline a real reduction removes. The two are different by two orders of magnitude at the
    slow end, and that difference is the answer.
    """
    rows = data["curve"]
    xs = [r["periodH"] for r in rows]
    ys = [max(1e-3, r["ppmPerMm"]) for r in rows]
    lo, hi = min(xs), max(xs)
    ylo, yhi = 1.0, max(4000.0, max(ys) * 1.4)

    sv = Svg(800, 540, "how much water reaches the fitted depth")
    L, T, W, H = 96, 84, 600, 320
    fx = lambda v: L + (math.log10(v) - math.log10(lo)) / (math.log10(hi) - math.log10(lo)) * W
    fy = lambda v: T + H - (math.log10(max(v, ylo)) - math.log10(ylo)) / (math.log10(yhi) - math.log10(ylo)) * H

    sv.text(L, T - 52, "How much of a water error actually reaches the measured depth",
            size=15, weight="600")
    sv.text(L, T - 34, f"{data['bandFromNm']:.0f}-{data['bandToNm']:.0f} nm, "
                       f"{data['targetTeffK']:.0f} K against {data['compTeffK']:.0f} K, "
                       f"{data['durationH']:.1f} h transit in a {data['windowH']:.1f} h window, "
                       f"baseline fitted on the out-of-transit points",
            size=11, fill=MUTED)
    sv.text(L, T - 18, "The perturbation is the same everywhere on this axis. Only its TIMESCALE "
                       "changes, and the detrend does the rest.", size=11, fill=MUTED)

    for v in (1, 10, 100, 1000):
        if v < ylo or v > yhi: continue
        sv.line(L, fy(v), L + W, fy(v))
        sv.text(L - 8, fy(v) + 4, f"{v:,}", size=11, fill=MUTED, anchor="end")
    for v in (0.25, 0.5, 1, 2, 4, 8, 24, 96):
        if v < lo or v > hi: continue
        sv.line(fx(v), T, fx(v), T + H)
        sv.text(fx(v), T + H + 18, f"{v:g}" if v >= 1 else f"{v}", size=11, fill=MUTED, anchor="middle")

    # THE TWO MARKERS THAT EXPLAIN THE SHAPE. The peak is not at the transit duration, it is at the
    # OBSERVING WINDOW: a half cycle across the whole visit is what a baseline cannot tell from a
    # transit, and that is where the damage is.
    for v, lab, col in ((data["durationH"], "transit duration", "#6b7280"),
                        (data["windowH"], "observing window", "#d62728")):
        if lo <= v <= hi:
            sv.line(fx(v), T - 4, fx(v), T + H, stroke=col, width=1.4, dash="4 4")
            sv.text(fx(v), T - 8, lab, size=10, fill=col, anchor="middle", weight="600")

    if floor_ppm:
        sv.rect(L, T, W, max(0.0, fy(floor_ppm) - T), "#2ca02c", 0.05)
        sv.line(L, fy(floor_ppm), L + W, fy(floor_ppm), stroke="#2ca02c", width=1.6)
        sv.text(L + W - 4, fy(floor_ppm) - 7, f"photon noise floor, {floor_ppm:,.0f} ppm",
                size=10, fill="#2ca02c", anchor="end", weight="600")

    sv.path([(fx(x), fy(y)) for x, y in zip(xs, ys)], "#0b3d91", 2.6)
    sv.line(L, T + H, L + W, T + H, stroke=INK, width=1.4)
    sv.line(L, T, L, T + H, stroke=INK, width=1.4)
    sv.text(L + W / 2, T + H + 40, "timescale of the water-column variation  (hours, log scale)",
            size=12, anchor="middle")
    sv.o.append(f'<text transform="translate({L-52},{T+H/2}) rotate(-90)" font-size="12" '
                f'fill="{INK}" text-anchor="middle">bias on the fitted depth  (ppm per mm of PWV)</text>')
    sv.text(L, T + H + 66, "Slow water is absorbed by the baseline. Fast water averages out. "
                           "Only water moving on the visit's own timescale survives.",
            size=10.5, fill=MUTED)
    sv.text(L, T + H + 80, "The spikes below 1 h are a single sinusoid beating against the 1 h "
                           "transit; a real spectrum would follow the envelope, not the nulls.",
            size=10.5, fill=MUTED)
    sv.text(L, T + H + 96, "How often the column actually moves on this timescale is the one "
                           "thing nobody has measured.", size=10.5, fill=INK, weight="600")
    return sv.save(os.path.join(out, "pwv_transfer.svg"))


# ----------------------------------------------------------------- figure 5: signal vs noise vs threshold

def fig_budget(out, bands, excursion_mm, note):
    """Water against the photon floor against the thing you are trying to detect."""
    sv = Svg(800, 540, "water against noise against transit depth")
    L, T, W, H = 108, 92, 560, 330
    lo, hi = 1.0, 30000.0
    fx = lambda v: L + (math.log10(max(v, lo)) - math.log10(lo)) / (math.log10(hi) - math.log10(lo)) * W
    sv.text(L - 40, T - 60, "Is water the thing that limits you?", size=15, weight="600")
    sv.text(L - 40, T - 42, note, size=11, fill=MUTED)

    step = H / len(bands)
    for i, b in enumerate(bands):
        y = T + step * (i + 0.5)
        sv.line(L - 40, y, L + W, y, stroke="#f7f8f9", width=step * 0.86)
        sv.text(L - 46, y + 4, b["band"], size=12, anchor="end", weight="600")
        if b.get("floorPpm", -1) > 0:
            sv.line(fx(1.0), y, fx(b["floorPpm"]), y, stroke="#c7ccd1", width=9)
            sv.dot(fx(b["floorPpm"]), y, 4.5, "#6b7280")
        w = b["waterPpm"]
        sv.line(fx(1.0), y + 0.5, fx(w), y + 0.5, stroke="#0b3d91", width=3.4)
        sv.dot(fx(w), y, 4.5, "#0b3d91")
        sv.text(fx(w) + 9, y + 4, f"{w:,.0f}", size=9.5, fill="#0b3d91", weight="600")
        # PLACED AFTER THE BAR, not under it. These four rows have no floor to draw, and the note
        # saying why was printed straight through the bar that was there.
        if not b.get("floorPpm", -1) > 0:
            sv.text(fx(w) + 48, y + 4, "no NIR detector on the roster - floor unmeasured",
                    size=9.5, fill=MUTED, style="italic")

    # STAGGERED, because three depth markers inside a factor of five overlap at this scale and a
    # legend printed on top of itself is a legend that lies about how many things it names.
    for k, (d, lab) in enumerate(((2949, "shallowest real transit"), (6920, "TRAPPIST-1 b"),
                                  (14831, "deepest"))):
        dy = -12 - 14 * (k % 2)
        sv.line(fx(d), T + dy + 4, fx(d), T + H, stroke="#d62728", width=1.3, dash="4 4")
        sv.text(fx(d), T + dy, lab, size=9.5, fill="#d62728",
                anchor="end" if k == 0 else ("middle" if k == 1 else "start"))
    for v in (1, 10, 100, 1000, 10000):
        sv.text(fx(v), T + H + 18, f"{v:,}", size=11, fill=MUTED, anchor="middle")
        sv.line(fx(v), T + H, fx(v), T + H + 5, stroke=MUTED)
    sv.line(L - 40, T + H, L + W, T + H, stroke=INK, width=1.4)
    sv.text(L + W / 2 - 20, T + H + 40, "parts per million of flux  (log scale)", size=12, anchor="middle")
    sv.text(L - 40, T + H + 68, "blue: water residual.   grey: the photon floor in a 30-minute bin.   "
                                "red: real transit depths.", size=10.5, fill=MUTED)
    sv.text(L - 40, T + H + 84, "Water is under the noise everywhere except I+z', and under every "
                                "real transit depth in all nine bands.", size=10.5, fill=INK, weight="600")
    return sv.save(os.path.join(out, "pwv_budget.svg"))


# ----------------------------------------------------------------- figure 6: where to put the split

def fig_band_edge(base, out, blue_nm, edges, scopes, target_k, comp_k, airmass, split_nm, points):
    """
    Where the red flank falls, against how much differential water that costs.

    THE REASON THIS IS THE FIGURE WORTH SHOWING AN INSTRUMENT BUILDER. Everything else here is a
    property of the atmosphere, which nobody can change. This one is a property of the INSTRUMENT,
    it is chosen once, and it moves the answer by more than the atmosphere does. Drawn for several
    telescopes because the roster's two sourced quantum-efficiency curves put the peak in the same
    place as the flat-response ones: the shape belongs to the 940 nm water band, not to the silicon.
    """
    jobs = [(s, e) for s in scopes for e in edges]

    def slope(scope, hi, P=2.5, h=0.25):
        d = {}
        for teff in (target_k, comp_k):
            d[teff] = (loss(base, P + h, blue_nm, hi, teff, airmass, points)
                       - loss(base, P - h, blue_nm, hi, teff, airmass, points))
        return abs(d[target_k] - d[comp_k]) / (2 * h) * 1000.0

    with ThreadPoolExecutor(max_workers=6) as ex:
        vals = list(ex.map(lambda j: slope(j[0], j[1]), jobs))
    G = {j: v for j, v in zip(jobs, vals)}

    yhi = max(vals) * 1.15
    sv = Svg(800, 520, "where the split falls")
    L, T, W, H = 92, 88, 600, 320
    fx = lambda v: L + (v - edges[0]) / (edges[-1] - edges[0]) * W
    fy = lambda v: T + H - v / yhi * H

    sv.text(L, T - 56, "The one term the instrument controls: where the red flank falls",
            size=15, weight="600")
    sv.text(L, T - 38, f"blue edge held at {blue_nm:.0f} nm, {target_k:.0f} K target against "
                       f"{comp_k:.0f} K comparisons, airmass {airmass}", size=11, fill=MUTED)
    sv.text(L, T - 22, "Three telescopes, two of which carry measured QE curves. They agree, "
                       "because the shape belongs to the 940 nm water band.", size=11, fill=MUTED)

    for v in nice_ticks(0, yhi, 5):
        sv.line(L, fy(v), L + W, fy(v))
        sv.text(L - 8, fy(v) + 4, f"{v:,.0f}", size=11, fill=MUTED, anchor="end")
    for v in edges:
        sv.line(fx(v), T + H, fx(v), T + H + 5, stroke=MUTED)
        sv.text(fx(v), T + H + 18, f"{v:.0f}", size=10, fill=MUTED, anchor="middle")

    # THE SPLIT, drawn where it actually is.
    sv.line(fx(split_nm), T - 10, fx(split_nm), T + H, stroke="#d62728", width=2)
    sv.text(fx(split_nm), T - 14, f"DUET splits here, {split_nm:.0f} nm",
            size=10.5, fill="#d62728", anchor="middle", weight="700")

    # DASHED SO ALL THREE ARE VISIBLE. They lie within about 12 % of each other, which is the
    # finding - but three solid curves drawn on top of one another look like one curve and a
    # legend that names two things that are not there.
    for i, s in enumerate(scopes):
        c = SERIES[[0, 2, 5][i % 3]]
        dash = (None, "7 4", "2 3")[i % 3]
        ys = [G[(s, e)] for e in edges]
        sv.path([(fx(e), fy(y)) for e, y in zip(edges, ys)], c, 2.4 if i == 0 else 1.8, dash)
        for e, y in zip(edges, ys):
            sv.dot(fx(e), fy(y), 2.4, c)
        sv.text(L + W + 6, fy(ys[-1]) + 4 + 14 * i, s, size=10, fill=c, weight="600")
    sv.text(L + W + 6, T + H + 22, "they overlap:", size=9, fill=MUTED)
    sv.text(L + W + 6, T + H + 34, "that is", size=9, fill=MUTED)
    sv.text(L + W + 6, T + H + 46, "the point", size=9, fill=MUTED)

    peak_i = max(range(len(edges)), key=lambda k: G[(scopes[0], edges[k])])
    sv.dot(fx(edges[peak_i]), fy(G[(scopes[0], edges[peak_i])]), 5.5, "#d62728")
    sv.text(fx(edges[peak_i]), fy(G[(scopes[0], edges[peak_i])]) - 12,
            f"worst at {edges[peak_i]:.0f} nm", size=10, fill="#d62728", anchor="middle", weight="600")

    sv.line(L, T + H, L + W, T + H, stroke=INK, width=1.4)
    sv.line(L, T, L, T + H, stroke=INK, width=1.4)
    sv.text(L + W / 2, T + H + 40, "red edge of the blue arm  (nm)", size=12, anchor="middle")
    sv.o.append(f'<text transform="translate({L-56},{T+H/2}) rotate(-90)" font-size="12" '
                f'fill="{INK}" text-anchor="middle">differential water  (umag per mm of PWV)</text>')
    sv.text(L, T + H + 68, "A factor of 15 to 18 across 200 nm, and the split sits at about "
                           "88 % of the worst point.", size=10.5, fill=INK, weight="600")
    sv.text(L, T + H + 84, "Moving the cut 35 nm bluer would cost a quarter as much water. "
                           "Whether that trade is worth it is an instrument", size=10.5, fill=MUTED)
    sv.text(L, T + H + 98, "question - but it can now be made against a number instead of "
                           "an intuition.", size=10.5, fill=MUTED)
    return sv.save(os.path.join(out, "pwv_band_edge.svg"))
