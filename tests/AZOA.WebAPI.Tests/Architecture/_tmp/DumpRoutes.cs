using System.Text.Json;
namespace AZOA.WebAPI.Tests.Architecture;
public sealed class TmpDumpRoutes
{
    [Fact]
    public void Dump()
    {
        var rows = GateInventory.Routes().Select(r => new { r.Key, r.Verb, r.Template, r.Controller, r.Action, Policies = GateInventory.Policies(r.Method) });
        File.WriteAllText(Environment.GetEnvironmentVariable("DUMP_PATH")!, JsonSerializer.Serialize(rows));
        var stores = GateInventory.OwnedStoreMethods().Select(s => new { s.Store, s.Method, Sig = s.Info.ToString(), Decl = s.Info.DeclaringType!.FullName });
        File.WriteAllText(Environment.GetEnvironmentVariable("DUMP_PATH")! + ".stores.json", JsonSerializer.Serialize(stores));
    }
}
