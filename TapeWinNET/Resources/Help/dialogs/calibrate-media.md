---
id: dialog.calibrate-media
title: Calibrate Media
kind: dialog
host: CalibrateWindow
keywords: [calibrate, calibration, capacity, scratch cartridge, early warning, EOM, profile]
intents:
  - "how do I calibrate a tape"
  - "measure the real capacity of a cartridge"
  - "why is reported free space wrong"
related:
  - dialog.calibration-result
  - dialog.calibration-profiles
ai_excerpt: true
---

# Calibrate Media

Calibration writes to a **scratch cartridge** until it is full, to measure the media's real capacity
and the early-warning (EW) → end-of-media (EOM) distance. The result is saved as a calibration profile.
**All content on the cartridge is destroyed.**

<!-- TODO: full content. -->

## Profile summary

Shows the profile that already exists for this drive and media type, if there is one.

## Operation and options

Choose the calibration operation and its options, such as ejecting the media when done or skipping the media-mismatch prompt.

## Confirmation

Tick the confirmation box to enable **Calibrate**.

## Controls

**Profile Summary** — The existing calibration profile for this drive and media type, if any.
**Operation** — Selects the calibration operation to run.
**Calibration options** — Options for the run, such as ejecting the media when done or not prompting on a media mismatch.
**Confirm calibration** — Confirms that the scratch cartridge will be erased. Required before you can start.
**Calibrate button** — Starts the calibration. Destroys all content on the cartridge.
