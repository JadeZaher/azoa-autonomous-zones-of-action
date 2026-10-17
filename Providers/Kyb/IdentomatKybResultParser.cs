using System.Text.Json;
using System.Security.Cryptography;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyb;

namespace AZOA.WebAPI.Providers.Kyb;

/// <summary>Parses only authoritative KYB step results; incomplete or unknown states remain pending.</summary>
public static class IdentomatKybResultParser
{
    public static HostedSessionOutcome Parse(JsonElement root)
    {
        if (!Object(root, "result", out var result) || !Array(result, "stepsResults", out var steps)
            || steps.GetArrayLength() > 100 || root.TryGetProperty("argumentError", out _))
            return new HostedSessionOutcome { Outcome = "UNKNOWN" };
        var companySteps = new List<JsonElement>();
        var beneficiarySteps = new List<JsonElement>();
        var reviews = new List<JsonElement>();
        foreach (var step in steps.EnumerateArray())
        {
            switch (Text(step, "type"))
            {
                case "company": companySteps.Add(step); break;
                case "beneficiaries": beneficiarySteps.Add(step); break;
                case "kyb-review": reviews.Add(step); break;
            }
        }
        if (reviews.Count != 1) return Pending();
        if (Text(reviews[0], "status") == "rejected")
            return new HostedSessionOutcome { Outcome = "REJECTED", IsTerminal = true, SessionExists = true };
        if (Text(reviews[0], "status") != "approved" || companySteps.Count != 1 || beneficiarySteps.Count == 0
            || steps.EnumerateArray().Any(step => Text(step, "status") != "approved")) return Pending();
        if (!Object(companySteps[0], "result", out var company)) return Pending();
        var name = Text(company, "companyName");
        var number = Text(company, "registrationNumber");
        var country = Text(company, "registrationCountry");
        if (name is null || number is null || country is null) return Pending();
        var links = new List<KybBeneficiaryLink>();
        var participantIds = new HashSet<string>(StringComparer.Ordinal);
        var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in beneficiarySteps)
        {
            if (!Array(step, "result", out var people) || people.GetArrayLength() is 0 or > 1000) return Pending();
            foreach (var person in people.EnumerateArray())
            {
                var type = Text(person, "entityType");
                if (type is not ("individual" or "company") || !Sessions(person, out var sessions)
                    || sessions.Any(session => !sessionIds.Add(session))) return Pending();
                var id = Text(person, "_id") ?? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(sessions))));
                if (!participantIds.Add(id)) return Pending();
                IReadOnlyList<string> positions = [];
                if (person.TryGetProperty("positions", out _) && !Strings(person, "positions", out positions)) return Pending();
                links.Add(new KybBeneficiaryLink(id, type, positions, sessions));
                if (links.Count > 1000) return Pending();
            }
        }
        return new HostedSessionOutcome
        {
            Outcome = "APPROVED", IsApproved = true, IsTerminal = true, SessionExists = true,
            Business = new KybBusinessEvidence(name, number, country, links),
        };
    }

    private static HostedSessionOutcome Pending() => new() { Outcome = "IN_REVIEW", SessionExists = true };
    private static string? Text(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 512 } text
            && !string.IsNullOrWhiteSpace(text) ? text : null;
    private static bool Object(JsonElement element, string key, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object;
    }
    private static bool Array(JsonElement element, string key, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Array;
    }
    private static bool Strings(JsonElement element, string key, out IReadOnlyList<string> strings)
    {
        strings = [];
        if (!Array(element, key, out var values) || values.GetArrayLength() is 0 or > 100) return false;
        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 256 } text
                || string.IsNullOrWhiteSpace(text)) return false;
            result.Add(text);
        }
        strings = result;
        return true;
    }

    private static bool Sessions(JsonElement person, out IReadOnlyList<string> sessions)
    {
        sessions = [];
        var singular = Text(person, "sessionId");
        if (person.TryGetProperty("sessionId", out _) && singular is null) return false;
        if (person.TryGetProperty("sessionIds", out _))
        {
            if (!Strings(person, "sessionIds", out sessions)) return false;
            return singular is null || sessions.Count == 1 && sessions[0] == singular;
        }
        if (singular is null) return false;
        sessions = [singular];
        return true;
    }
}
