# Public login validator regression

The identity/password matrix covers the HTTP model validator that runs before UserManager.
It includes stable UUID login, unchanged email login, missing/malformed identities, dual
identities, Guid.Empty with and without valid email, and both password paths. This is the
boundary missed by manager-only tests. End-to-end hosted journeys retain real HTTP login
after a committed claim and verify recovery never starts a second claim.
