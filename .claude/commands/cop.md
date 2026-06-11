Run cop static analysis on this repository.

Execute the following command:
```
cop cop-checks/main.cop -t .
```

This runs all cop checks defined in `cop-checks/main.cop` against the repository root.
If there are violations, fix them before continuing.

If `cop-checks/` doesn't exist, tell the user they need to create cop check files first.
Run `cop help language` for the full language reference if you need to write or fix cop rules.