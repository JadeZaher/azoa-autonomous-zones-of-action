namespace AZOA.WebAPI.Models.Responses;

/// <summary>A login challenge; the client signs the returned strings byte-for-byte.</summary>
public sealed record NodeAdminChallengeResponse(string ChallengeId, string Nonce, string ExpiresAt, string Audience);

/// <summary>An issued node-admin session.</summary>
public sealed record NodeAdminSessionResponse(
    string AccessToken, string ExpiresAt, string Handle, string Principal, IReadOnlyList<string> Permissions, NodeAdminView? Admin);

/// <summary>Contract schema <c>Admin</c>.</summary>
public sealed record NodeAdminView(
    string Id,
    string Username,
    string DisplayName,
    string Status,
    IReadOnlyList<NodeAdminRoleView> Roles,
    IReadOnlyList<string> Permissions,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<NodeAdminKeyView> Keys,
    bool MfaEnrolled);

/// <summary>Contract schema <c>Key</c>.</summary>
public sealed record NodeAdminKeyView(string Fingerprint, string Label, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>Contract schema <c>Role</c>.</summary>
public sealed record NodeAdminRoleView(string Id, string Name, bool BuiltIn, IReadOnlyList<string> Permissions, long Revision);

/// <summary>Contract schema <c>Session</c>; <c>handle</c> is an opaque id that is never the token's sid (M-1).</summary>
public sealed record NodeAdminSessionView(
    string Handle, string Principal, DateTimeOffset AuthTime, IReadOnlyList<string> Amr, DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt, string Ip, bool Current);

/// <summary>Contract schema <c>AuditRow</c>.</summary>
public sealed record NodeAdminAuditRowView(
    long Seq, string At, string Actor, IReadOnlyList<string> Amr, string Action, string Target, string Outcome,
    string Detail, string RequestId, string PrevHash, string Hash);

/// <summary>Chain tail.</summary>
public sealed record NodeAdminAuditHeadView(long Seq, string Hash);

/// <summary><c>GET /audit</c> page.</summary>
public sealed record NodeAdminAuditPageView(IReadOnlyList<NodeAdminAuditRowView> Rows, NodeAdminAuditHeadView Head);

/// <summary><c>GET /audit/verify</c>: the window verified is <c>fromSeq..verifiedThroughSeq</c>.</summary>
public sealed record NodeAdminAuditVerifyView(bool Ok, long FromSeq, long VerifiedThroughSeq, string HeadHash, long? FirstBrokenSeq);

/// <summary>Root identity in <c>GET /me</c>.</summary>
public sealed record NodeAdminRootView(string Fingerprint);

/// <summary><c>GET /me</c>.</summary>
public sealed record NodeAdminMeView(
    string Principal, NodeAdminView? Admin, NodeAdminRootView? Root, IReadOnlyList<string> Permissions, NodeAdminSessionView Session);

/// <summary>
/// <c>POST /admins/{id}/mfa/reset</c> and <c>PUT /admins/{id}/roles</c>: the admin after the change and, when it had to
/// re-enroll, a fresh enrollment token returned only to root (C1); null otherwise.
/// </summary>
public sealed record NodeAdminReenrollmentView(NodeAdminView Admin, string? EnrollmentToken, DateTimeOffset? EnrollmentExpiresAt);

/// <summary><c>POST /admins</c>.</summary>
public sealed record NodeAdminCreatedView(NodeAdminView Admin, string EnrollmentToken, DateTimeOffset EnrollmentExpiresAt);

/// <summary><c>POST /enroll/start</c>; the TOTP secret is shown once.</summary>
public sealed record NodeAdminEnrollStartView(string EnrollmentId, string OtpauthUri, string TotpSecretBase32, string Audience, string Fingerprint);

/// <summary><c>POST /enroll/complete</c>: the admin now awaits activation by fingerprint.</summary>
public sealed record NodeAdminEnrollCompleteView(string AdminId, string Status, string Fingerprint);
