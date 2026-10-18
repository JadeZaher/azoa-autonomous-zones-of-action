-- user-discoverable (owner decision 2026-10-18): POST /api/search no longer enumerates every user to
-- any signed-in caller — a user is a hit only if they opted in via this flag, are the caller
-- themself, or the caller is a node admin. `user` already has rows on a deployed database (round-4
-- generation reset predates this change), so the new field reaches it only through this forward
-- DEFINE FIELD OVERWRITE; see ../AGENTS.md §deploy-path and ../CompatibilityBaselines/user.surql.
DEFINE FIELD OVERWRITE discoverable ON TABLE user TYPE bool DEFAULT ALWAYS false;

-- Old-row rule (../AGENTS.md §old-row-rule): DEFAULT ALWAYS only fills NONE on the NEXT write, so a
-- row untouched since this migration still reads NONE. Materialize the default once; a no-op on a
-- fresh database.
UPDATE user SET discoverable = discoverable ?? false WHERE discoverable = NONE;
