## Examples

Delete the latest set:

    tapecon delete-sets 0

Delete set 3 **and every set after it**:

    tapecon delete-sets 3

*Notice:* tapecon deletes from the given set through the **end** of the volume. A set in the middle cannot be removed
alone.
