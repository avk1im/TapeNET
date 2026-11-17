# Encryption

`--encrypt` encrypts a new backup set with AES-256-GCM: its files **and** its TOC entry, meaning names, sizes, times and
the file count. Only what navigation needs stays visible: that the set exists, where it is, and how large it is.

    tapecon backup D:\Data --subdirs --encrypt

tapecon asks for the password twice, masked.

## Passwords in scripts

Pass the password through a file or an environment variable, never on the command line:

- `--password-file FILE` reads the file as UTF-8. One trailing line break is ignored; anything else, a trailing space
  included, is part of the password.
- `--password-env VARIABLE` reads the named environment variable.

A `--password` option does not exist on purpose: the shell history and the process list would show it.

In PowerShell, set the variable with `$env:`, and use single quotes so `$` signs stay literal:

    $env:TAPE_PW = 'correct horse battery staple'
    tapecon backup D:\Data --subdirs --encrypt --password-env TAPE_PW

In cmd, use `set TAPE_PW=...`.

## Locked sets

Without its password, an encrypted set shows as **[Locked]** with its size, and nothing else. You can still delete or
overwrite it, and every other set stays fully usable.

- `restore`, `validate`, `verify` and `list` ask for the password. A set that stays locked is skipped, and tapecon
  ends with exit code 6.
- `info` never asks. With `--password-file` or `--password-env`, it shows the sets the password opens.

## Incremental chains

A chain stays in one encryption. An incremental on an encrypted set is encrypted with the same key and needs that
set's password. A plain incremental on an encrypted set, or the reverse, is refused.

## Changing the password

    tapecon rename-set 2 --change-password

tapecon asks for the new password twice. Unattended, give it with `--new-password-file` or `--new-password-env`.
Older TOC exports, and other volumes' TOC copies, keep the old password.

## Encryption and compression

Encrypted data does not compress, so software encryption rules out the drive's **hardware** compression. Software
compression works: it compresses **before** encrypting (`--compression medium`, the default). Drive encryption
(`--encryption hardware`) is not supported yet.

## Keep a spare TOC

**A lost password cannot be recovered**, by tapecon or anyone else. And an encrypted set's files can only be found
through the TOC, so keep an exported TOC of important cartridges (`tapecon export-toc`). An export of an encrypted set
stays encrypted.
