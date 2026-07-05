# Quest API — Plan

## Tasks

1. [ ] Create `Models/Quest/DTOs/QuestCreateModel.cs` and `QuestUpdateModel.cs`
2. [ ] Create `Models/Quest/DTOs/QuestNodeCreateModel.cs` and `QuestNodeUpdateModel.cs`
3. [ ] Create `Models/Quest/DTOs/QuestEdgeCreateModel.cs`
4. [ ] Create `Models/Quest/DTOs/QuestDependencyCreateModel.cs`
5. [ ] Create `Models/Quest/DTOs/QuestExecutionModels.cs` — ExecutionResult, DependencyCheckResult, QuestExecutionState
6. [ ] Create `Models/Quest/DTOs/QuestTemplateModels.cs` — template CRUD and instantiation DTOs
7. [ ] Create `Interfaces/Managers/IQuestManager.cs` — returns `AZOAResult<T>` matching existing manager pattern
8. [ ] Create `Managers/QuestManager.cs` — quest CRUD with avatar scoping, node/edge/dependency management
9. [ ] Implement `ActivateAsync` — DAG validation + dependency check → Draft → Active
10. [ ] Implement `ExecuteNodeAsync` — node dispatch: deserialize Config to correct request model, call matching manager method, serialize AZOAResult to Output
11. [ ] Implement `ExecuteNextAsync` — topological order, find next ready node(s), dispatch
12. [ ] Implement `InstantiateFromTemplateAsync` — parameterized quest creation
13. [ ] Implement `CreateNodeTemplateAsync` / `CreateQuestTemplateAsync`
14. [ ] Register `QuestManager` in `Program.cs` DI with all injected managers: `IHolonManager`, `INftManager`, `IWalletManager`, `ISTARManager`, `ISearchManager`, `IAvatarNFTService`, `IBlockchainOperationManager`
15. [ ] Create `Controllers/QuestController.cs` — CRUD + template instantiation, `[Authorize]`, `GetAvatarIdFromClaims()`
16. [ ] Create `Controllers/QuestNodesController.cs` — node/edge/dependency endpoints
17. [ ] Create `Controllers/QuestExecutionController.cs` — activate, execute-next, execute-node, complete, fail, execution-state
18. [ ] Create `Controllers/QuestTemplatesController.cs` — node template + quest template CRUD
19. [ ] Add EF Core migration for all quest tables
20. [ ] Manager unit tests — activation, dispatch table (each QuestNodeType → correct manager), dependency checks
21. [ ] Controller integration tests — happy paths for all endpoints
22. [ ] Run `dotnet build` — zero warnings
23. [ ] Run tests — all passing
24. [ ] Verify Swagger UI lists all quest endpoints
