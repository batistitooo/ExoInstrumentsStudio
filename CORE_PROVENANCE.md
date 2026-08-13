# Where the physics core came from

`Core/`, `Session/` and `Visualization/FitsWriter.cs` are not written here. They are the
[ExoInstruments](https://github.com/batistitooo/ExoInstruments) KSP mod's physics, copied into
this repository so that it builds with nothing else on the machine.

This file records what was copied, from where, and what that costs.

## The copy

| | |
|---|---|
| Source | `ExoInstruments/ExoInstruments/` |
| Commit | `8ef0b50` *"Land the accumulated working tree: PSF cost, sky chart, ground ops, career balance"* |
| Taken on | 2026-08-13 |
| Files | 122 under `Core/`, 3 under `Session/`, 1 from `Visualization/` |
| Lines | 36,569 |

**The working tree was not clean when this was taken, and the copy includes the uncommitted
work.** That is worth stating rather than hiding, because `8ef0b50` alone does not reproduce this
tree. Eleven files were modified or untracked at the time:

```
 M Core/EmissionLines.cs          M Core/StarFieldRenderer.cs
 M Core/EmissionPatchSet.cs       M Core/StarTarget.cs
 M Core/ExoplanetCSVLoader.cs     M Core/SystemBandpass.cs
 M Core/GalaxyCatalog.cs          M Visualization/FitsWriter.cs
 M Core/RenderedStarCatalog.cs   ?? Core/Supernovae.cs
 M Core/ScienceRewards.cs        ?? Core/SupernovaTemplateSet.cs
```

Two of those are new files that exist nowhere in the mod's history yet. If the mod's tree is ever
reverted, this copy is the only place some of that work survives.

## What is deliberately not copied

`Core/SkyChartTexture.cs`, the one file in `Core` that genuinely uses `UnityEngine`. There is no
Unity here. The rest of Unity's dialect (`Color`, `Mathf`, `Texture2D` and friends) is supplied by
`Engine/Simulation/UnityShims.cs`, which is why the remaining 122 files compile verbatim.

## The cost, and what is done about it

The mod's build file used to compile this tree **in place**, and said why in as many words:

> A copy would drift within a week and the galsim cross-validation would then only cover one of
> the copies.

That reasoning was correct, and copying does not make it wrong. Two independent copies of 36,569
lines of physics will diverge, and a cross-validation that runs against one of them proves nothing
about the other.

What replaces the old guarantee is a check:

```bash
python3 tools/check_core_drift.py --mod /path/to/ExoInstruments/ExoInstruments
```

It hashes every vendored file against the mod's and names each one that differs, is missing, or is
new on either side. Exit 0 means identical, 1 means diverged, 2 means it could not find a mod
checkout to compare against, which is not the same thing as agreement.

**Divergence is allowed.** The mod moves with KSP and Studio moves with what a headless server
needs; a fix can reasonably land in one before the other. What is not allowed is divergence nobody
noticed. When the check reports a difference, the answer is one of three: re-vendor from the mod,
port the change back the other way, or record the fork here with its reason.

### Forks recorded so far

None. As of 2026-08-13 the check reports 126 of 126 files identical.
