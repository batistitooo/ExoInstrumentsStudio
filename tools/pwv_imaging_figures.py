#!/usr/bin/env python3
"""
The water-vapour result, measured THROUGH THE IMAGING PATH.

Every number here comes from frames: photons deposited, digitised, reduced, joined into per-star
light curves, and a transit depth fitted back out. Nothing is read off the passband integral.

The design is PAIRED. Frame i of a sequence draws from `seed + i * 7919`, so two runs with the same
seed and different water differ by NOTHING BUT THE WATER. A single run cannot see the effect - the
per-point scatter is thousands of ppm and the water term is smaller - but the DIFFERENCE between
two runs can, because the photon noise is common to both and subtracts.

Input is the JSON written by the paired driver; this file only reads and draws.

    python3 tools/pwv_imaging_figures.py paired.json --out figures
"""
import argparse, json, os

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

plt.rcParams.update({
    "figure.dpi": 130, "savefig.dpi": 130, "font.size": 10,
    "axes.spines.top": False, "axes.spines.right": False,
    "axes.grid": True, "grid.alpha": 0.25, "grid.linewidth": 0.6,
})


def fig_lightcurves(runs, out, depth_ppm):
    """The measured light curves themselves, one panel per water condition."""
    n = len(runs)
    fig, axes = plt.subplots(n, 1, figsize=(8.6, 2.05 * n), sharex=True)
    if n == 1: axes = [axes]
    for ax, r in zip(axes, runs):
        s = r["series"]
        t0 = s[0]["ut"]
        t = [(p["ut"] - t0) / 3600.0 for p in s]
        y = [p["ratio"] for p in s]
        inn = [p["transitFactor"] < 0.99999 for p in s]
        ax.plot([ti for ti, m in zip(t, inn) if not m],
                [yi for yi, m in zip(y, inn) if not m], "o", ms=4, color="#666", label="out")
        ax.plot([ti for ti, m in zip(t, inn) if m],
                [yi for yi, m in zip(y, inn) if m], "o", ms=5, color="#c0392b", label="in transit")
        ax.set_ylabel("ratio", fontsize=9)
        ax.annotate(f"{r['label']}   →  {r['depthTimeAirmassPpm']:.0f} ppm recovered",
                    (0.01, 0.06), xycoords="axes fraction", fontsize=9, weight="bold")
        if r.get("pwvMin") is not None:
            ax.annotate(f"PWV {r['pwvMin']:.2f}–{r['pwvMax']:.2f} mm",
                        (0.99, 0.06), xycoords="axes fraction", fontsize=8.5,
                        color="#1f4e9c", ha="right")
    axes[0].legend(loc="upper right", frameon=False, fontsize=8.5, ncol=2)
    axes[-1].set_xlabel("hours since the first frame")
    fig.suptitle(f"Measured light curves: a {depth_ppm:.0f} ppm transit through real frames",
                 y=0.997, weight="bold")
    fig.tight_layout(rect=(0, 0, 1, 0.985))
    p = os.path.join(out, "4_measured_lightcurves.png")
    fig.savefig(p); plt.close(fig)
    return p


def fig_bias(runs, out, depth_ppm):
    """What the water did to the recovered depth, against the control with no water at all."""
    ref = next(r for r in runs if r["pwvMin"] is None)
    others = [r for r in runs if r is not ref]
    labels = [r["label"] for r in others]
    bias = [r["depthTimeAirmassPpm"] - ref["depthTimeAirmassPpm"] for r in others]
    err = [r["rmsPpm"] / max(1, r["inTransit"]) ** 0.5 for r in others]
    y = list(range(len(others)))[::-1]
    fig, ax = plt.subplots(figsize=(8.2, 3.6))
    ax.barh(y, bias, height=0.5, color=["#1f4e9c" if abs(b) < e else "#c0392b"
                                        for b, e in zip(bias, err)], zorder=3)
    ax.errorbar(bias, y, xerr=err, fmt="none", ecolor="#333", capsize=4, lw=1.2, zorder=4)
    ax.axvline(0, color="#333", lw=1.2)
    ax.set_yticks(y); ax.set_yticklabels(labels, fontsize=10)
    ax.set_xlabel("shift in the recovered transit depth caused by water   (ppm)")
    ax.set_title(f"What water did to a {depth_ppm:.0f} ppm transit, measured on frames",
                 weight="bold")
    ax.annotate("error bars are the out-of-transit scatter over the in-transit points;\n"
                "red means the shift exceeds it, blue means it does not",
                (0.0, -0.42), xycoords="axes fraction", fontsize=8.5, color="#666")
    fig.tight_layout(rect=(0, 0.09, 1, 1))
    p = os.path.join(out, "5_depth_bias_measured.png")
    fig.savefig(p); plt.close(fig)
    return p


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("paired")
    ap.add_argument("--out", default="figures")
    ap.add_argument("--depth-ppm", type=float, default=6400.0)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    runs = json.load(open(a.paired))
    print(fig_lightcurves(runs, a.out, a.depth_ppm))
    print(fig_bias(runs, a.out, a.depth_ppm))
    ref = next(r for r in runs if r["pwvMin"] is None)
    print(f"\n{'condition':22s} {'depth':>10s} {'shift':>10s} {'+/-':>8s}")
    for r in runs:
        d = r["depthTimeAirmassPpm"]
        e = r["rmsPpm"] / max(1, r["inTransit"]) ** 0.5
        print(f"{r['label']:22s} {d:8.0f} ppm {d - ref['depthTimeAirmassPpm']:+8.0f} ppm {e:7.0f}")


if __name__ == "__main__":
    main()
