#!/usr/bin/env python3
"""
The water-vapour figures, in matplotlib.

Every number comes from GET /api/pwv/transmission and GET /api/noise-model on a running server.
This file contains no physics: it asks, it divides, it draws.

    python3 tools/pwv_figures.py --port 5228 --out figures
"""
import argparse, json, math, os, urllib.parse, urllib.request
from concurrent.futures import ThreadPoolExecutor

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

BANDS = [("g'", 400, 550), ("r'", 550, 700), ("i'", 700, 850), ("z'", 850, 1000),
         ("I+z'", 750, 1000), ("Y", 970, 1070), ("YJ", 970, 1330),
         ("J", 1170, 1330), ("Hs", 1500, 1650)]

plt.rcParams.update({
    "figure.dpi": 130, "savefig.dpi": 130, "font.size": 10,
    "axes.spines.top": False, "axes.spines.right": False,
    "axes.grid": True, "grid.alpha": 0.25, "grid.linewidth": 0.6,
    "figure.facecolor": "white", "axes.facecolor": "white",
})


def api(base, path, **q):
    with urllib.request.urlopen(f"{base}{path}?{urllib.parse.urlencode(q)}", timeout=600) as r:
        return json.load(r)


def loss(base, pwv, lo, hi, teff, airmass, scope="RC20", points=64):
    d = api(base, "/api/pwv/transmission", pwv=pwv, airmass=airmass, telescope=scope,
            filter="Luminance", fromNm=lo, toNm=hi, points=points, teffK=teff)
    v = d.get("lossMmagForTeff")
    if v is None:
        raise SystemExit(f"refused {lo}-{hi} nm at {pwv} mm: {d.get('spanNote')}")
    return float(v)


def slopes(base, target_k, comp_k, airmass, P=2.5, h=0.25, points=64):
    """Absorbed and differential, in micromagnitudes per mm, for every band."""
    jobs = [(n, lo, hi, t, p) for n, lo, hi in BANDS
            for t in (target_k, comp_k) for p in (P - h, P + h)]
    with ThreadPoolExecutor(max_workers=6) as ex:
        vals = list(ex.map(lambda j: loss(base, j[4], j[1], j[2], j[3], airmass, points=points), jobs))
    G = {(j[0], j[3], j[4]): v for j, v in zip(jobs, vals)}
    out = {}
    for n, lo, hi in BANDS:
        dT = G[(n, target_k, P + h)] - G[(n, target_k, P - h)]
        dC = G[(n, comp_k, P + h)] - G[(n, comp_k, P - h)]
        out[n] = (abs(dT) / (2 * h) * 1000.0, abs(dT - dC) / (2 * h) * 1000.0)
    return out


def ppm(umag):
    return (1.0 - 10.0 ** (-0.4 * umag * 1e-6)) * 1e6


# --------------------------------------------------------------------- figures

def fig_absorbed_vs_differential(sl, out):
    """
    THE RESULT THAT IS ACTUALLY COUNTERINTUITIVE. How much water a band absorbs and how much of it
    limits a transit are not the same ranking. z' swallows more water than I+z' and costs half as
    much; i' swallows eighty times what r' does and costs less. Picking a filter to minimise
    absorption optimises the wrong variable.
    """
    names = [n for n, _, _ in BANDS]
    ab = [sl[n][0] for n in names]
    di = [sl[n][1] for n in names]
    fig, ax = plt.subplots(figsize=(7.4, 5.2))
    ax.scatter(ab, di, s=70, zorder=3, color="#1f4e9c")
    for n, x, y in zip(names, ab, di):
        ax.annotate(n, (x, y), textcoords="offset points", xytext=(9, 4),
                    fontsize=11, weight="bold" if n in ("z'", "I+z'") else "normal")
    ax.set_xscale("log"); ax.set_yscale("log")
    ax.set_xlabel("water the band ABSORBS   (µmag per mm of PWV)")
    ax.set_ylabel("water that SURVIVES the ratio   (µmag per mm)")
    ax.set_title("Absorbing water and being hurt by it are different rankings", weight="bold")
    lo = min(min(ab), min(di)) * 0.5
    hi = max(max(ab), max(di)) * 2
    ax.plot([lo, hi], [lo, hi], "--", color="#999", lw=1, zorder=1)
    ax.annotate("if the two were the same\nthe points would sit here",
                xy=(hi * 0.35, hi * 0.35), fontsize=8.5, color="#777", ha="right")
    ax.set_xlim(lo, hi); ax.set_ylim(lo, hi)
    ax.annotate("z' absorbs MORE than I+z'\nand costs HALF as much",
                xy=(sl["z'"][0], sl["z'"][1]), xytext=(-140, -40),
                textcoords="offset points", fontsize=9, color="#c0392b",
                arrowprops=dict(arrowstyle="->", color="#c0392b", lw=1.2))
    fig.tight_layout()
    p = os.path.join(out, "1_absorbed_vs_differential.png")
    fig.savefig(p); plt.close(fig)
    return p


def fig_budget(sl, floors, depths, out, sigma_mm, note):
    """Water against the photon noise against the thing you are trying to detect."""
    names = [n for n, _, _ in BANDS]
    water = [ppm(sl[n][1] * sigma_mm) for n in names]
    y = list(range(len(names)))[::-1]
    fig, ax = plt.subplots(figsize=(8.4, 5.4))
    ax.barh(y, water, height=0.5, color="#1f4e9c", zorder=3, label="water error")
    for yy, n in zip(y, names):
        f = floors.get(n)
        if f:
            ax.plot([f, f], [yy - 0.34, yy + 0.34], color="#444", lw=2.4, zorder=4)
        else:
            # AFTER the bar, not on it: white-on-blue at 8 pt is not text, it is texture.
            ax.annotate("  no NIR detector modelled — floor unknown",
                        (ppm(sl[n][1] * sigma_mm), yy), fontsize=8, color="#777",
                        va="center", style="italic")
    for d, lab, c in depths:
        ax.axvline(d, color="#c0392b", ls="--", lw=1.1, zorder=2)
        ax.annotate(lab, (d, len(names) - 0.35), rotation=90, fontsize=8,
                    color="#c0392b", ha="right", va="top")
    ax.plot([], [], color="#444", lw=2.4, label="photon noise, 30-min bin")
    ax.plot([], [], color="#c0392b", ls="--", lw=1.1, label="real transit depths")
    ax.set_xscale("log"); ax.set_xlim(1, 40000)
    ax.set_yticks(y); ax.set_yticklabels(names, fontsize=11)
    ax.set_xlabel("parts per million of flux")
    ax.set_title("Is water the thing that limits you?", weight="bold")
    fig.text(0.012, 0.015, note, fontsize=8.2, color="#666", wrap=True)
    ax.legend(loc="lower right", frameon=False, fontsize=9)
    fig.tight_layout(rect=(0, 0.055, 1, 1))
    p = os.path.join(out, "2_water_vs_noise_vs_depth.png")
    fig.savefig(p); plt.close(fig)
    return p


def fig_transfer(transfer, out, floor_ppm):
    """How much of the perturbation reaches the FITTED depth, against its timescale."""
    rows = transfer["curve"]
    xs = [r["periodH"] for r in rows]
    ys = [r["ppmPerMm"] for r in rows]
    fig, ax = plt.subplots(figsize=(8.2, 5.0))
    ax.plot(xs, ys, color="#1f4e9c", lw=2.2, zorder=3)
    ax.axvline(transfer["durationH"], color="#888", ls=":", lw=1.4)
    ax.axvline(transfer["windowH"], color="#c0392b", ls="--", lw=1.4)
    ax.annotate(" transit lasts this long", (transfer["durationH"], min(ys) * 1.25),
                fontsize=8.5, color="#666", ha="left", rotation=90, va="bottom")
    ax.annotate(" whole visit lasts this long", (transfer["windowH"], min(ys) * 1.25),
                fontsize=8.5, color="#c0392b", ha="left", rotation=90, va="bottom")
    if floor_ppm:
        ax.axhline(floor_ppm, color="#2e7d32", lw=1.6)
        ax.annotate(f"photon noise, {floor_ppm:,.0f} ppm", (xs[-1], floor_ppm * 1.15),
                    fontsize=8.5, color="#2e7d32", ha="right")
    ax.set_xscale("log"); ax.set_yscale("log")
    ax.set_xlabel("how fast the water column is changing   (hours per cycle)")
    ax.set_ylabel("error on the MEASURED depth   (ppm per mm of PWV)")
    ax.set_title("Almost none of the water error reaches the answer", weight="bold")
    ax.annotate("slow drift:\nthe baseline fit eats it", (28, 90),
                fontsize=9, color="#2e7d32", ha="center")
    ax.annotate("fast wobble:\nit averages out", (0.28, 14), fontsize=9, color="#2e7d32", ha="center")
    ax.annotate("only here does it hurt", (2.4, max(ys) * 0.55), fontsize=9.5,
                color="#c0392b", ha="center", weight="bold")
    fig.tight_layout()
    p = os.path.join(out, "3_what_reaches_the_depth.png")
    fig.savefig(p); plt.close(fig)
    return p


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5228)
    ap.add_argument("--out", default="figures")
    ap.add_argument("--airmass", type=float, default=1.2)
    ap.add_argument("--target-teff", type=float, default=2600.0)
    ap.add_argument("--comp-teff", type=float, default=5800.0)
    ap.add_argument("--sigma-mm", type=float, default=0.53)
    ap.add_argument("--transfer", default=None, help="json from tools/pwv_transit_bias.py --json")
    a = ap.parse_args()
    base = f"http://127.0.0.1:{a.port}"
    os.makedirs(a.out, exist_ok=True)

    sl = slopes(base, a.target_teff, a.comp_teff, a.airmass)
    print(f"{'band':6s} {'absorbed':>12s} {'differential':>14s}")
    for n, _, _ in BANDS:
        print(f"{n:6s} {sl[n][0]:9.0f} u/mm {sl[n][1]:11.0f} u/mm")

    floors = {"g'": 8063, "r'": 2361, "i'": 1405, "z'": 1107, "I+z'": 889}
    depths = [(2949, "shallowest real transit", "#c0392b"),
              (6920, "TRAPPIST-1 b", "#c0392b"),
              (14831, "deepest", "#c0392b")]
    print(fig_absorbed_vs_differential(sl, a.out))
    print(fig_budget(sl, floors, depths, a.out, a.sigma_mm,
                     f"1 m at Paranal, V=18.8 ultracool target vs a V=12 comparison, 60 s, "
                     f"airmass {a.airmass}, binned to 30 min. Water shown for a {a.sigma_mm} mm error."))
    if a.transfer:
        print(fig_transfer(json.load(open(a.transfer)), a.out, 889))


if __name__ == "__main__":
    main()
