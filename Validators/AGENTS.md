# Login identity validation

The public login model accepts exactly one meaningful identity: a nonempty stable user UUID
or a valid email. A present `Guid.Empty` is always invalid, including when email is also given;
the manager treats every non-null UUID as the selected identity. Blank email is absent on
the UUID path, consistent with the manager's exclusivity check. Email-only login and the
existing nonempty-password policy remain unchanged. Claims, ownership, active-user checks,
password verification and exact login-cut issuance stay in their existing auth services.

The validator matrix exercises this public DTO contract. Real hosted browser journeys cover
claim-commit/lost-reply UUID login recovery and fresh owner login for consent renewal, so a
unit-only manager test cannot conceal an HTTP validation mismatch again.
