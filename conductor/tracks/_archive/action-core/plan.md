# ActionDefinition Core — Plan

## Tasks

1. [x] Create `Models/ActionDefinition/ActionDefinition.cs` — ActionDefinition entity with status enum, DateTime timestamps
2. [x] Create `Models/ActionDefinition/ActionEnums.cs` — ActionStatus, ActionNodeType (30+ values mapping to existing managers), ActionNodeState, ActionEdgeType, ActionDependencyType
3. [x] Create `Models/ActionDefinition/ActionNode.cs` — Node with Config JSON, State, Entry/Terminal flags
4. [x] Create `Models/ActionDefinition/ActionEdge.cs` — Directed edge with optional condition
5. [x] Create `Models/ActionDefinition/ActionDependency.cs` — Cross-action dependency (Required/Optional)
6. [x] Create `Models/ActionDefinition/ActionNodeTemplate.cs` — Reusable meta-node with schema validation
7. [x] Create `Models/ActionDefinition/ActionTemplate.cs` — Reusable full-DAG template
8. [x] Create `Models/ActionDefinition/ActionTemplateNode.cs` and `ActionTemplateEdge.cs` — Template building blocks
9. [x] Add EF configurations inline to `AZOADbContext` (dictConverter, listGuidConverter, listStringConverter)
10. [x] Add DbSets to `AZOADbContext` for action entities
11. [x] Create `Interfaces/IActionRepository.cs` — persistence abstraction
12. [x] Create `Interfaces/IActionDagValidator.cs` — DAG validation abstraction
13. [x] Create `Interfaces/IActionInstantiator.cs` — template instantiation abstraction
14. [x] Create `Services/ActionDagValidator.cs` — cycle detection (Kahn's algorithm), topological sort, entry/terminal/orphan validation
15. [x] Create `Services/ActionInstantiator.cs` — template → action instantiation with param validation
16. [x] Unit tests for `ActionDagValidator` — cycle detection, valid DAG, orphan detection, diamond graph, execution order (8 tests)
17. [x] Unit tests for `ActionInstantiator` — template substitution, schema validation, error cases (5 tests)
18. [x] Register ActionDefinition services in Program.cs DI
19. [x] Run `dotnet build` — zero errors
20. [x] Run tests — 315 total (36 new action tests), all passing
