// SPDX-License-Identifier: UNLICENSED
// Binds a foreign key as a NATIVE record id for query parameters.

using System;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>
/// Parameter-side counterpart to <see cref="SurrealLink.ToLink"/>.
///
/// <para>
/// <see cref="SurrealLink.ToLink"/> produces the <c>table:id</c> <b>string</b>
/// that a POCO's FK property holds; SurrealForge's CBOR marshaller promotes such
/// a property to a native record id on write because the property carries
/// <c>[References]</c>. A query <em>parameter</em> has no attribute attached, so
/// nothing promotes it and it stays CBOR text.
/// </para>
///
/// <para>
/// That matters because a <c>TYPE record&lt;…&gt;</c> column now genuinely holds
/// a record link, and in SurrealQL a record link is never equal to a string:
/// <c>WHERE avatar_id = $_avatar</c> with a text parameter matches <b>nothing</b>
/// and returns an empty set with no error. Under the pre-1.0.0 JSON transport
/// the same comparison worked, because the column held a string too.
/// </para>
///
/// <para>
/// This is not a SurrealForge defect — the package supplies
/// <see cref="SurrealRecordId"/> precisely for this, and a marshaller cannot
/// know whether a bare string parameter is meant as a link or as text (a scope
/// token like <c>dapp:develop</c> is the counter-example). Every
/// <c>WithParam</c> that targets a record column goes through here.
/// </para>
/// </summary>
public static class SurrealRecordParam
{
    /// <summary>
    /// Bind <paramref name="id"/> as <c>table:id</c>. Returns <see langword="null"/>
    /// for a null/empty id, matching <see cref="SurrealLink.ToLink"/>, so an
    /// <c>option&lt;record&lt;…&gt;&gt;</c> comparison keeps its previous shape.
    /// An id that already carries a <c>table:</c> prefix is accepted and its own
    /// table wins, again matching <c>ToLink</c>.
    /// </summary>
    public static object? Of(string table, string? id)
    {
        if (string.IsNullOrEmpty(table)) throw new ArgumentException("table is required.", nameof(table));
        if (string.IsNullOrEmpty(id)) return null;

        var colon = id!.IndexOf(':');
        return colon >= 0
            ? SurrealRecordId.Create(id.Substring(0, colon), id.Substring(colon + 1))
            : SurrealRecordId.Create(table, id);
    }

    /// <summary>
    /// Bind a value that is already a <c>table:id</c> link -- typically an FK
    /// read straight off a POCO, where the table is carried by the value itself.
    /// A value with no <c>table:</c> prefix cannot address a record and is
    /// rejected rather than silently bound as text.
    /// </summary>
    public static object? OfLink(string? link)
    {
        if (string.IsNullOrEmpty(link)) return null;

        var colon = link!.IndexOf(':');
        if (colon <= 0 || colon >= link.Length - 1)
            throw new ArgumentException(
                $"'{link}' is not a table:id record link, so it cannot be bound to a " +
                "record column. Use SurrealRecordParam.Of(table, id) for a bare id.",
                nameof(link));

        return SurrealRecordId.Create(link.Substring(0, colon), link.Substring(colon + 1));
    }
}
