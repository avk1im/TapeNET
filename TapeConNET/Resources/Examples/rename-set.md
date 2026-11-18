## Examples

Rename set 2. Here the index counts from the **oldest** set: 1 is the first backup on the tape:

    tapecon rename-set 2 --name "Before the migration"

Let tapecon ask for the new description:

    tapecon rename-set 2

Change the password of encrypted set 2; tapecon asks for the old one, then twice for the new one:

    tapecon rename-set 2 --change-password

The same in a script, with both passwords from files:

    tapecon rename-set 2 --password-file old.txt --new-password-file new.txt
