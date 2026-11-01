---
id: dialog.calibration-result
title: Calibration Result
kind: dialog
host: CalibrationWindow
keywords: [calibration result, measured capacity, phantom free, early warning, EOM, save profile, apply profile]
intents:
  - "what do the calibration results mean"
  - "save a calibration profile"
  - "apply a calibration profile"
related:
  - dialog.calibrate-media
  - dialog.calibration-profiles
ai_excerpt: true
---

# Calibration Result

Shows what calibration measured. You can save the result as a profile and apply it to the
current media (single-partition only).

<!-- TODO: full content. -->

## Measured result

The profile key, the capacity reported at the beginning of media, the measured capacity, the phantom free space at EOM, and the EW landmark.

## Recalibration: before → after

Shown when an existing profile was recalibrated; compares the old and new values.

## Calibration curve

Plots actual remaining capacity against the capacity the drive reports. Left-click to select a region to magnify; right-click to reset to EW → EOM.

## Controls

**Calibration Result** — The full calibration result: measured values, recalibration delta, and curve.
**Measured Result** — The measured capacity figures and the EW → EOM landmark.
**Recalibration delta** — Before → after comparison when an existing profile was recalibrated.
**Calibration curve** — Actual against reported remaining capacity. Left-click to zoom into a region, right-click to reset.
