using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Whose authentication may settle an existing custody liability; see Services/Hold/AGENTS.md §tenant-scope.</summary>
public static class HoldCustodyScope
{
    /// <summary>True for the owner's own login (no tenant) or the exact tenant that holds custody of the liability.</summary>
    public static bool AdmitsSettlement(HoldCommandIntent intent)
        => intent.TenantId is null || intent.TenantId == intent.CustodyTenantId;
}
