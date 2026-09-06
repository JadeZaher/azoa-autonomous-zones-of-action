# Action API — Plan

## Tasks

1. [ ] Create `Models/Actions/DTOs/ActionCreateModel.cs` and `ActionUpdateModel.cs`
2. [ ] Create `Models/Actions/DTOs/ActionNodeCreateModel.cs` and `ActionNodeUpdateModel.cs`
3. [ ] Create `Models/Actions/DTOs/ActionEdgeCreateModel.cs`
4. [ ] Create `Models/Actions/DTOs/ActionDependencyCreateModel.cs`
5. [ ] Create `Models/Actions/DTOs/ActionExecutionModels.cs` — ExecutionResult, DependencyCheckResult, ActionExecutionState
6. [ ] Create `Models/Actions/DTOs/ActionTemplateModels.cs` — template CRUD and instantiation DTOs
7. [ ] Create `Interfaces/Managers/IActionManager.cs` — returns `AZOAResult<T>` matching existing manager pattern
8. [ ] Create `Managers/ActionManager.cs` — action CRUD with user scoping, node/edge/dependency management
9. [ ] Implement `ActivateAsync` — DAG validation + dependency check → Draft → Active
10. [ ] Implement `ExecuteNodeAsync` — node dispatch: deserialize Config to correct request model, call matching manager method, serialize AZOAResult to Output
11. [ ] Implement `ExecuteNextAsync` — topological order, find next ready node(s), dispatch
12. [ ] Implement `InstantiateFromTemplateAsync` — parameterized action creation
13. [ ] Implement `CreateNodeTemplateAsync` / `CreateActionTemplateAsync`
14. [ ] Register `ActionManager` in `Program.cs` DI with all injected managers: `IHolonManager`, `INftManager`, `IWalletManager`, `ISTARManager`, `ISearchManager`, `IUserNFTService`, `IBlockchainOperationManager`
15. [ ] Create `Controllers/ActionController.cs` — CRUD + template instantiation, `[Authorize]`, `GetUserIdFromClaims()`
16. [ ] Create `Controllers/ActionNodesController.cs` — node/edge/dependency endpoints
17. [ ] Create `Controllers/ActionExecutionController.cs` — activate, execute-next, execute-node, complete, fail, execution-state
18. [ ] Create `Controllers/ActionTemplatesController.cs` — node template + action template CRUD
19. [ ] Add EF Core migration for all action tables
20. [ ] Manager unit tests — activation, dispatch table (each ActionNodeType → correct manager), dependency checks
21. [ ] Controller integration tests — happy paths for all endpoints
22. [ ] Run `dotnet build` — zero warnings
23. [ ] Run tests — all passing
24. [ ] Verify Swagger UI lists all action endpoints
