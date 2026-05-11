# Dapp Composition — Plan

## Tasks

1. [ ] Create `Models/Quest/DappSeries.cs` — dApp series entity with DappSeriesStatus enum, DateTime timestamps
2. [ ] Create `Models/Quest/DappSeriesQuest.cs` — ordered quest entry with InputMappings JSON
3. [ ] Create `Models/Quest/DappManifest.cs` — composed artifact with BoundHolonIds, QuestGraph, Config
4. [ ] Create EF Core configurations (dictConverter for SharedConfig, JSON string for InputMappings/Manifest)
5. [ ] Add DbSets to `OASISDbContext` for DappSeries, DappSeriesQuest
6. [ ] Create `Interfaces/Managers/IDappCompositionManager.cs` — manager interface returning OASISResult<T>
7. [ ] Create `Managers/DappCompositionManager.cs` — series CRUD, quest management, composition
8. [ ] Implement `ComposeAsync` — validate all quests completed, build DappManifest with BoundHolonIds from quest node configs, TargetChain, combined Config
9. [ ] Implement `GenerateAsync` — create STARODK via `ISTARManager.CreateOrUpdateAsync`, build `STARDappGenerationRequest`, call `ISTARManager.GenerateAsync`, store StarOdkId
10. [ ] Implement `DeployAsync` — verify Ready status, call `ISTARManager.DeployAsync`, update status → Deployed
11. [ ] Register `DappCompositionManager` in `Program.cs` DI (depends on ISTARManager, IQuestRepository)
12. [ ] Create `Controllers/DappSeriesController.cs` — series CRUD, quest management endpoints
13. [ ] Create `Controllers/DappCompositionController.cs` — compose, generate, deploy endpoints
14. [ ] Add EF Core migration for dApp composition tables
15. [ ] Unit tests for composition validation rules (all quests completed, chain completeness, no circular deps)
16. [ ] Integration tests for full pipeline (series → compose → generate mock → deploy mock)
17. [ ] Run `dotnet build` — zero warnings
18. [ ] Run tests — all passing
19. [ ] Verify Swagger UI lists all dApp composition endpoints
