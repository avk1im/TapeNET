**TapeNET README file**

Revision 1.0 July 2024
Copyright © 2023, 2024 by [avkl1m](https://github.com/avk1im)
*All third-party brand names, trademarks, and registered trademarks are the property of their respective owners. Their use here does not imply any endorsement, affiliation, or sponsorship by the owners.


# TapeNET Introduction

TapeNET is a software package that allows to back up files to and restore from a tape drive. TapeNET features include:

* Support of the popular USB-connectable tape drives, including Sony* AIT*, DAT 320 (DDS7), and DLT* VS1
* Flexible ways to specify files to back up: directly or with wildcards, from multiple directories, optionally including subdirectories
* Optional data integrity protection of backed-up files using hashing algorithms such as Crc32 or Crc64
* Incremental backups: backing up only the files that have changes since the last backup
* Support for multi-volume backups: using multiple tape volumes to accommodate large backup sets.


# TapeNET Content

Currently TapeNET includes:
* *tapelib* library for using tape drives under Microsoft* Windows* and .NET*, and
* *tapecon* command line application for Microsoft Windows. 

*tapecon* is a full-featured backup utility that also illustrates the usage of *tapelib* library. *tapecon* can run under Microsoft Windows 10 and 11. For more information on using *tapecon*, refer to tapecon.pdf User Guide.

TapeNET is free, open-source software distributed under MIT license. Refer to the license file LICENSE.txt for more information.

**CAUTION**: When using *tapecon*, it’s advisable to follow best backup practices, including employing multiple backup methods, not relying solely on this tool, and verifying or validating the backups regularly.


# Why back up to tape drives?

The popular USB-connectable tape drives, such as Sony AIT, DAT 320 (DDS7), or DLT VS1, as well as the tape media for them (cassettes or cartridges), have become extremely inexpensive recently, due to low demand. Many potential users view them as outdated or even not supported anymore.

While portable hard drives and solid-state drives (SSDs) certainly exceed the tape drives in both capacity and data read-write rates, the tape drives still offer certain advantages:

* Lower cost per capacity, since the tape media prices for these drives have come down dramatically
* Multi-volume support for large backup sets, compensating for smaller capacity per single volume
* Long shelf life and WORM (write once, read many times) capability of tape media
* Virtually full protection against viruses and ransomware, since the malicious software cannot access even the loaded tape media, leave alone separately stored tape media
* Certain rustic charm of using tape technology – in a very modern software environment!

The popular USB-connectable tape drives, such as Sony AIT, DAT 320 (DDS7), or DLT VS1, are fully supported by Windows 10 and Windows 11, including driver availability through the standard Windows distribution and/or from Windows Update.

However, the selection of the backup software that could use the drives has been rather modest. Most contemporary backup applications either do not support tape drives at all, or only work with the expensive professional LTO* tape systems. TapeNET and *tapecon* close this gap by providing a free, open-source backup application for tape drives.

