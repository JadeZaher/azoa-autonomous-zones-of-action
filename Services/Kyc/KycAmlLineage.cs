// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Models.HostedSessions;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>Correlates AML evidence with one hosted attempt and its verified subject.</summary>
public static class KycAmlLineage
{
    public static string ForHosted(string submissionId, string sessionId, HostedSessionSubjectAttributes subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(subject);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            "hosted-aml-v1", submissionId, sessionId, subject.FirstName, subject.LastName,
            subject.Birthday, subject.Citizenship, subject.DocumentNumber, subject.IssuingState
        });
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
