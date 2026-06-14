---
id: dialog.connect-remote-host
title: Connect to Remote Host
kind: dialog
host: ConnectToRemoteHostWindow
keywords: [remote, connect, host, port, TLS, local host, server, network]
intents:
  - "how do I connect to a remote host"
  - "connect to a tape server"
  - "use a network tape drive"
  - "connect over TLS"
related:
  - concepts.remote-service
  - dialog.open-remote-virtual-drive
ai_excerpt: true
---

# Connect to Remote Host

Connects TapeWin to a remote **TapeNET service**, so you can use a tape (or
virtual tape) attached to another machine as if it were local.  See
[Remote service](help://topic/concepts.remote-service) for background.

## Host and Port

- **Host** — the server's name or IP address.
- **Port** — the service port (default **50551** for plaintext, **50552** for
  TLS).
- **Use local host (127.0.0.1)** — a shortcut that targets a service running on
  this same machine.

## Use secure connection (TLS)

Tick to connect over TLS.  When you toggle TLS, the port switches between the
plaintext and TLS defaults automatically (unless you have set a custom port).
The server must have TLS enabled for this to succeed.

## Connecting

Click **Connect**.  TapeWin probes the server and shows a progress indicator
while connecting; controls are disabled during the probe.  On success the
dialog closes and the remote host appears in the tree, status bar, and the
**Drive** menu.  Any connection error is shown inline.

Once connected, use
[Open Remote Virtual Drive](help://topic/dialog.open-remote-virtual-drive) or
the remote drive submenu to mount a drive.

> Shortcut: [Connect to remote host](help://action/connect-to-remote-host).

## Controls

**Host and port** — The network address (hostname or IP) and port number of the TapeWin [remote service](help://topic/concepts.remote-service). Default port is 7474.
**Connection options** — Use local host (127.0.0.1) for a service running on this machine; enable TLS for an encrypted connection.
**Connect button** — Attempts to connect to the specified host and port. On success the dialog closes and the drive tree updates.
