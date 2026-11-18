# TapeNET README

Copyright (c) 2023-2026 by [avk1im](https://github.com/avk1im)

_All third-party brand names, trademarks, and registered trademarks are the property of their respective owners. Their use here does not imply any endorsement, affiliation, or sponsorship by the owners._

## TapeNET Introduction

TapeNET is a free, open-source software package for backing up files to -- and
restoring them from -- tape drives on Microsoft\* Windows\* and .NET\*. It spans
the whole spectrum of tape hardware: from the charmingly inexpensive
USB-connectable drives to the cutting-edge SAS-connected LTO\* devices.

### TapeNET features include:

- Support for the modern LTO drives - as well as the popular pre-LTO tape drives
  including Sony\* AIT\*, DAT 160/320 (DDS6/DDS7), and DLT\* VS1
- Flexible file selection: directly, with wildcards, or with a file
  selection language (FCL, s. below), from multiple directories, optionally
  including subfolders
- A powerful filtering language (**FCL** - File Conditions Language) for
  selecting files by name, path, size, date, and attributes, with optional
  AI-assisted natural-language translation
- Optional data-integrity protection using hashing algorithms such as Crc32 or
  Crc64, with **Validate** and **Verify** operations
- Data compression and encryption using either the tape drive's built-in capabilities
  or software-based methods
- Incremental backups: capturing only the files that changed since the last
  backup, with chain-aware restore
- Multi-volume backups: spanning large backup sets across several tape volumes
- Media and drive calibration to maximize the media space capacity usage.
- Comprehensive Scan Media mechanism to survey a tape and recover the data and a lost table of contents
- Virtual drives: file- or RAM-backed tape emulation, so you can try every
  workflow without any hardware
- Remote operation: driving a tape drive hosted on another machine over the
  network, with optional TLS
- Comprehensive integrated help system, including AI-assisted natural-language search and
  question-answering

## TapeNET Content

TapeNET currently includes:

- **TapeConNET** (tapecon) -- a full-featured command-line backup utility for
  Windows 10 and 11
- **TapeWinNET** (TapeWin) -- a GUI tape backup manager for Windows: tree-based
  navigation, media space usage visualization, a structured log pane, FCL filtering,
  Scan Media for tapes with a  missing or damaged TOC, and an integrated AI-assisted help system
- **TapeServiceNET** (tapesvc) -- a Windows Service / console host that exposes
  a tape drive over the network via gRPC, enabling remote backups

The libraries installed allong:

- **TapeLibNET** -- the core tape I/O library: drives, agents, table of
  contents, streams, and serialization
- **FclNET** -- the File Conditions Language for flexible file filtering
- **FclAiNET** -- AI-assisted natural-language to FCL translation
- **HelpNET** -- AI-assisted interactive help system
- **AiNET** -- the underlying AI assistant library, with support for multiple
  AI providers and models

Both tapecon and TapeWin are full-featured backup utilities that also
illustrate the usage of the underlying libraries. For more information on using
tapecon, start it with `help` command, or browse its built-in topics with `tapecon docs`;
for TapeWin, press **F1** anywhere in the app to open its built-in interactive help.

**CAUTION**: When backing up important data, it's advisable to follow best
backup practices -- employing multiple backup methods, not relying solely on any
single tool, and verifying or validating your backups regularly.

## Installing TapeNET

TapeNET comes as two Windows installers (Windows 10/11 or Windows Server 2016+, x64). Both
bundle the .NET runtime: no separate .NET installation needed.

- **TapeNET-\<version\>-setup.exe** -- tapecon and TapeWin, with tapesvc as an optional
  component for machines that serve their tape drives to others as well.
- **tapesvc-\<version\>-setup.exe** -- the remote tape service alone, for servers.

**Physical tape drives require administrator rights.** Use the **TapeWin (Administrator)** and
**tapecon prompt (Administrator)** Start Menu shortcuts for physical drives; virtual drives and
remote hosts work without elevation.

Building the installers from source: see [packaging/README.md](packaging/README.md).

## Why back up to tape?

In an age of instant cloud sync and terabyte SSDs, reaching for a tape drive
can feel charmingly out of step with the times. And yet -- tape endures, not
out of nostalgia alone, but because it still does some things better than
anything else.

## A little rustic charm -- at a compelling price point

The popular USB-connectable tape drives -- Sony AIT, DAT 320 (DDS7), DLT VS1 --
and the cassettes and cartridges that feed them have become wonderfully
inexpensive, precisely _because_ the world assumed they were obsolete. Many
people view them as relics. But there is a certain rustic charm in the quiet
whir of a tape spinning up -- a tactile, deliberate ritual of preservation in a
very modern software environment. You can _hold_ your backup in your hand and
put it on a shelf.

Portable hard drives and solid-state drives (SSDs) certainly beat tape on raw
capacity and read/write speed. Even so, the humble tape drive holds real
advantages:

- **Lower cost per capacity** -- media prices have fallen dramatically
- **Multi-volume support** for large backup sets, spreading a backup across
  several inexpensive cartridges
- **Long shelf life** and **WORM** (write once, read many) capability
- **Near-total protection from viruses and ransomware** -- malware cannot reach
  even a _loaded_ tape, let alone one stored on a shelf

## The cutting edge: modern LTO, made affordable

Tape is not only a nostalgia act. At the high end, **LTO (Linear Tape-Open)** is
one of the most advanced storage technologies in active development:

- **Enormous, scalable capacity** -- modern LTO generations store tens of terabytes of
  _compressed_ data on a single cartridge, with the roadmap reaching into the
  hundreds
- **Blistering streaming throughput** -- fed a steady stream of data, an LTO
  drive sustains transfer rates that rival or exceed fast disks
- **Hardware compression** built into the drive, for yet higher capacity utilization
  and faster throughput
- **Hardware encryption and WORM capabilities** built into the drive,
  for compliance-grade archives
- **The true air-gap** -- an ejected cartridge is physically disconnected: no
  network, no remote attacker, no accidental deletion can touch it. This is why
  hyperscale data centers and film studios still trust tape for their
  cold-storage and disaster-recovery tiers

The popular USB-connectable drives are fully supported by Windows 10 and
Windows 11, including drivers through the standard Windows distribution and/or
Windows Update. Yet the selection of backup software that can actually use them
has been modest: most contemporary applications either ignore tape entirely or
only speak to expensive professional LTO\* systems. The software that can work
with LTO drives in turn tends to be highly expensive. **TapeNET closes that gap**
-- a free, open-source backup application that drives both the inexpensive
USB-connectable drives _and_ modern LTO hardware.

## License

TapeNET is free, open-source software distributed under the MIT license. Refer
to LICENSE.txt and THIRD-PARTY-NOTICES.md for more information.

## Redistribution of Microsoft components

The TapeNET installers ship self-contained builds: each app folder includes the .NET runtime
(and, for tapesvc, the ASP.NET Core runtime) together with Microsoft libraries such as
Microsoft.Extensions.\*.dll, System.CommandLine.dll, and System.IO.Hashing.dll. These components
are open source under the MIT license, Copyright (c) .NET Foundation and Contributors, and are
redistributed under its terms.

THIRD-PARTY-NOTICES.md lists every shipped third-party component. Each installed app also
carries a `licenses\` folder with the license and notice files of its packages and an exact
package inventory (`licenses\_packages.csv`).
