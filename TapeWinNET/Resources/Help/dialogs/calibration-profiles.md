---
id: dialog.calibration-profiles
title: Calibration Profiles
kind: dialog
host: CalibrationProfilesWindow
keywords: [calibration profiles, import, export, apply profile, remove profile, drive filter]
intents:
  - "manage calibration profiles"
  - "export calibration profiles"
  - "import a calibration profile"
related:
  - dialog.calibrate-media
  - dialog.calibration-result
ai_excerpt: true
---

# Calibration Profiles

Lists every calibration profile ever saved. Select one to review it, apply it to the currently
loaded media (single-partition only), or remove it. Profiles can also be imported and exported.

<!-- TODO: full content. -->

## Selecting a profile

**Only for this drive** limits the list to profiles for the current drive.

## Profile details

The selected profile is shown in the same layout as the [Calibration Result](help://topic/dialog.calibration-result).

## Controls

**Drive filter** — Shows only the profiles recorded for the current drive.
**Profile selector** — Picks the profile to review, apply, export or remove.
**Calibration Result** — Details of the selected profile: measured values and curve.
**Measured Result** — The measured capacity figures and the EW → EOM landmark.
**Recalibration delta** — Before → after comparison when the profile was recalibrated.
**Calibration curve** — Actual against reported remaining capacity. Left-click to zoom into a region, right-click to reset.
