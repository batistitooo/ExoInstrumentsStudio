#!/usr/bin/env python3
"""
Draw what tools/pwv_depth_from_frames.py measured. Reads its JSON, draws, computes nothing.

    python3 tools/pwv_depth_figure.py figures/depth_from_frames.json --out figures
"""
import argparse, json, math, os

import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

plt.rcParams.update({"figure.dpi": 130, "savefig.dpi": 130, "font.size": 9,
                     "axes.spines.top": False, "axes.spines.right": False})


def baseline(rows, regressors=("1", "t")):
    t0 = rows[0]["ut"]
    cols = {"1": np.ones(len(rows)),
            "t": np.array([(r["ut"] - t0) / 3600.0 for r in rows]),
            "x": np.array([r["airmass"] for r in rows])}
    A = np.column_stack([cols[k] for k in regressors])
    y = np.array([r["ratio"] for r in rows])
    inn = np.array([r["transitFactor"] < 0.99999 for r in rows])
    c, *_ = np.linalg.lstsq(A[~inn], y[~inn], rcond=None)
    return cols["t"], y, A @ c, inn, np.array([r["transitFactor"] for r in rows])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("json")
    ap.add_argument("--out", default="figures")
    ap.add_argument("--depth-ppm", type=float, default=6400.0)
    a = ap.parse_args()
    runs = json.load(open(a.json))
    os.makedirs(a.out, exist_ok=True)

    n = len(runs)
    fig, axes = plt.subplots(n, 1, figsize=(9.2, 2.35 * n), sharex=True,
                             gridspec_kw=dict(hspace=0.16))
    if n == 1:
        axes = [axes]
    for ax, r in zip(axes, runs):
        t, y, model, inn, tf = baseline(r["rows"])
        ax.plot(t[~inn], y[~inn], "o", ms=4.5, color="#666", label="hors transit")
        ax.plot(t[inn], y[inn], "o", ms=5.5, color="#c0392b", label="en transit")
        ax.plot(t, model, "-", color="#1f4e9c", lw=1.3, label="base ajustee hors transit")
        ax.plot(t, model * tf, "--", color="#c0392b", lw=1.3, label="base x profil injecte")
        ax.set_ylabel("cible / ensemble", fontsize=8.5)
        ax.grid(alpha=0.22)
        ax.annotate(f"{r['label']}   →   {r['depthPpm']:.0f} ± {r['errPpm']:.0f} ppm",
                    (0.012, 0.08), xycoords="axes fraction", fontsize=9.5, weight="bold")
        if r.get("pwvMin") is not None:
            ax2 = ax.twinx()
            ax2.plot(t, [q["pwvMm"] for q in r["rows"]], color="#2e7d32", lw=1.2, alpha=0.75)
            ax2.set_ylabel("PWV (mm)", color="#2e7d32", fontsize=8)
            ax2.tick_params(axis="y", colors="#2e7d32", labelsize=7.5)
            ax2.spines["top"].set_visible(False)
            ax.set_zorder(ax2.get_zorder() + 1); ax.patch.set_visible(False)
    axes[0].legend(frameon=False, fontsize=8, ncol=2, loc="upper right")
    axes[-1].set_xlabel("heures depuis la premiere pose")
    fig.suptitle(f"Quatre nuits identiques, {a.depth_ppm:.0f} ppm injectes dans les pixels. "
                 f"Seule la vapeur d'eau change.", y=0.998, fontsize=11, weight="bold")
    fig.tight_layout(rect=(0, 0, 1, 0.985))
    p1 = os.path.join(a.out, "12_lightcurves_from_frames.png")
    fig.savefig(p1); plt.close(fig)

    ref = runs[0]
    fig, ax = plt.subplots(figsize=(8.4, 3.4))
    labels = [r["label"] for r in runs[1:]]
    shift = [r["depthPpm"] - ref["depthPpm"] for r in runs[1:]]
    err = [math.hypot(r["errPpm"], ref["errPpm"]) for r in runs[1:]]
    y = list(range(len(labels)))[::-1]
    ax.barh(y, shift, height=0.5, zorder=3,
            color=["#c0392b" if abs(s) > e else "#1f4e9c" for s, e in zip(shift, err)])
    ax.errorbar(shift, y, xerr=err, fmt="none", ecolor="#222", capsize=4, lw=1.2, zorder=4)
    ax.axvline(0, color="#222", lw=1.2)
    ax.set_yticks(y); ax.set_yticklabels(labels, fontsize=10)
    ax.set_xlabel("decalage de la profondeur mesuree, contre le controle sec   (ppm)")
    ax.set_title("Ce que l'eau fait a la profondeur, mesure sur les frames", weight="bold")
    ax.grid(alpha=0.22, axis="x")
    ax.annotate("rouge : le decalage depasse sa barre d'erreur.   bleu : il ne la depasse pas.",
                (0.0, -0.32), xycoords="axes fraction", fontsize=8.5, color="#666")
    fig.tight_layout(rect=(0, 0.06, 1, 1))
    p2 = os.path.join(a.out, "13_depth_shift_from_frames.png")
    fig.savefig(p2); plt.close(fig)

    print(f"{p1}\n{p2}\n")
    print(f"{'condition':30s} {'profondeur':>12s} {'decalage':>12s}")
    for r in runs:
        s = r["depthPpm"] - ref["depthPpm"]
        print(f"{r['label']:30s} {r['depthPpm']:8.0f} ppm {s:+9.0f} ppm"
              f"   PWV {r['pwvMin']}..{r['pwvMax']}")


if __name__ == "__main__":
    main()
