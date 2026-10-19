# Action tests

The workflow designer HTTP fixture uses the generic `HostBuilder` with `ConfigureWebHost(...UseTestServer())`, matching the repository's delegated-credential and OAuth host tests and the installed TestHost 10 API. Direct `WebHostBuilder` and the `TestServer(IWebHostBuilder)` constructor are obsolete in .NET 10. Startup is awaited before obtaining clients; every test disposes its client and asynchronously stops/disposes the host. Failed startup disposes the partially initialized host and rethrows the original exception.

The tests retain live-tenant ownership/revocation, non-tenant/cross-owner/delegated rejection, role checks, canonical shared tenant request budgets and consent/no-provider-call assertions. This hosting change does not replace policy behavior with mocks.
