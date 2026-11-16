## Examples

Show the drive, the media and an overview of its sets:

    tapecon info

Show the drive only; no media needed:

    tapecon info --drive-only

Show the drive and the media, without reading the TOC:

    tapecon info --media

Show the encrypted sets, too. `info` never asks for a password, but uses one you give:

    tapecon info --password-env TAPE_PW

Show a virtual tape:

    tapecon info --virtual-open C:\Tapes\first
