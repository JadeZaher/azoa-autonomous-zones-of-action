# Quest Core — Plan

## Tasks

1. [x] Create `Models/Quest/Quest.cs` — Quest entity with status enum, DateTime timestamps
2. [x] Create `Models/Quest/QuestEnums.cs` — QuestStatus, QuestNodeType (30+ values mapping to existing managers), QuestNodeState, QuestEdgeType, QuestDependencyType
3. [x] Create `Models/Quest/QuestNode.cs` — Node with Config JSON, State, Entry/Terminal flags
4. [x] Create `Models/Quest/QuestEdge.cs` — Directed edge with optional condition
5. [x] Create `Models/Quest/QuestDependency.cs` — Cross-quest dependency (Required/Optional)
6. [x] Create `Models/Quest/QuestNodeTemplate.cs` — Reusable meta-node with schema validation
7. [x] Create `Models/Quest/QuestTemplate.cs` — Reusable full-DAG template
8. [x] Create `Models/Quest/QuestTemplateNode.cs` and `QuestTemplateEdge.cs` — Template building blocks
9. [x] Add EF configurations inline to `AZOADbContext` (dictConverter, listGuidConverter, listStringConverter)
10. [x] Add DbSets to `AZOADbContext` for quest entities
11. [x] Create `Interfaces/IQuestRepository.cs` — persistence abstraction
12. [x] Create `Interfaces/IQuestDagValidator.cs` — DAG validation abstraction
13. [x] Create `Interfaces/IQuestInstantiator.cs` — template instantiation abstraction
14. [x] Create `Services/QuestDagValidator.cs` — cycle detection (Kahn's algorithm), topological sort, entry/terminal/orphan validation
15. [x] Create `Services/QuestInstantiator.cs` — template → quest instantiation with param validation
16. [x] Unit tests for `QuestDagValidator` — cycle detection, valid DAG, orphan detection, diamond graph, execution order (8 tests)
17. [x] Unit tests for `QuestInstantiator` — template substitution, schema validation, error cases (5 tests)
18. [x] Register Quest services in Program.cs DI
19. [x] Run `dotnet build` — zero errors
20. [x] Run tests — 315 total (36 new quest tests), all passing
