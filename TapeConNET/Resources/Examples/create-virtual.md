## Examples

Create a virtual tape: the file `C:\Tapes\first.vt`, with 500 MiB of room:

    tapecon create-virtual C:\Tapes\first

Create a larger one, with a description:

    tapecon create-virtual C:\Tapes\big --capacity 20GB --mdesc "Big tape"

Add a **TOC partition** of 16 MiB, in the file `C:\Tapes\first_init.vt`:

    tapecon create-virtual C:\Tapes\first --init-capacity 16MB

Replace an existing virtual tape without asking:

    tapecon create-virtual C:\Tapes\first --overwrite-virtual

Then back up to it:

    tapecon backup D:\Data --subdirs --virtual-open C:\Tapes\first
