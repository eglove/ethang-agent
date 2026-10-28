# Issue #305 — the 48-file `subagent` surface, inventoried

Required by the owner's inventory comment on #305: "The 48-file surface is inventoried in the
implementation, and each file is changed or recorded as deliberately unchanged with a reason."

`git grep -li subagent -- src` returned **48 files at `1ba7936`**, the commit this issue started
from. It now returns **49**: the 48 plus `HostLaunchingRuntime.cs`, the new orchestration runtime
this issue adds. The classification below is the whole set, and the count difference is itself
part of the record — a reader re-running the grep should see 49 and know why.

## Added by #305

- `src/eThangAgent.Composition/HostLaunchingRuntime.cs` — the orchestration mechanism a host
  resolves: launches one host per agent, admits against the host cap, parks past it, and retires
  a host when its agent settles.
- `src/eThangAgent.Agent.Domain/HostCapacity.cs` — the cap, derived from a machine measurement.
- `src/eThangAgent.Agent.Domain/HostAdmissionGate.cs` — admission at the host boundary, parking
  rather than dropping.

(These three do not contain the string `subagent`, which is why only `HostLaunchingRuntime.cs`
changes the grep count: it references `SubAgents.RootTurnBudget` when building a child host.)

## Changed by #305

- `src/eThangAgent.Agent.Domain/AgentRecord.cs` — the D5 workspace stamp.
- `src/eThangAgent.Agent.Application/StartSpawnHandler.cs`
- `src/eThangAgent.ChildHost/ChildHostServer.cs`
- `src/eThangAgent.ChildHost/SessionHost.cs`
- `src/eThangAgent.Composition/AgentComposition.cs`
- `src/eThangAgent.Composition/HostLaunchingRuntime.cs`

## Deliberately unchanged

| File | Why it stays |
| --- | --- |
| `src/eThangAgent.Agent.Application/SpawnOptions.cs` | Model-resolution knobs. `WorkspaceRoot` is now also read for the D5 stamp; no field changes. |
| `src/eThangAgent.Agent.Domain/Agent.cs` | The agent loop itself. It runs an agent; #305 changes WHERE, not how. |
| `src/eThangAgent.Agent.Domain/ChildEvent.cs` | Event vocabulary. #316 retires agent-to-agent messaging, not this issue. |
| `src/eThangAgent.Agent.Domain/ChildProgressLedger.cs` | Pull-side progress ledger. #316 retires it with the messaging surface. |
| `src/eThangAgent.Agent.Domain/DefaultContextCompactor.cs` | Compaction policy; its handoff path reads running children, unchanged by where they run. |
| `src/eThangAgent.Agent.Domain/RootTurnBudget.cs` | Root-turn bounds. Passed unchanged into every launched host. |
| `src/eThangAgent.Agent.Domain/SubAgentOptions.cs` | Holds `MaxConcurrentAgents`, now the CHILD cap that `HostCapacity` supersedes at the host boundary. Retiring the key is a product decision for #316. |
| `src/eThangAgent.Agent.Domain/SubAgentServices.cs` | Service bundle for a runner. The runner still runs agents. |
| `src/eThangAgent.Agent.Domain/SubAgentSpawner.cs` | The leaf executor's body. Runs one agent; every agent uses it. |
| `src/eThangAgent.Agent.Domain/ToolRepeatGuard.cs` | Loop guard, per-run. Unaffected by process topology. |
| `src/eThangAgent.Agent.Infrastructure/InProcessAgentRuntime.cs` | SURVIVES AS THE LEAF EXECUTOR. It no longer orchestrates N children in one host; it runs the one agent a host owns, reached via `SessionHost.LocalRunner`. |
| `src/eThangAgent.ChildHost/RootRunHost.cs` | Runs the conversation's own turn. Unchanged by where children run. |
| `src/eThangAgent.Composition/AgentSettings.cs` | Settings carrier. Launched hosts inherit it verbatim. |
| `src/eThangAgent.Composition/AgentSettingsLoader.cs` | Loads settings. No key retired, so no sweep change. |
| `src/eThangAgent.Composition/JobObject.cs` | Containment primitive, reused unchanged by each launched host. |
| `src/eThangAgent.Composition/PlanChangePublisher.cs` | Publishes plan facts. #301 consumes them. |
| `src/eThangAgent.Composition/RemoteHostSupervisor.cs` | UNCHANGED — reused as the launcher. It was already one-host-per-identity; #305 only changes WHO calls it and with which identity. |
| `src/eThangAgent.Composition/SubAgentConfiguration.cs` | Strict preference binding. No key retired. |
| `src/eThangAgent.Composition/WatchdogSettings.cs` | Watchdog knobs. No key retired. |
| `src/eThangAgent.Desktop/ViewModels/AgentSessionViewModel.cs` | Desktop view-model. Renders one session; the grid (#307) is where cells multiply. |
| `src/eThangAgent.Desktop/ViewModels/MainViewModel.cs` | Tab host. Unchanged by per-agent hosts. |
| `src/eThangAgent.Desktop/ViewModels/PlansViewViewModel.cs` | Plans rail. #312 adds the chain view beside it. |
| `src/eThangAgent.Desktop/ViewModels/SettingsViewModel.cs` | Settings UI. No key retired. |
| `src/eThangAgent.Desktop/ViewModels/SubagentPanelViewModel.cs` | #316 retires it; #307 replaces the surface. |
| `src/eThangAgent.Desktop/ViewModels/WorkbenchViewModel.cs` | #312/#318/#13 add rail views beside it. |
| `src/eThangAgent.Desktop/Views/AgentView.axaml` | Layout. The grid is #307. |
| `src/eThangAgent.Desktop/Views/AgentView.axaml.cs` | Code-behind. The grid is #307. |
| `src/eThangAgent.Model.Domain/IUsageQueries.cs` | Read seam. The walk still attributes because `parent_id` is still written. |
| `src/eThangAgent.Model.Domain/UsageRow.cs` | Row shape. Unchanged. |
| `src/eThangAgent.Roslyn.ACL/CSharpScriptExecEngine.cs` | Exec engine. Unaffected by process topology. |
| `src/eThangAgent.Roslyn.ACL/ScriptGlobals.cs` | Exec globals. Unaffected. |
| `src/eThangAgent.Skill.Domain/skills/EthangToolsMapping.md` | Tool-binding skill. #308 reworks skills for chains. |
| `src/eThangAgent.Skill.Domain/skills/dispatching-parallel-agents/SKILL.md` | #308 reworks skills for chains. |
| `src/eThangAgent.Skill.Domain/skills/executing-plans/SKILL.md` | #308. |
| `src/eThangAgent.Skill.Domain/skills/experiment-driven-development/SKILL.md` | #308. |
| `src/eThangAgent.Skill.Domain/skills/grill/SKILL.md` | #308. |
| `src/eThangAgent.Skill.Domain/skills/requesting-code-review/SKILL.md` | #308. |
| `src/eThangAgent.Skill.Domain/skills/writing-plans/SKILL.md` | #308. |
| `src/eThangAgent.Skill.Domain/skills/writing-skills/SKILL.md` | #308. |
| `src/eThangAgent.Storage.ACL/DeadPreferenceSweep.cs` | No preference key is retired by #305. Retiring `MaxConcurrentAgents` as a child cap belongs with #316. |
| `src/eThangAgent.Storage.ACL/SqliteUsageQueries.cs` | Attribution CTE unchanged: `parent_id` is still populated, pinned by `UsageAttributionParentLinkTests`. |
| `src/eThangAgent.Transport.ACL/ChildEventEnvelope.cs` | Wire envelope. #316 retires the messaging surface. |
| `src/eThangAgent.Transport.ACL/RemoteAgentRuntime.cs` | The wire runtime, REUSED: `HostLaunchingRuntime` builds one per launched host. |
| `src/eThangAgent.Transport.ACL/RootTurnProtocol.cs` | Root-turn codec. Unchanged. |
