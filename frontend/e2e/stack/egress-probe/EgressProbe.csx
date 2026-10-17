// AZOA E2E egress probe: runs INSIDE the API container (`dotnet /e2e/probe/AzoaEgressProbe.dll`),
// so .NET's own trust store, configured by that container's SSL_CERT_FILE / SSL_CERT_DIR, decides.
// Arguments: "+<url>" must complete a trusted HTTPS request, "-<url>" must fail. Exit code = violations.
// Kept as .csx so the repo-root AZOA.WebAPI.csproj (which compiles every **/*.cs) never picks it up;
// e2e-stack.mjs copies it to Program.cs inside an SDK container. See frontend/e2e/AGENTS.md §hermetic.
using System.Security.Authentication;

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var violations = 0;
foreach (var argument in args)
{
    var mustSucceed = argument.StartsWith('+');
    var url = argument[1..];
    bool succeeded;
    string outcome;
    try
    {
        using var response = await client.GetAsync(url);
        succeeded = true;
        outcome = $"trusted response HTTP {(int)response.StatusCode}";
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        succeeded = false;
        outcome = exception.InnerException is AuthenticationException
            ? "refused: certificate not trusted"
            : $"refused: {exception.GetType().Name}: {exception.Message}";
    }

    var asRequired = succeeded == mustSucceed;
    if (!asRequired) violations++;
    Console.WriteLine($"{(asRequired ? "ok  " : "FAIL")} {(mustSucceed ? "must reach" : "must not reach")} {url}: {outcome}");
}

return violations;
