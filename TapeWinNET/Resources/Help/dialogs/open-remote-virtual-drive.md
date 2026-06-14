---
id: dialog.open-remote-virtual-drive
title: Open Remote Virtual Drive
kind: dialog
host: OpenRemoteVirtualDriveWindow
keywords: [remote, virtual drive, remote volume, server, named volume, in-memory, capacity]
intents:
  - "how do I open a remote virtual drive"
  - "use a virtual tape on another machine"
  - "create a remote volume"
  - "pick a server volume"
related:
  - concepts.remote-service
  - dialog.connect-remote-host
  - dialog.open-virtual-drive
ai_excerpt: true
---

# Open Remote Virtual Drive

Opens or creates a virtual tape **hosted by a remote TapeNET service** you are
connected to.  You must already be connected — see
[Connect to a remote host](help://topic/dialog.connect-remote-host).

The remote host you are working with is shown at the top of the dialog.

## Mode

- **Open existing remote volume** — choose a named volume that already exists
  on the server.  All configuration fields become read-only and are filled in
  from the selected volume.
- **Create new remote volume** — build a fresh virtual tape on the server with
  the settings below.

## Select Volume

*(Open mode)* A picker listing the named volumes available in your current
server session.  Choosing one populates the read-only details.

## Media Storage

*(Create mode)*

- **Named** — the server creates files for the volume under the supplied name,
  so it persists between sessions.
- **In-memory (no files created on the server)** — a throw-away volume held in
  the server's RAM.

## Media Description, Preset Configuration, Capacity, Features, Block Sizes

These work exactly as in the local
[Open Virtual Drive](help://topic/dialog.open-virtual-drive) dialog — a
**Preset** **Apply** fills in capacity, features (Setmarks / Seq. Filemarks,
Initiator Partition) and block sizes, which you can then adjust.

## Confirming

Click the confirm button (its label reflects **Open** or **Create**) to mount
the remote volume, or **Cancel** to abort.

> Shortcut: [Open a remote virtual drive](help://action/open-remote-virtual-drive).

## Controls

**Drive mode** — Chooses between opening an existing named volume on the remote host or creating a new one for this session.
**Select volume** — (Open existing mode) Drop-down list of named volumes available on the connected remote host.
**Media storage** — (Create mode) Named stores the media as a server-side file that persists between sessions; in-memory creates a temporary volume that is deleted when the drive closes.
