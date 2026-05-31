---
id: concepts.remote-service
title: Remote Service
kind: concept
keywords: [remote, service, server, network, gRPC, TLS, session, remote drive]
intents:
  - "what is the remote service"
  - "use a tape drive on another machine"
  - "back up over the network"
  - "how does remote connection work"
---

# Remote Service

The **TapeNET remote service** lets a tape drive (or virtual tape) attached to
one machine be used from TapeWin running on another.  TapeWin connects to the
service over the network and then operates the remote drive as if it were
local.

## How it works

- The server machine runs the TapeNET service, listening on a port (default
  **50551**, or **50552** for TLS).
- TapeWin connects with [Connect to Remote Host](help://topic/dialog.connect-remote-host),
  establishing a **session** with the server.
- Once connected, you open a physical remote drive from the **Drive** menu, or
  a [remote virtual drive](help://topic/dialog.open-remote-virtual-drive).

## Sessions and keepalive

Each connection holds an independent session on the server, so multiple clients
can work with different drives at once.  TapeWin periodically pings the server
to keep an idle session alive.

## Security

The connection can be secured with **TLS**.  Beyond the session token, access
control is best handled at the firewall level — restrict which hosts may reach
the service port.

## Multi-volume over the network

Remote backup and restore support multi-volume operations: when a volume fills
or another is needed, TapeWin prompts you to choose or create the next remote
volume, just as it does locally.
