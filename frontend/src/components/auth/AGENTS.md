# Member handoff

The claim URL keeps the token only in its fragment. Capture and scrub it before a request;
do not put it in browser storage, logs, query parameters, or error messages. A refresh before
claim needs the original invitation or resend. An uncertain claim response recovers with
the same password and stable account reference through ordinary user-id login.

A successful login already on `/consent` reads the saved descriptor with that fresh session
and updates the current component. Assigning the same pathname with a new fragment is only
a same-document navigation; it does not remount the guarded initial read. The separate
`/claim` to `/consent` transition can retain its full navigation. No test forces a reload or
substitutes an authenticated descriptor for the actual server read.

Claiming never implies permission. Consent uses the exact persisted node descriptor and
first-party session after ownership revocations commit. An in-flight approval retries the
same command; decline resolves the pending command through server withdrawal or revokes its original grant. Stripe setup runs with the
member's first-party session, never tenant authority. The platform return is an explicit link
after consent; unavailable bank setup never claims cash-out is ready.

Decline describes this request only; prior grants can remain active. Invitation expiry
prevents new approval, while an approving request can replay a committed receipt or use
Decline to resolve its command. Show the invitation deadline and a return link for a fresh request.
