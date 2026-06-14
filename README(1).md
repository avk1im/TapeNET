**TapeNET README**

Copyright (c) 2023-2025 by [avk1im](https://github.com/avk1im)

*All third-party brand names, trademarks, and registered trademarks are the property of their respective owners. Their use here does not imply any endorsement, affiliation, or sponsorship by the owners.*


# TapeNET Introduction

TapeNET is a free, open-source software package for backing up files to -- and
restoring them from -- tape drives on Microsoft* Windows* and .NET*. It spans
the whole spectrum of tape hardware: from the charmingly inexpensive
USB-connectable drives on a hobbyist's desk to the cutting-edge LTO* libraries
humming in a data center.

TapeNET features include:

* Support for popular USB-connectable tape drives -- including Sony* AIT*,
  DAT 320 (DDS7), and DLT* VS1 -- as well as modern LTO drives
* Flexible file selection: directly or with wildcards, from multiple
  directories, optionally including subfolders
* A powerful filtering language (**FCL** -- File Conditions Language) for
  selecting files by name, path, size, date, and attributes -- with optional
  AI-assisted natural-language translation
* Optional data-integrity protection using hashing algorithms such as Crc32 or
  Crc64, with **Validate** and **Verify** operations
* Incremental backups: capturing only the files that changed since the last
  backup, with chain-aware restore
* Multi-volume backups: spanning large backup sets across several tape volumes
* Virtual drives: file- or RAM-backed tape emulation, so you can try every
  workflow without any hardware
* Remote operation: driving a tape drive hosted on another machine over the
  network, with optional TLS


# TapeNET Content

TapeNET currently includes:

* **TapeLibNET** -- the core tape I/O library: drives, agents, table of
  contents, streams, and serialization
* **FclNET** -- the File Conditions Language for flexible file filtering
* **FclAiNET** -- AI-assisted natural-language to FCL translation
* **TapeConNET** (`tapecon`) -- a full-featured command-line backup utility for
  Windows 10 and 11
* **TapeWinNET** -- a GUI tape backup manager for Windows: tree-based
  navigation, a structured log pane, FCL filtering, and an integrated help
  system
* **TapeServiceNET** -- a Windows Service / console host that exposes a tape
  drive over the network via gRPC, enabling remote backups

Both `tapecon` and `TapeWinNET` are full-featured backup utilities that also
illustrate the usage of the underlying libraries. For more information on using
`tapecon`, refer to the tapecon.pdf User Guide; for `TapeWinNET`, press **F1**
anywhere in the app to open its built-in help.

**CAUTION**: When backing up important data, it's advisable to follow best
backup practices -- employing multiple backup methods, not relying solely on any
single tool, and verifying or validating your backups regularly.


# Why back up to tape?

In an age of instant cloud sync and terabyte SSDs, reaching for a tape drive
can feel charmingly out of step with the times. And yet -- tape endures, not
out of nostalgia alone, but because it still does some things better than
anything else.

## A little rustic charm

The popular USB-connectable tape drives -- Sony AIT, DAT 320 (DDS7), DLT VS1 --
and the cassettes and cartridges that feed them have become wonderfully
inexpensive, precisely *because* the world assumed they were obsolete. Many
people view them as relics. But there is a certain rustic charm in the quiet
whir of a tape spinning up -- a tactile, deliberate ritual of preservation in a
very modern software environment. You can *hold* your backup in your hand and
put it on a shelf.

Portable hard drives and solid-state drives (SSDs) certainly beat tape on raw
capacity and read/write speed. Even so, the humble tape drive holds real
advantages:

* **Lower cost per capacity** -- media prices have fallen dramatically
* **Multi-volume support** for large backup sets, spreading a backup across
  several inexpensive cartridges
* **Long shelf life** and **WORM** (write once, read many) capability
* **Near-total protection from viruses and ransomware** -- malware cannot reach
  even a *loaded* tape, let alone one stored on a shelf

## The cutting edge: modern LTO

Tape is not only a nostalgia act. At the high end, **LTO (Linear Tape-Open)** is
one of the most advanced storage technologies in active development:

* **Enormous capacity** -- modern LTO generations store tens of terabytes of
  *compressed* data on a single cartridge, with the roadmap reaching into the
  hundreds
* **Blistering streaming throughput** -- fed a steady stream of data, an LTO
  drive sustains transfer rates that rival or exceed fast disks
* **Hardware encryption and WORM** built into the drive, for compliance-grade
  archives
* **The true air-gap** -- an ejected cartridge is physically disconnected: no
  network, no remote attacker, no accidental deletion can touch it. This is why
  hyperscale data centers and film studios still trust tape for their
  cold-storage and disaster-recovery tiers

The popular USB-connectable drives are fully supported by Windows 10 and
Windows 11, including drivers through the standard Windows distribution and/or
Windows Update. Yet the selection of backup software that can actually use them
has been modest: most contemporary applications either ignore tape entirely or
only speak to expensive professional LTO* systems. **TapeNET closes that gap**
-- a free, open-source backup application that drives both the inexpensive
USB-connectable drives *and* modern LTO hardware.


# License

TapeNET is free, open-source software distributed under the MIT license. Refer
to the license file LICENSE.txt for more information.

## Redistribution of Microsoft DLLs

This software includes the following Microsoft DLLs as dependencies:
- Microsoft.Extensions.*.dll
- System.IO.Hashing.dll

These DLLs are part of the .NET runtime and libraries, which are covered by the .NET Library License. You have the right to redistribute these files as part of this software, provided that you comply with the terms of the .NET Library License.

For more information, please refer to the [Microsoft .NET Library License](https://dotnet.microsoft.com/en/dotnet_library_license.htm).