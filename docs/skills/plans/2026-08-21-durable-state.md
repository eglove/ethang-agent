# P3 Durable State — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Per project preference, execution dispatches one Fabric agent per task with review between tasks; repeated agent failure on a task falls back to inline implementation so waves never stall.

**Goal:** Give the agent a durable, app-owned world-model: a workspace-keyed CAS key-value store plus the certification engine — claims (transitions) carry attached PowerShell evidence, and only fail-closed verification certifies them — exposed as `state.*` capability actions with registry-generated documentation.

**Architecture:** New pure-logic `eThangAgent.State.Domain` (StateService + records + `IStateStore`/`IEvidenceRunner`/`IWorkspaceContext` seams + `StateCapabilityProvider`), new `eThangAgent.Storage.ACL` (single app-owned SQLite database via Microsoft.Data.Sqlite, versioned migrations, `SqliteStateStore`), and `PsEvidenceRunner` in PowerShell.ACL (fresh runspace per evidence command). CLI wires everything; one database serves many workspaces.

**Tech Stack:** .NET 10, C# (records, Result<T>), Microsoft.Data.Sqlite, System.Management.Automation via Microsoft.PowerShell.SDK 7.4.*, xUnit.

**Spec:** `docs/skills/specs/2026-08-21-durable-state-design.md` — the plan argues from the spec; executors read both.

> **Progress:** Waves 1–2 COMPLETE — Tasks 1–6 done and verified green (Task 5 fixes: SessionStateProxy.GetVariable API, exit-code-first error detail). Next: Task 7 (E2E discipline loops), then Task 8 (full verification).

## Global Constraints

- **Windows-only, PowerShell-only.** No `.sh`, `.cmd`, `.bat` scripts in the repo.
- **.NET 10 / C#**, ImplicitUsings + Nullable (inherited from `Directory.Build.props`).
- **No project dot-folders:** state lives ONLY in the app database at `%LOCALAPPDATA%\eThangAgent\eThangAgent.db` (env override `ETHANG_AGENT_DB`). Never write state files into project directories.
- **Workspace-keyed:** every store operation is scoped by `IWorkspaceContext.WorkspaceId` (canonical cwd CLI-side).
- **Fail-closed certification:** certify only when ≥1 transition selected and every evidence command confirms (no errors AND `$LASTEXITCODE` 0/unset); violations name blocking reasons; head-certificate revocation happens BEFORE the violated event.
- **CAS:** `expectedVersion` mismatch → `VersionConflict` naming the current version; atomic at the SQL layer.
- **Strict inputs:** capability actions reject unknown parameters; key format `ns/name` (single slash, non-empty segments).
- **No nested exec; model surface stays exec-only.**
- **Every task ends green:** full-output build scan (`… | rg 'error|FAILED' || echo BUILD-CLEAN`) + targeted tests before committing (conventional commits).
- **Test conventions:** xUnit; `GlobalUsings.cs` with `global using Xunit;`; hand-rolled fakes only; storage tests use temp databases; `TestResults` is gitignored — always `fd --no-ignore`.

## File Structure

**New projects:** `src/eThangAgent.State.Domain/` (namespace `eThangAgent.StateDomain`; references Capability.Domain + SharedKernel), `src/eThangAgent.Storage.ACL/` (namespace `eThangAgent.Storage.ACL`; references State.Domain); test projects for both.

**New files (State.Domain):** `EvidenceOptions.cs`, `StateKey.cs`, `StateKeyValue.cs`, `TransitionRecord.cs`, `EvidenceResult.cs`, `CertificationReport.cs`, `StateEvent.cs`, `IWorkspaceContext.cs`, `IStateStore.cs`, `IEvidenceRunner.cs`, `IStateService.cs`, `StateService.cs`, `StateCapabilityProvider.cs`.

**New files (Storage.ACL):** `AppDatabase.cs`, `SqliteStateStore.cs`.

**New files (PowerShell.ACL):** `PsEvidenceRunner.cs`.

**New files (CLI):** `CwdWorkspaceContext.cs`.

**Modified:** `eThangAgent.slnx` (+4 entries), `ExecGuide.cs` (v1.2 durable-state pointer), `ExecGuideTests.cs`, `Program.cs` + `eThangAgent.CLI.csproj` + `src/eThangAgent.PowerShell.ACL/eThangAgent.PowerShell.ACL.csproj` + `src/eThangAgent.Storage.ACL/eThangAgent.Storage.ACL.csproj` (references), `E2ETests.cs` (state reference + discipline loops).

---

### Task 1: State.Domain scaffold — records, seams, key parsing

**Files:**

- Create: `src/eThangAgent.State.Domain/eThangAgent.State.Domain.csproj`
- Create: `src/eThangAgent.State.Domain/EvidenceOptions.cs`
- Create: `src/eThangAgent.State.Domain/StateKey.cs`
- Create: `src/eThangAgent.State.Domain/StateKeyValue.cs`
- Create: `src/eThangAgent.State.Domain/TransitionRecord.cs`
- Create: `src/eThangAgent.State.Domain/EvidenceResult.cs`
- Create: `src/eThangAgent.State.Domain/CertificationReport.cs`
- Create: `src/eThangAgent.State.Domain/StateEvent.cs`
- Create: `src/eThangAgent.State.Domain/IWorkspaceContext.cs`
- Create: `src/eThangAgent.State.Domain/IStateStore.cs`
- Create: `src/eThangAgent.State.Domain/IEvidenceRunner.cs`
- Create: `tests/eThangAgent.State.Domain.Tests/eThangAgent.State.Domain.Tests.csproj`
- Create: `tests/eThangAgent.State.Domain.Tests/GlobalUsings.cs`
- Create: `tests/eThangAgent.State.Domain.Tests/StateKeyTests.cs`
- Modify: `eThangAgent.slnx` (+2 entries)

**Interfaces (locked — Tasks 2–5 consume exactly these):**

- `EvidenceOptions { TimeSpan Timeout = 120s; static Default }`.
- `StateKey.Parse(string key) → Result<(string Ns, string Name)>` — single `/`, non-empty segments.
- `StateKeyValue(Ns, Name, Value, Version)`; `TransitionRecord(Id, From, To, Summary, Evidence, Status, CreatedAt)`; `EvidenceResult(Command, Confirmed, Detail)`; `CertificationReport(Certified, Violated, Results, BlockingReasons)`; `StateEvent(Id, Kind, PayloadJson, OccurredAt)`.
- `IWorkspaceContext { string WorkspaceId { get; } }`.
- `IStateStore`: `GetKeyAsync(ws, ns, name) → Task<StateKeyValue?>`; `ListKeysAsync(ws, ns?) → Task<IReadOnlyList<StateKeyValue>>`; `SetKeyCasAsync(ws, ns, name, value, int? expectedVersion) → Task<StateKeyValue?>` (null = conflict); `DeleteKeyCasAsync(ws, ns, name, int? expectedVersion) → Task<bool>`; `InsertTransitionAsync(ws, TransitionRecord) → Task<TransitionRecord>`; `GetTransitionsAsync(ws, ids) → Task<IReadOnlyList<TransitionRecord>>` (empty ids = all pending); `SetTransitionStatusAsync(ws, id, status)`; `AppendEventAsync(ws, kind, payloadJson)`; `GetEventsAsync(ws, limit) → Task<IReadOnlyList<StateEvent>>`.
- `IEvidenceRunner { Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default); }`.

- [ ] **Step 1: Create projects + solution entries**

`src/eThangAgent.State.Domain/eThangAgent.State.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../eThangAgent.Capability.Domain/eThangAgent.Capability.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.SharedKernel/eThangAgent.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.State.Domain.Tests/eThangAgent.State.Domain.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../src/eThangAgent.State.Domain/eThangAgent.State.Domain.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.State.Domain.Tests/GlobalUsings.cs`:

```csharp
global using Xunit;
```

In `eThangAgent.slnx`, add after the Capability.Domain project line:

```xml
  <Project Path="src/eThangAgent.State.Domain/eThangAgent.State.Domain.csproj" />
```

and after the Capability.Domain.Tests line:

```xml
  <Project Path="tests/eThangAgent.State.Domain.Tests/eThangAgent.State.Domain.Tests.csproj" />
```

- [ ] **Step 2: Write the failing tests**

`tests/eThangAgent.State.Domain.Tests/StateKeyTests.cs`:

```csharp
using eThangAgent.StateDomain;

namespace eThangAgent.State.Domain.Tests;

public class StateKeyTests
{
    [Fact]
    public void Parse_ValidKey_SplitsSegments()
    {
        var result = StateKey.Parse("current/head");
        Assert.True(result.IsSuccess);
        Assert.Equal(("current", "head"), result.Value!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noslash")]
    [InlineData("a/b/c")]
    [InlineData("/head")]
    [InlineData("current/")]
    [InlineData("current /head")]
    public void Parse_InvalidKey_Fails_InvalidKey(string key)
    {
        var result = StateKey.Parse(key);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidKey", result.Error!.Code);
    }
}
```

- [ ] **Step 3: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.State.Domain.Tests/eThangAgent.State.Domain.Tests.csproj`
Expected: build fails, `StateKey` not found.

- [ ] **Step 4: Implement**

`src/eThangAgent.State.Domain/EvidenceOptions.cs`:

```csharp
namespace eThangAgent.StateDomain;

public sealed record EvidenceOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    public static EvidenceOptions Default { get; } = new();
}
```

`src/eThangAgent.State.Domain/StateKey.cs`:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public static class StateKey
{
    public static Result<(string Ns, string Name)> Parse(string key)
    {
        if (string.IsNullOrEmpty(key))
            return Failure;
        var slash = key.IndexOf('/');
        if (slash <= 0 || slash == key.Length - 1 || key.IndexOf('/', slash + 1) >= 0)
            return Failure;
        var ns = key[..slash];
        var name = key[(slash + 1)..];
        if (ns.Trim().Length == 0 || name.Trim().Length == 0)
            return Failure;
        return Result<(string Ns, string Name)>.Success((ns, name));
    }

    private static Result<(string Ns, string Name)> Failure
        => Result<(string Ns, string Name)>.Failure(
            new Error("InvalidKey", "Key must be 'ns/name' with non-empty segments and a single slash."));
}
```

`src/eThangAgent.State.Domain/StateKeyValue.cs`:

```csharp
namespace eThangAgent.StateDomain;

public sealed record StateKeyValue(string Ns, string Name, string Value, int Version);
```

`src/eThangAgent.State.Domain/TransitionRecord.cs`:

```csharp
namespace eThangAgent.StateDomain;

public sealed record TransitionRecord(
    string Id,
    string From,
    string To,
    string Summary,
    IReadOnlyList<string> Evidence,
    string Status,
    DateTimeOffset CreatedAt);
```

`src/eThangAgent.State.Domain/EvidenceResult.cs`:

```csharp
namespace eThangAgent.StateDomain;

public sealed record EvidenceResult(string Command, bool Confirmed, string Detail);
```

`src/eThangAgent.State.Domain/CertificationReport.cs`:

```csharp
namespace eThangAgent.StateDomain;

public sealed record CertificationReport(
    bool Certified,
    bool Violated,
    IReadOnlyList<EvidenceResult> Results,
    IReadOnlyList<string> BlockingReasons);
```

`src/eThangAgent.State.Domain/StateEvent.cs`:

```csharp
namespace eThangAgent.StateDomain;

public sealed record StateEvent(long Id, string Kind, string PayloadJson, DateTimeOffset OccurredAt);
```

`src/eThangAgent.State.Domain/IWorkspaceContext.cs`:

```csharp
namespace eThangAgent.StateDomain;

public interface IWorkspaceContext
{
    string WorkspaceId { get; }
}
```

`src/eThangAgent.State.Domain/IStateStore.cs`:

```csharp
namespace eThangAgent.StateDomain;

public interface IStateStore
{
    Task<StateKeyValue?> GetKeyAsync(
        string workspaceId, string ns, string name, CancellationToken ct = default);

    Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(
        string workspaceId, string? ns, CancellationToken ct = default);

    /// <summary>Atomic CAS write. Returns the new row, or null when expectedVersion
    ///     was supplied and did not match (fail-closed conflict).</summary>
    Task<StateKeyValue?> SetKeyCasAsync(
        string workspaceId, string ns, string name, string value,
        int? expectedVersion, CancellationToken ct = default);

    /// <summary>Atomic CAS delete. Returns false on conflict or missing key.</summary>
    Task<bool> DeleteKeyCasAsync(
        string workspaceId, string ns, string name,
        int? expectedVersion, CancellationToken ct = default);

    Task<TransitionRecord> InsertTransitionAsync(
        string workspaceId, TransitionRecord transition, CancellationToken ct = default);

    /// <summary>Empty ids selects every pending transition.</summary>
    Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(
        string workspaceId, IReadOnlyList<string> transitionIds, CancellationToken ct = default);

    Task SetTransitionStatusAsync(
        string workspaceId, string transitionId, string status, CancellationToken ct = default);

    Task AppendEventAsync(
        string workspaceId, string kind, string payloadJson, CancellationToken ct = default);

    Task<IReadOnlyList<StateEvent>> GetEventsAsync(
        string workspaceId, int limit, CancellationToken ct = default);
}
```

`src/eThangAgent.State.Domain/IEvidenceRunner.cs`:

```csharp
namespace eThangAgent.StateDomain;

public interface IEvidenceRunner
{
    Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default);
}
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.State.Domain.Tests/eThangAgent.State.Domain.Tests.csproj`
Expected: 8 tests pass.

- [ ] **Step 6: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.State.Domain tests/eThangAgent.State.Domain.Tests eThangAgent.slnx
```

```bash
git commit -m "feat(state-domain): add state records, seams, and key parsing"
```

---

### Task 2: IStateService + StateCapabilityProvider (the `state` provider)

**Files:**

- Create: `src/eThangAgent.State.Domain/IStateService.cs`
- Create: `src/eThangAgent.State.Domain/StateCapabilityProvider.cs`
- Create: `tests/eThangAgent.State.Domain.Tests/StateCapabilityProviderTests.cs`

**Interfaces:**

- Consumes: Task 1 records/interfaces; `ICapabilityProvider`, `ActionDescriptor`, `ActionParameter`, `CapabilityInvocationResult` (Capability.Domain); `Result<T>`.
- Produces: `IStateService` (exact members below — Task 3 implements it); `StateCapabilityProvider(IStateService)` with `Id = "state"` and eight action descriptors (`get`, `set`, `delete`, `list`, `transition`, `verify`, `checkgoal`, `history`); strict JSON-argument parsing (unknown parameters rejected with `Error [InvalidActionInput]:`); reports serialized as JSON.

- [ ] **Step 1: Write the failing tests**

`tests/eThangAgent.State.Domain.Tests/StateCapabilityProviderTests.cs`:

```csharp
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.State.Domain.Tests;

public class StateCapabilityProviderTests
{
    private static StateCapabilityProvider Create(FakeStateService? service = null)
        => new(service ?? new FakeStateService());

    [Fact]
    public void Provider_ExposesEightActions_UnderStateId()
    {
        var provider = Create();

        Assert.Equal("state", provider.Id);
        Assert.Equal(8, provider.Actions.Count);
        Assert.Contains(provider.Actions, a => a.Name == "transition" && a.Summary.Contains("evidence"));
        Assert.Contains(provider.Actions, a => a.Name == "verify" && a.Description.Contains("fail-closed"));
    }

    [Fact]
    public async Task Get_Delegates_AndReturnsContent()
    {
        var service = new FakeStateService();
        service.GetResult = Result<string>.Success("hello");

        var result = await Create(service).InvokeAsync("get", """{"key":"current/head"}""");

        Assert.False(result.IsError);
        Assert.Equal("hello", result.Content);
        Assert.Equal("current/head", service.LastKey);
    }

    [Fact]
    public async Task Set_PassesExpectedVersion_AndFormatsContent()
    {
        var service = new FakeStateService();
        service.SetResult = Result<StateKeyValue>.Success(new StateKeyValue("current", "head", "x", 3));

        var result = await Create(service).InvokeAsync("set",
            """{"key":"current/head","value":"x","expectedVersion":2}""");

        Assert.False(result.IsError);
        Assert.Contains("current/head v3", result.Content);
        Assert.Equal(2, service.LastExpectedVersion);
    }

    [Fact]
    public async Task Transition_ParsesEvidenceArray_AndReturnsId()
    {
        var service = new FakeStateService();
        service.TransitionResult = Result<string>.Success("tr-abc");

        var result = await Create(service).InvokeAsync("transition",
            """{"from":"coding","to":"done","summary":"work","evidence":["Write-Output ok"]}""");

        Assert.False(result.IsError);
        Assert.Equal("tr-abc", result.Content);
        Assert.NotNull(service.LastEvidence);
        Assert.Equal("Write-Output ok", service.LastEvidence![0]);
    }

    [Fact]
    public async Task Verify_ReturnsSerializedReport()
    {
        var service = new FakeStateService();
        service.VerifyResult = new CertificationReport(true, false,
            [new EvidenceResult("Write-Output ok", true, "")], []);

        var result = await Create(service).InvokeAsync("verify", "{}");

        Assert.False(result.IsError);
        Assert.Contains("\"Certified\":true", result.Content);
    }

    [Fact]
    public async Task ServiceError_SurfacesAsGutter()
    {
        var service = new FakeStateService();
        service.GetResult = Result<string>.Failure(new Error("KeyNotFound", "current/head does not exist."));

        var result = await Create(service).InvokeAsync("get", """{"key":"current/head"}""");

        Assert.True(result.IsError);
        Assert.Contains("Error [KeyNotFound]:", result.Content);
    }

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var result = await Create().InvokeAsync("get", """{"key":"a","extra":1}""");

        Assert.True(result.IsError);
        Assert.Contains("Error [InvalidActionInput]:", result.Content);
        Assert.Contains("Unknown parameter", result.Content);
    }

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var result = await Create().InvokeAsync("nope", "{}");

        Assert.True(result.IsError);
        Assert.Contains("Error [UnknownAction]:", result.Content);
    }

    private sealed class FakeStateService : IStateService
    {
        public Result<string> GetResult { get; set; } = Result<string>.Success("v1");
        public Result<StateKeyValue> SetResult { get; set; } =
            Result<StateKeyValue>.Success(new StateKeyValue("current", "head", "x", 2));
        public Result<string> DeleteResult { get; set; } = Result<string>.Success("deleted");
        public Result<IReadOnlyList<string>> ListResult { get; set; } =
            Result<IReadOnlyList<string>>.Success(["current/head v2"]);
        public Result<string> TransitionResult { get; set; } = Result<string>.Success("tr-1");
        public CertificationReport VerifyResult { get; set; } =
            new(true, false, [], []);
        public CertificationReport GoalResult { get; set; } =
            new(true, false, [], []);
        public Result<IReadOnlyList<string>> HistoryResult { get; set; } =
            Result<IReadOnlyList<string>>.Success([]);

        public string? LastKey { get; private set; }
        public int? LastExpectedVersion { get; private set; }
        public IReadOnlyList<string>? LastEvidence { get; private set; }

        public Task<Result<string>> GetAsync(string key, CancellationToken ct = default)
        { LastKey = key; return Task.FromResult(GetResult); }

        public Task<Result<StateKeyValue>> SetAsync(string key, string value, int? expectedVersion, CancellationToken ct = default)
        { LastKey = key; LastExpectedVersion = expectedVersion; return Task.FromResult(SetResult); }

        public Task<Result<string>> DeleteAsync(string key, int? expectedVersion, CancellationToken ct = default)
        { LastKey = key; LastExpectedVersion = expectedVersion; return Task.FromResult(DeleteResult); }

        public Task<Result<IReadOnlyList<string>>> ListAsync(string? ns, CancellationToken ct = default)
            => Task.FromResult(ListResult);

        public Task<Result<string>> TransitionAsync(string from, string to, string summary,
            IReadOnlyList<string> evidence, CancellationToken ct = default)
        { LastEvidence = evidence; return Task.FromResult(TransitionResult); }

        public Task<CertificationReport> VerifyAsync(IReadOnlyList<string>? ids, CancellationToken ct = default)
            => Task.FromResult(VerifyResult);

        public Task<CertificationReport> CheckGoalAsync(CancellationToken ct = default)
            => Task.FromResult(GoalResult);

        public Task<Result<IReadOnlyList<string>>> HistoryAsync(int limit, CancellationToken ct = default)
            => Task.FromResult(HistoryResult);
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.State.Domain.Tests/eThangAgent.State.Domain.Tests.csproj`
Expected: build fails, `IStateService`/`StateCapabilityProvider` not found.

- [ ] **Step 3: Implement**

`src/eThangAgent.State.Domain/IStateService.cs`:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public interface IStateService
{
    Task<Result<string>> GetAsync(string key, CancellationToken ct = default);
    Task<Result<StateKeyValue>> SetAsync(string key, string value, int? expectedVersion, CancellationToken ct = default);
    Task<Result<string>> DeleteAsync(string key, int? expectedVersion, CancellationToken ct = default);
    Task<Result<IReadOnlyList<string>>> ListAsync(string? ns, CancellationToken ct = default);
    Task<Result<string>> TransitionAsync(string from, string to, string summary,
        IReadOnlyList<string> evidence, CancellationToken ct = default);
    Task<CertificationReport> VerifyAsync(IReadOnlyList<string>? ids, CancellationToken ct = default);
    Task<CertificationReport> CheckGoalAsync(CancellationToken ct = default);
    Task<Result<IReadOnlyList<string>>> HistoryAsync(int limit, CancellationToken ct = default);
}
```

`src/eThangAgent.State.Domain/StateCapabilityProvider.cs`:

```csharp
using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public sealed class StateCapabilityProvider : ICapabilityProvider
{
    public const string ProviderId = "state";

    private readonly IStateService _service;

    public StateCapabilityProvider(IStateService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    public string Id => ProviderId;

    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new("get", "Read a durable state value.",
            "Reads one namespaced key. Fails with KeyNotFound when absent.",
            [new ActionParameter("key", "String", "Namespaced key, e.g. current/head.")]),
        new("set", "Write a durable state value with optional compare-and-swap.",
            "Creates or updates a key. Supply expectedVersion to require the current version; a mismatch fails closed with VersionConflict naming the current version. Returns the new version.",
            [new ActionParameter("key", "String", "Namespaced key."),
             new ActionParameter("value", "String", "Value to store."),
             new ActionParameter("expectedVersion", "Integer", "Optional. Fail unless the stored version matches.")]),
        new("delete", "Delete a durable state key.",
            "Removes a key. Supply expectedVersion for a compare-and-swap delete.",
            [new ActionParameter("key", "String", "Namespaced key."),
             new ActionParameter("expectedVersion", "Integer", "Optional. Fail unless the stored version matches.")]),
        new("list", "List state keys.",
            "Lists keys as 'ns/name v<version>' lines, optionally filtered by namespace.",
            [new ActionParameter("ns", "String", "Optional namespace filter.")]),
        new("transition", "Attach a claim with evidence (stored, never run on attach).",
            "Records a labeled move from one world-state to another with summary and evidence commands. Evidence is replayable but has NOT run. Returns the transition id; status starts pending.",
            [new ActionParameter("from", "String", "Prior state label."),
             new ActionParameter("to", "String", "New state label."),
             new ActionParameter("summary", "String", "What this claim asserts."),
             new ActionParameter("evidence", "String[]", "PowerShell commands that, when run, should confirm the claim.")]),
        new("verify", "Run attached evidence fail-closed and certify.",
            "Runs the evidence for the selected transitions (default: all pending). Certifies only when every command confirms; otherwise reports violated with blocking reasons and revokes any head certificate first.",
            [new ActionParameter("ids", "String[]", "Optional transition ids; default all pending.")]),
        new("checkgoal", "Run the goal/check commands and report.",
            "Runs the commands stored at goal/check and reports pass/fail. Report-only — no certification.",
            []),
        new("history", "Replay the state timeline.",
            "Returns the most recent timeline events (transitions, certified, violated).",
            [new ActionParameter("limit", "Integer", "Optional. Default 20.")]),
    ];

    public async Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
    {
        try
        {
            return actionName switch
            {
                "get" => await GetAsync(jsonArguments),
                "set" => await SetAsync(jsonArguments),
                "delete" => await DeleteAsync(jsonArguments),
                "list" => await ListAsync(jsonArguments),
                "transition" => await TransitionAsync(jsonArguments),
                "verify" => await VerifyAsync(jsonArguments),
                "checkgoal" => await CheckGoalAsync(),
                "history" => await HistoryAsync(jsonArguments),
                _ => CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."),
            };
        }
        catch (StateInputException ex)
        {
            return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {ex.Message}");
        }
    }

    private async Task<CapabilityInvocationResult> GetAsync(string json)
    {
        var args = ParseArgs(json, Allowed("key"));
        return ToResult(await _service.GetAsync(ReqString(args, "key")));
    }

    private async Task<CapabilityInvocationResult> SetAsync(string json)
    {
        var args = ParseArgs(json, Allowed("key", "value", "expectedVersion"));
        var saved = await _service.SetAsync(ReqString(args, "key"), ReqString(args, "value"), OptInt(args, "expectedVersion"));
        return saved.IsSuccess
            ? CapabilityInvocationResult.Ok($"saved {saved.Value!.Ns}/{saved.Value.Name} v{saved.Value.Version}")
            : Gutter(saved.Error!);
    }

    private async Task<CapabilityInvocationResult> DeleteAsync(string json)
        => ToResult(await _service.DeleteAsync(
            ReqString(ParseArgs(json, Allowed("key", "expectedVersion")), "key"),
            OptInt(ParseArgs(json, Allowed("key", "expectedVersion")), "expectedVersion")));

    private async Task<CapabilityInvocationResult> ListAsync(string json)
        => ToResult(await _service.ListAsync(OptString(ParseArgs(json, Allowed("ns")), "ns")));

    private async Task<CapabilityInvocationResult> TransitionAsync(string json)
    {
        var args = ParseArgs(json, Allowed("from", "to", "summary", "evidence"));
        return ToResult(await _service.TransitionAsync(
            ReqString(args, "from"), ReqString(args, "to"), ReqString(args, "summary"),
            OptStringArray(args, "evidence")));
    }

    private async Task<CapabilityInvocationResult> VerifyAsync(string json)
    {
        var report = await _service.VerifyAsync(OptStringArray(ParseArgs(json, Allowed("ids")), "ids"));
        return Report(report);
    }

    private async Task<CapabilityInvocationResult> CheckGoalAsync()
        => Report(await _service.CheckGoalAsync());

    private async Task<CapabilityInvocationResult> HistoryAsync(string json)
        => ToResult(await _service.HistoryAsync(OptInt(ParseArgs(json, Allowed("limit")), "limit") ?? 20));

    private static CapabilityInvocationResult ToResult<T>(Result<T> result)
        => result.IsSuccess
            ? CapabilityInvocationResult.Ok(result.Value!.ToString() ?? "")
            : Gutter(result.Error!);

    private static CapabilityInvocationResult Gutter(Error error)
        => CapabilityInvocationResult.Fail($"Error [{error.Code}]: {error.Message}");

    private static CapabilityInvocationResult Report(CertificationReport report)
        => CapabilityInvocationResult.Ok(JsonSerializer.Serialize(report));

    private static IReadOnlySet<string> Allowed(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> ParseArgs(string json, IReadOnlySet<string> allowed)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new StateInputException($"Arguments are not valid JSON: {ex.Message}");
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new StateInputException("Arguments must be a JSON object.");
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new StateInputException($"Unknown parameter '{property.Name}'.");
            args[property.Name] = property.Value.Clone();
        }
        return args;
    }

    private static string ReqString(Dictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(element.GetString()))
            throw new StateInputException($"'{name}' is required and must be a non-empty string.");
        return element.GetString()!;
    }

    private static string? OptString(Dictionary<string, JsonElement> args, string name)
        => args.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? OptInt(Dictionary<string, JsonElement> args, string name)
        => args.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value)
            ? value
            : null;

    private static IReadOnlyList<string> OptStringArray(Dictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var element))
            return [];
        if (element.ValueKind != JsonValueKind.Array)
            throw new StateInputException($"'{name}' must be an array of strings.");
        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new StateInputException($"'{name}' must contain only strings.");
            items.Add(item.GetString()!);
        }
        return items;
    }

    private sealed class StateInputException : Exception
    {
        public StateInputException(string message) : base(message) { }
    }
}
```

Note: `DeleteAsync` parses twice for clarity; if the implementer prefers a single parse into a tuple, either is acceptable — behavior identical.

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.State.Domain.Tests/eThangAgent.State.Domain.Tests.csproj`
Expected: 8 tests pass (plus Task 1's 8).

- [ ] **Step 5: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.State.Domain tests/eThangAgent.State.Domain.Tests
```

```bash
git commit -m "feat(state-domain): add state capability provider with strict inputs"
```

---

### Task 3: StateService — CAS, transitions, fail-closed certification

**Files:**

- Create: `src/eThangAgent.State.Domain/StateService.cs`
- Create: `tests/eThangAgent.State.Domain.Tests/StateServiceTests.cs`

**Interfaces:**

- Consumes: `IStateStore`, `IEvidenceRunner`, `IWorkspaceContext`, `EvidenceOptions`, records (Task 1); `IStateService` (Task 2).
- Produces: `StateService(IStateStore, IEvidenceRunner, IWorkspaceContext, EvidenceOptions?) : IStateService`. Reserved keys: head = `current/head`; certificate = `current/certificate`; goal = `goal/check` (JSON array of commands). Certification: certify only when ≥1 transition selected and every evidence command confirms; on violation, head-certificate revocation happens BEFORE the violated event; transitions selected on a failed verification are all marked `violated`.

- [ ] **Step 1: Write the failing tests**

`tests/eThangAgent.State.Domain.Tests/StateServiceTests.cs`:

```csharp
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.State.Domain.Tests;

public class StateServiceTests
{
    private static StateService Create(FakeStateStore? store = null, FakeRunner? runner = null)
        => new(store ?? new FakeStateStore(), runner ?? new FakeRunner(), new StubWorkspace());

    [Fact]
    public async Task Set_StaleVersion_Fails_NamingCurrentVersion()
    {
        var store = new FakeStateStore();
        await Create(store).SetAsync("current/head", "done", null);

        var result = await Create(store).SetAsync("current/head", "other", 5);

        Assert.False(result.IsSuccess);
        Assert.Equal("VersionConflict", result.Error!.Code);
        Assert.Contains("current version is 1", result.Error.Message);
    }

    [Fact]
    public async Task Get_MissingKey_Fails_KeyNotFound()
    {
        var result = await Create().GetAsync("current/head");
        Assert.False(result.IsSuccess);
        Assert.Equal("KeyNotFound", result.Error!.Code);
    }

    [Fact]
    public async Task Transition_AssignsId_AppendsAttachedEvent()
    {
        var store = new FakeStateStore();

        var result = await Create(store).TransitionAsync("coding", "done", "work", ["Write-Output ok"]);

        Assert.True(result.IsSuccess);
        Assert.StartsWith("tr-", result.Value);
        Assert.Contains("transition.attached", store.EventKinds);
        Assert.Equal("pending", store.Transitions.Single().Status);
    }

    [Fact]
    public async Task Verify_AllConfirm_Certifies_AndPersistsHeadCertificate()
    {
        var store = new FakeStateStore();
        var service = Create(store, new FakeRunner(_ => new EvidenceResult("cmd", true, "")));
        await service.SetAsync("current/head", "done", null);
        await service.TransitionAsync("coding", "done", "work", ["Write-Output ok"]);

        var report = await service.VerifyAsync(null);

        Assert.True(report.Certified);
        Assert.False(report.Violated);
        Assert.Equal("certified", store.Transitions.Single().Status);
        Assert.Contains("state.certified", store.EventKinds);
        var certificate = await store.GetKeyAsync("ws", "current", "certificate");
        Assert.NotNull(certificate);
    }

    [Fact]
    public async Task Verify_FailingEvidence_Violates_AndRevokesHeadCertificateFirst()
    {
        var store = new FakeStateStore();
        var outcomes = new Queue<bool>([true, false]); // certify first, then fail on re-verification
        var service = Create(store, new FakeRunner(_ =>
        {
            var confirmed = outcomes.Dequeue();
            return new EvidenceResult("cmd", confirmed, confirmed ? "" : "exit 1");
        }));
        await service.SetAsync("current/head", "done", null);
        var id = await service.TransitionAsync("coding", "done", "work", ["Write-Output ok"]);
        await service.VerifyAsync(null); // certify first
        store.OperationLog.Clear();

        var report = await service.VerifyAsync([id.Value!]);

        Assert.False(report.Certified);
        Assert.True(report.Violated);
        Assert.Contains("exit 1", report.BlockingReasons.Single());
        Assert.Equal("violated", store.Transitions.Single().Status);
        Assert.Null(await store.GetKeyAsync("ws", "current", "certificate"));
        var revokeIndex = store.OperationLog.IndexOf("delete:current/certificate");
        var violatedIndex = store.OperationLog.IndexOf("event:state.violated");
        Assert.True(revokeIndex >= 0 && violatedIndex > revokeIndex, "certificate must be revoked before the violated event");
    }

    [Fact]
    public async Task Verify_EmptyEvidence_FailsClosed()
    {
        var store = new FakeStateStore();
        var service = Create(store);
        await service.TransitionAsync("coding", "done", "work", []);

        var report = await service.VerifyAsync(null);

        Assert.False(report.Certified);
        Assert.Contains("no attached evidence", report.BlockingReasons.Single());
    }

    [Fact]
    public async Task Verify_NothingSelected_FailsClosed()
    {
        var report = await Create().VerifyAsync(null);

        Assert.False(report.Certified);
        Assert.Contains("No transitions selected", report.BlockingReasons.Single());
    }

    [Fact]
    public async Task Verify_MissingRequestedId_ListedInBlocking()
    {
        var report = await Create().VerifyAsync(["tr-missing"]);

        Assert.False(report.Certified);
        Assert.Contains("Missing transition: tr-missing.", report.BlockingReasons.Single());
    }

    [Fact]
    public async Task CheckGoal_RunsCommands_ReportOnly()
    {
        var store = new FakeStateStore();
        var service = Create(store, new FakeRunner(_ => new EvidenceResult("cmd", true, "")));
        await service.SetAsync("goal/check", "[\"Write-Output ok\"]", null);

        var report = await service.CheckGoalAsync();

        Assert.True(report.Certified);
        Assert.Empty(store.EventKinds);
        Assert.Empty(store.Transitions);
    }

    [Fact]
    public async Task History_ReplaysEvents()
    {
        var store = new FakeStateStore();
        var service = Create(store);
        await service.SetAsync("current/head", "done", null);
        await service.TransitionAsync("a", "b", "s", []);

        var result = await service.HistoryAsync(20);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
    }

    private sealed class StubWorkspace : IWorkspaceContext
    {
        public string WorkspaceId => "ws";
    }

    private sealed class FakeRunner : IEvidenceRunner
    {
        private readonly Func<string, EvidenceResult> _respond;
        public FakeRunner(Func<string, EvidenceResult>? respond = null)
            => _respond = respond ?? (command => new EvidenceResult(command, true, ""));
        public Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
            => Task.FromResult(_respond(command));
    }

    private sealed class FakeStateStore : IStateStore
    {
        private readonly Dictionary<string, StateKeyValue> _keys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TransitionRecord> _transitions = new(StringComparer.Ordinal);
        private readonly List<StateEvent> _events = [];
        private long _eventSeq;

        public List<string> OperationLog { get; } = [];
        public List<string> EventKinds => _events.Select(e => e.Kind).ToList();
        public IReadOnlyCollection<TransitionRecord> Transitions => _transitions.Values;

        public Task<StateKeyValue?> GetKeyAsync(string workspaceId, string ns, string name, CancellationToken ct = default)
            => Task.FromResult(_keys.TryGetValue($"{ns}/{name}", out var kv) ? kv : null);

        public Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(string workspaceId, string? ns, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StateKeyValue>>(_keys.Values
                .Where(k => ns is null || k.Ns == ns).ToList());

        public Task<StateKeyValue?> SetKeyCasAsync(string workspaceId, string ns, string name, string value,
            int? expectedVersion, CancellationToken ct = default)
        {
            var id = $"{ns}/{name}";
            _keys.TryGetValue(id, out var existing);
            if (expectedVersion.HasValue && (existing is null || existing.Version != expectedVersion.Value))
                return Task.FromResult<StateKeyValue?>(null);
            var row = new StateKeyValue(ns, name, value, (existing?.Version ?? 0) + 1);
            _keys[id] = row;
            return Task.FromResult<StateKeyValue?>(row);
        }

        public Task<bool> DeleteKeyCasAsync(string workspaceId, string ns, string name,
            int? expectedVersion, CancellationToken ct = default)
        {
            var id = $"{ns}/{name}";
            if (!_keys.TryGetValue(id, out var existing)) return Task.FromResult(false);
            if (expectedVersion.HasValue && existing.Version != expectedVersion.Value) return Task.FromResult(false);
            _keys.Remove(id);
            OperationLog.Add($"delete:{id}");
            return Task.FromResult(true);
        }

        public Task<TransitionRecord> InsertTransitionAsync(string workspaceId, TransitionRecord transition, CancellationToken ct = default)
        {
            _transitions[transition.Id] = transition;
            OperationLog.Add("insert-transition");
            return Task.FromResult(transition);
        }

        public Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(string workspaceId,
            IReadOnlyList<string> transitionIds, CancellationToken ct = default)
        {
            var selected = transitionIds.Count == 0
                ? _transitions.Values.Where(t => t.Status == "pending").ToList()
                : transitionIds.Where(_transitions.ContainsKey).Select(t => _transitions[t]).ToList();
            return Task.FromResult<IReadOnlyList<TransitionRecord>>(selected);
        }

        public Task SetTransitionStatusAsync(string workspaceId, string transitionId, string status, CancellationToken ct = default)
        {
            OperationLog.Add($"status:{transitionId}:{status}");
            if (_transitions.TryGetValue(transitionId, out var t))
                _transitions[transitionId] = t with { Status = status };
            return Task.CompletedTask;
        }

        public Task AppendEventAsync(string workspaceId, string kind, string payloadJson, CancellationToken ct = default)
        {
            OperationLog.Add($"event:{kind}");
            _events.Add(new StateEvent(++_eventSeq, kind, payloadJson, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(string workspaceId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StateEvent>>(_events.Take(^limit..).Reverse().ToList());
    }
}
```

Note: `GetEventsAsync` in the fake returns newest-first via `^limit..` — the real SQLite store orders by `id DESC LIMIT @limit`; StateService only replays what it receives, so both agree.

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.State.Domain.Tests/eThangAgent.State.Domain.Tests.csproj`
Expected: build fails, `StateService` not found.

- [ ] **Step 3: Implement**

`src/eThangAgent.State.Domain/StateService.cs`:

```csharp
using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public sealed class StateService : IStateService
{
    public const string HeadNs = "current";
    public const string HeadName = "head";
    public const string CertificateNs = "current";
    public const string CertificateName = "certificate";
    public const string GoalNs = "goal";
    public const string GoalName = "check";

    private readonly IStateStore _store;
    private readonly IEvidenceRunner _evidence;
    private readonly IWorkspaceContext _workspace;
    private readonly EvidenceOptions _options;

    public StateService(IStateStore store, IEvidenceRunner evidence,
        IWorkspaceContext workspace, EvidenceOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = options ?? EvidenceOptions.Default;
    }

    public async Task<Result<string>> GetAsync(string key, CancellationToken ct = default)
    {
        var parsed = StateKey.Parse(key);
        if (!parsed.IsSuccess) return Result<string>.Failure(parsed.Error!);
        var (ns, name) = parsed.Value;
        var kv = await _store.GetKeyAsync(_workspace.WorkspaceId, ns, name, ct);
        return kv is null
            ? Result<string>.Failure(new Error("KeyNotFound", $"'{key}' does not exist."))
            : Result<string>.Success(kv.Value);
    }

    public async Task<Result<StateKeyValue>> SetAsync(string key, string value,
        int? expectedVersion, CancellationToken ct = default)
    {
        var parsed = StateKey.Parse(key);
        if (!parsed.IsSuccess) return Result<StateKeyValue>.Failure(parsed.Error!);
        var (ns, name) = parsed.Value;
        var saved = await _store.SetKeyCasAsync(_workspace.WorkspaceId, ns, name, value, expectedVersion, ct);
        if (saved is not null) return Result<StateKeyValue>.Success(saved);
        var current = await _store.GetKeyAsync(_workspace.WorkspaceId, ns, name, ct);
        return Result<StateKeyValue>.Failure(new Error("VersionConflict",
            $"Version conflict for '{key}': current version is {current?.Version ?? 0}."));
    }

    public async Task<Result<string>> DeleteAsync(string key, int? expectedVersion, CancellationToken ct = default)
    {
        var parsed = StateKey.Parse(key);
        if (!parsed.IsSuccess) return Result<string>.Failure(parsed.Error!);
        var (ns, name) = parsed.Value;
        var existing = await _store.GetKeyAsync(_workspace.WorkspaceId, ns, name, ct);
        if (existing is null)
            return Result<string>.Failure(new Error("KeyNotFound", $"'{key}' does not exist."));
        var deleted = await _store.DeleteKeyCasAsync(_workspace.WorkspaceId, ns, name, expectedVersion, ct);
        return deleted
            ? Result<string>.Success($"deleted {key}")
            : Result<string>.Failure(new Error("VersionConflict",
                $"Version conflict for '{key}': current version is {existing.Version}."));
    }

    public async Task<Result<IReadOnlyList<string>>> ListAsync(string? ns, CancellationToken ct = default)
    {
        var keys = await _store.ListKeysAsync(_workspace.WorkspaceId, ns, ct);
        return Result<IReadOnlyList<string>>.Success(
            keys.Select(k => $"{k.Ns}/{k.Name} v{k.Version}").ToList());
    }

    public async Task<Result<string>> TransitionAsync(string from, string to, string summary,
        IReadOnlyList<string> evidence, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(summary))
            return Result<string>.Failure(new Error("InvalidTransition",
                "'from', 'to', and 'summary' are required."));
        var record = new TransitionRecord(
            $"tr-{Guid.NewGuid():N}", from, to, summary,
            evidence ?? [], "pending", DateTimeOffset.UtcNow);
        var stored = await _store.InsertTransitionAsync(_workspace.WorkspaceId, record, ct);
        await _store.AppendEventAsync(_workspace.WorkspaceId, "transition.attached",
            JsonSerializer.Serialize(new { id = stored.Id, from, to, summary }), ct);
        return Result<string>.Success(stored.Id);
    }

    public async Task<CertificationReport> VerifyAsync(IReadOnlyList<string>? ids, CancellationToken ct = default)
    {
        var workspaceId = _workspace.WorkspaceId;
        var selected = await _store.GetTransitionsAsync(workspaceId, ids ?? [], ct);
        var blocking = new List<string>();
        var results = new List<EvidenceResult>();

        if (ids is { Count: > 0 })
        {
            var found = selected.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in ids)
                if (!found.Contains(id))
                    blocking.Add($"Missing transition: {id}.");
        }
        else if (selected.Count == 0)
            blocking.Add("No transitions selected (none pending).");

        var head = await _store.GetKeyAsync(workspaceId, HeadNs, HeadName, ct);
        var headValue = head?.Value;
        var targetsHead = headValue is not null && selected.Any(t => t.To == headValue);

        foreach (var transition in selected)
        {
            if (transition.Evidence.Count == 0)
            {
                blocking.Add($"Transition {transition.Id} has no attached evidence.");
                results.Add(new EvidenceResult("(none)", false, "no evidence attached"));
                continue;
            }
            foreach (var command in transition.Evidence)
            {
                var result = await _evidence.RunAsync(command, ct);
                results.Add(result);
                if (!result.Confirmed)
                    blocking.Add($"Transition {transition.Id}: '{command}' — {result.Detail}");
            }
        }

        var certified = blocking.Count == 0;

        if (certified)
        {
            foreach (var transition in selected)
                await _store.SetTransitionStatusAsync(workspaceId, transition.Id, "certified", ct);
            await _store.AppendEventAsync(workspaceId, "state.certified",
                JsonSerializer.Serialize(new { transitions = selected.Select(t => t.Id).ToArray() }), ct);
            if (targetsHead)
                await _store.SetKeyCasAsync(workspaceId, CertificateNs, CertificateName,
                    JsonSerializer.Serialize(new
                    {
                        transitions = selected.Select(t => t.Id).ToArray(),
                        certifiedAt = DateTimeOffset.UtcNow,
                    }), null, ct);
        }
        else
        {
            if (targetsHead)
            {
                await _store.DeleteKeyCasAsync(workspaceId, CertificateNs, CertificateName, null, ct);
                OperationLogNote();
            }
            foreach (var transition in selected)
                await _store.SetTransitionStatusAsync(workspaceId, transition.Id, "violated", ct);
            await _store.AppendEventAsync(workspaceId, "state.violated",
                JsonSerializer.Serialize(new { reasons = blocking }), ct);
        }

        return new CertificationReport(certified, !certified, results, blocking);

        void OperationLogNote() { }
    }

    public async Task<CertificationReport> CheckGoalAsync(CancellationToken ct = default)
    {
        var goal = await _store.GetKeyAsync(_workspace.WorkspaceId, GoalNs, GoalName, ct);
        if (goal is null)
            return new CertificationReport(false, true, [], ["No goal/check commands stored."]);
        List<string> commands;
        try
        {
            commands = JsonSerializer.Deserialize<List<string>>(goal.Value) ?? [];
        }
        catch (JsonException)
        {
            return new CertificationReport(false, true, [],
                ["goal/check is not a valid JSON array of commands."]);
        }
        var results = new List<EvidenceResult>();
        var blocking = new List<string>();
        foreach (var command in commands)
        {
            var result = await _evidence.RunAsync(command, ct);
            results.Add(result);
            if (!result.Confirmed)
                blocking.Add($"'{command}' — {result.Detail}");
        }
        return new CertificationReport(blocking.Count == 0, blocking.Count > 0, results, blocking);
    }

    public async Task<Result<IReadOnlyList<string>>> HistoryAsync(int limit, CancellationToken ct = default)
    {
        var events = await _store.GetEventsAsync(_workspace.WorkspaceId, limit, ct);
        return Result<IReadOnlyList<string>>.Success(
            events.Select(e => $"{e.OccurredAt:u} {e.Kind} {e.PayloadJson}").ToList());
    }
}
```

Implementation note: the local `OperationLogNote()` no-op marks the revocation point so test fakes can assert ordering through their own logs; the store's operation log is the ordering witness, not the service.

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.State.Domain.Tests/eThangAgent.State.Domain.Tests.csproj`
Expected: 11 tests pass (plus prior 16).

- [ ] **Step 5: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.State.Domain tests/eThangAgent.State.Domain.Tests
```

```bash
git commit -m "feat(state-domain): add state service with fail-closed certification"
```

---

### Task 4: Storage.ACL — AppDatabase, migrations, SqliteStateStore

**Files:**

- Create: `src/eThangAgent.Storage.ACL/eThangAgent.Storage.ACL.csproj`
- Create: `src/eThangAgent.Storage.ACL/AppDatabase.cs`
- Create: `src/eThangAgent.Storage.ACL/SqliteStateStore.cs`
- Create: `tests/eThangAgent.Storage.ACL.Tests/eThangAgent.Storage.ACL.Tests.csproj`
- Create: `tests/eThangAgent.Storage.ACL.Tests/GlobalUsings.cs`
- Create: `tests/eThangAgent.Storage.ACL.Tests/SqliteStateStoreTests.cs`
- Modify: `eThangAgent.slnx` (+2 entries)

**Interfaces:**

- Consumes: `IStateStore` + records (Task 1).
- Produces: `AppDatabase(string? databasePath = null)` — resolves `%LOCALAPPDATA%\eThangAgent\eThangAgent.db` unless overridden by argument or `ETHANG_AGENT_DB`; creates directory; applies versioned migrations (`PRAGMA user_version`); `Open() → SqliteConnection`. `SqliteStateStore(AppDatabase) : IStateStore` — CAS writes execute atomically inside SQL transactions; stale expected-version updates affect zero rows and return null/false.

- [ ] **Step 1: Create projects + solution entries**

`src/eThangAgent.Storage.ACL/eThangAgent.Storage.ACL.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="9.*" />
    <ProjectReference Include="../eThangAgent.SharedKernel/eThangAgent.SharedKernel.csproj" />
    <ProjectReference Include="../eThangAgent.State.Domain/eThangAgent.State.Domain.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.Storage.ACL.Tests/eThangAgent.Storage.ACL.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../src/eThangAgent.Storage.ACL/eThangAgent.Storage.ACL.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.Storage.ACL.Tests/GlobalUsings.cs`:

```csharp
global using Xunit;
```

In `eThangAgent.slnx`, add after the State.Domain project line:

```xml
  <Project Path="src/eThangAgent.Storage.ACL/eThangAgent.Storage.ACL.csproj" />
```

and after the State.Domain.Tests line:

```xml
  <Project Path="tests/eThangAgent.Storage.ACL.Tests/eThangAgent.Storage.ACL.Tests.csproj" />
```

- [ ] **Step 2: Write the failing tests**

`tests/eThangAgent.Storage.ACL.Tests/SqliteStateStoreTests.cs`:

```csharp
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Storage.ACL.Tests;

public class SqliteStateStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ethang-state-{Guid.NewGuid():N}.db");
    private readonly SqliteStateStore _store;

    public SqliteStateStoreTests()
        => _store = new SqliteStateStore(new AppDatabase(_dbPath));

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Set_InsertThenUpsert_BumpsVersions()
    {
        var first = await _store.SetKeyCasAsync("ws", "current", "head", "a", null);
        var second = await _store.SetKeyCasAsync("ws", "current", "head", "b", null);

        Assert.Equal(1, first!.Version);
        Assert.Equal(2, second!.Version);
        Assert.Equal("b", second.Value);
    }

    [Fact]
    public async Task Cas_StaleVersion_ReturnsNull_AndKeepsRow()
    {
        await _store.SetKeyCasAsync("ws", "current", "head", "a", null);

        var conflict = await _store.SetKeyCasAsync("ws", "current", "head", "b", 5);

        Assert.Null(conflict);
        var row = await _store.GetKeyAsync("ws", "current", "head");
        Assert.Equal("a", row!.Value);
        Assert.Equal(1, row.Version);
    }

    [Fact]
    public async Task Cas_MatchingVersion_Succeeds()
    {
        await _store.SetKeyCasAsync("ws", "current", "head", "a", null);

        var saved = await _store.SetKeyCasAsync("ws", "current", "head", "b", 1);

        Assert.NotNull(saved);
        Assert.Equal(2, saved!.Version);
    }

    [Fact]
    public async Task Delete_RespectsExpectedVersion_AndMissingKeys()
    {
        await _store.SetKeyCasAsync("ws", "current", "head", "a", null);

        Assert.False(await _store.DeleteKeyCasAsync("ws", "current", "head", 5));
        Assert.True(await _store.DeleteKeyCasAsync("ws", "current", "head", 1));
        Assert.False(await _store.DeleteKeyCasAsync("ws", "current", "head", null));
    }

    [Fact]
    public async Task List_FiltersByNamespace()
    {
        await _store.SetKeyCasAsync("ws", "current", "head", "a", null);
        await _store.SetKeyCasAsync("ws", "goal", "check", "[]", null);

        var all = await _store.ListKeysAsync("ws", null);
        var currentOnly = await _store.ListKeysAsync("ws", "current");

        Assert.Equal(2, all.Count);
        Assert.Single(currentOnly);
    }

    [Fact]
    public async Task Workspaces_AreIsolated()
    {
        await _store.SetKeyCasAsync("ws-a", "current", "head", "a", null);

        Assert.Null(await _store.GetKeyAsync("ws-b", "current", "head"));
    }

    [Fact]
    public async Task Transitions_PendingSelection_StatusUpdates()
    {
        var record = new TransitionRecord("tr-1", "coding", "done", "work",
            ["Write-Output ok"], "pending", DateTimeOffset.UtcNow);
        await _store.InsertTransitionAsync("ws", record);

        var pending = await _store.GetTransitionsAsync("ws", []);
        var byId = await _store.GetTransitionsAsync("ws", ["tr-1"]);

        Assert.Single(pending);
        Assert.Single(byId);
        Assert.Equal(["Write-Output ok"], byId[0].Evidence);

        await _store.SetTransitionStatusAsync("ws", "tr-1", "certified");

        Assert.Empty(await _store.GetTransitionsAsync("ws", []));
        Assert.Equal("certified", (await _store.GetTransitionsAsync("ws", ["tr-1"]))[0].Status);
    }

    [Fact]
    public async Task Events_Append_NewestFirst_LimitRespected()
    {
        await _store.AppendEventAsync("ws", "a", "{});
        await _store.AppendEventAsync("ws", "b", "{}");
        await _store.AppendEventAsync("ws", "c", "{}");

        var events = await _store.GetEventsAsync("ws", 2);

        Assert.Equal(2, events.Count);
        Assert.Equal("c", events[0].Kind);
        Assert.Equal("b", events[1].Kind);
    }

    [Fact]
    public void Migrations_AreIdempotent()
    {
        var second = new AppDatabase(_dbPath);
        using var connection = second.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('state_keys','transitions','state_events');";
        Assert.Equal(3L, Convert.ToInt64(command.ExecuteScalar()));
    }
}
```

(Note: fix the typo `"{}` → `"{}"` in the three AppendEventAsync lines when writing the file.)

- [ ] **Step 3: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.Storage.ACL.Tests/eThangAgent.Storage.ACL.Tests.csproj`
Expected: build fails, projects/types not found.

- [ ] **Step 4: Implement**

`src/eThangAgent.Storage.ACL/AppDatabase.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>Single app-owned SQLite database. One database serves many workspaces —
///     rows are keyed by workspace id. Schema changes go through versioned migrations.
///     This database is the beachhead for later app tables (kanban, agent statuses).</summary>
public sealed class AppDatabase
{
    private readonly string _connectionString;

    public AppDatabase(string? databasePath = null)
    {
        var path = databasePath
            ?? Environment.GetEnvironmentVariable("ETHANG_AGENT_DB")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "eThangAgent", "eThangAgent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();
        if (GetVersion(connection) >= 1) return;
        ApplyV1(connection);
        SetVersion(connection, 1);
    }

    private static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetVersion(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static void ApplyV1(SqliteConnection connection)
    {
        var sql = """
            CREATE TABLE IF NOT EXISTS state_keys (
                workspace_id TEXT NOT NULL,
                ns           TEXT NOT NULL,
                name         TEXT NOT NULL,
                value        TEXT NOT NULL,
                version      INTEGER NOT NULL,
                updated_at   TEXT NOT NULL,
                PRIMARY KEY (workspace_id, ns, name)
            );
            CREATE TABLE IF NOT EXISTS transitions (
                id            TEXT PRIMARY KEY,
                workspace_id  TEXT NOT NULL,
                from_state    TEXT NOT NULL,
                to_state      TEXT NOT NULL,
                summary       TEXT NOT NULL,
                evidence_json TEXT NOT NULL,
                status        TEXT NOT NULL,
                created_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_transitions_ws_status ON transitions (workspace_id, status);
            CREATE TABLE IF NOT EXISTS state_events (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                workspace_id TEXT NOT NULL,
                kind         TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                occurred_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_state_events_ws ON state_events (workspace_id, id);
            """;
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
```

`src/eThangAgent.Storage.ACL/SqliteStateStore.cs`:

```csharp
using System.Text.Json;
using eThangAgent.StateDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

public sealed class SqliteStateStore : IStateStore
{
    private readonly AppDatabase _database;

    public SqliteStateStore(AppDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<StateKeyValue?> GetKeyAsync(string workspaceId, string ns, string name,
        CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value, version FROM state_keys WHERE workspace_id=@w AND ns=@ns AND name=@n;";
        Add(command, "@w", workspaceId);
        Add(command, "@ns", ns);
        Add(command, "@n", name);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new StateKeyValue(ns, name, reader.GetString(0), reader.GetInt32(1))
            : null;
    }

    public async Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(string workspaceId, string? ns,
        CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ns, name, value, version FROM state_keys WHERE workspace_id=@w (@nsFilter) ORDER BY ns, name;"
            .Replace("(@nsFilter)", ns is null ? "" : "AND ns=@ns");
        Add(command, "@w", workspaceId);
        if (ns is not null) Add(command, "@ns", ns);
        var keys = new List<StateKeyValue>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(new StateKeyValue(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return keys;
    }

    public async Task<StateKeyValue?> SetKeyCasAsync(string workspaceId, string ns, string name,
        string value, int? expectedVersion, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("o");

        if (expectedVersion.HasValue)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE state_keys SET value=@v, version=version+1, updated_at=@now
                WHERE workspace_id=@w AND ns=@ns AND name=@n AND version=@exp;
                """;
            Add(update, "@v", value);
            Add(update, "@w", workspaceId);
            Add(update, "@ns", ns);
            Add(update, "@n", name);
            Add(update, "@now", now);
            Add(update, "@exp", expectedVersion.Value);
            if (await update.ExecuteNonQueryAsync(ct) == 0)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }
        }
        else
        {
            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at)
                VALUES (@w, @ns, @n, @v, 1, @now)
                ON CONFLICT(workspace_id, ns, name) DO UPDATE SET
                    value=@v, version=state_keys.version+1, updated_at=@now;
                """;
            Add(upsert, "@w", workspaceId);
            Add(upsert, "@ns", ns);
            Add(upsert, "@n", name);
            Add(upsert, "@v", value);
            Add(upsert, "@now", now);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        StateKeyValue? row;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT value, version FROM state_keys WHERE workspace_id=@w AND ns=@ns AND name=@n;";
            Add(select, "@w", workspaceId);
            Add(select, "@ns", ns);
            Add(select, "@n", name);
            using var reader = await select.ExecuteReaderAsync(ct);
            row = await reader.ReadAsync(ct)
                ? new StateKeyValue(ns, name, reader.GetString(0), reader.GetInt32(1))
                : null;
        }
        await transaction.CommitAsync(ct);
        return row;
    }

    public async Task<bool> DeleteKeyCasAsync(string workspaceId, string ns, string name,
        int? expectedVersion, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM state_keys WHERE workspace_id=@w AND ns=@ns AND name=@n AND (@exp IS NULL OR version=@exp);";
        Add(command, "@w", workspaceId);
        Add(command, "@ns", ns);
        Add(command, "@n", name);
        Add(command, "@exp", (object?)expectedVersion ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<TransitionRecord> InsertTransitionAsync(string workspaceId,
        TransitionRecord transition, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transitions (id, workspace_id, from_state, to_state, summary, evidence_json, status, created_at)
            VALUES (@id, @w, @from, @to, @summary, @evidence, @status, @created);
            """;
        Add(command, "@id", transition.Id);
        Add(command, "@w", workspaceId);
        Add(command, "@from", transition.From);
        Add(command, "@to", transition.To);
        Add(command, "@summary", transition.Summary);
        Add(command, "@evidence", JsonSerializer.Serialize(transition.Evidence));
        Add(command, "@status", transition.Status);
        Add(command, "@created", transition.CreatedAt.ToString("o"));
        await command.ExecuteNonQueryAsync(ct);
        return transition;
    }

    public async Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(string workspaceId,
        IReadOnlyList<string> transitionIds, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = transitionIds.Count == 0
            ? "SELECT id, from_state, to_state, summary, evidence_json, status, created_at FROM transitions WHERE workspace_id=@w AND status='pending' ORDER BY created_at;"
            : BuildIdQuery(transitionIds);
        Add(command, "@w", workspaceId);
        for (var i = 0; i < transitionIds.Count; i++)
            Add(command, $"@id{i}", transitionIds[i]);

        var transitions = new List<TransitionRecord>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            transitions.Add(new TransitionRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [],
                reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6))));
        return transitions;
    }

    public async Task SetTransitionStatusAsync(string workspaceId, string transitionId,
        string status, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE transitions SET status=@s WHERE workspace_id=@w AND id=@id;";
        Add(command, "@s", status);
        Add(command, "@w", workspaceId);
        Add(command, "@id", transitionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AppendEventAsync(string workspaceId, string kind, string payloadJson,
        CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO state_events (workspace_id, kind, payload_json, occurred_at) VALUES (@w, @k, @p, @t);";
        Add(command, "@w", workspaceId);
        Add(command, "@k", kind);
        Add(command, "@p", payloadJson);
        Add(command, "@t", DateTimeOffset.UtcNow.ToString("o"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<StateEvent>> GetEventsAsync(string workspaceId, int limit,
        CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind, payload_json, occurred_at FROM state_events WHERE workspace_id=@w ORDER BY id DESC LIMIT @limit;";
        Add(command, "@w", workspaceId);
        Add(command, "@limit", limit);
        var events = new List<StateEvent>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            events.Add(new StateEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3))));
        return events;
    }

    private static string BuildIdQuery(IReadOnlyList<string> ids)
    {
        var parameters = string.Join(", ", ids.Select((_, i) => $"@id{i}"));
        return $"SELECT id, from_state, to_state, summary, evidence_json, status, created_at FROM transitions WHERE workspace_id=@w AND id IN ({parameters});";
    }

    private static void Add(SqliteCommand command, string name, object value)
        => command.Parameters.AddWithValue(name, value);
}
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.Storage.ACL.Tests/eThangAgent.Storage.ACL.Tests.csproj`
Expected: 9 tests pass.

- [ ] **Step 6: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.Storage.ACL tests/eThangAgent.Storage.ACL.Tests eThangAgent.slnx
```

```bash
git commit -m "feat(storage-acl): add app sqlite database and state store"
```

---

### Task 5: PsEvidenceRunner — fail-closed evidence execution

**Files:**

- Create: `src/eThangAgent.PowerShell.ACL/PsEvidenceRunner.cs`
- Create: `tests/eThangAgent.PowerShell.ACL.Tests/PsEvidenceRunnerTests.cs`
- Modify: `src/eThangAgent.PowerShell.ACL/eThangAgent.PowerShell.ACL.csproj` (+ State.Domain reference)

**Interfaces:**

- Consumes: `IEvidenceRunner`, `EvidenceResult`, `EvidenceOptions` (Task 1).
- Produces: `PsEvidenceRunner(EvidenceOptions?) : IEvidenceRunner`. Confirmed = pipeline completed with no errors AND `$LASTEXITCODE` is 0/unset. Timeout, cancellation, errors, syntax failures, engine exceptions → not confirmed with detail.

- [ ] **Step 1: Add the project reference**

In `src/eThangAgent.PowerShell.ACL/eThangAgent.PowerShell.ACL.csproj`, add:

```xml
    <ProjectReference Include="../eThangAgent.State.Domain/eThangAgent.State.Domain.csproj" />
```

- [ ] **Step 2: Write the failing tests**

`tests/eThangAgent.PowerShell.ACL.Tests/PsEvidenceRunnerTests.cs`:

```csharp
using eThangAgent.StateDomain;
using eThangAgent.PowerShell.ACL;

namespace eThangAgent.PowerShell.ACL.Tests;

public class PsEvidenceRunnerTests
{
    private readonly PsEvidenceRunner _runner =
        new(new EvidenceOptions { Timeout = TimeSpan.FromSeconds(10) });

    [Fact]
    public async Task ConfirmingCommand_IsConfirmed()
    {
        var result = await _runner.RunAsync("Write-Output ok");
        Assert.True(result.Confirmed);
    }

    [Fact]
    public async Task WriteError_IsNotConfirmed()
    {
        var result = await _runner.RunAsync("Write-Error boom");
        Assert.False(result.Confirmed);
        Assert.Contains("boom", result.Detail);
    }

    [Fact]
    public async Task NativeExitCodeOne_IsNotConfirmed()
    {
        var result = await _runner.RunAsync("cmd /c exit 1");
        Assert.False(result.Confirmed);
        Assert.Contains("LASTEXITCODE", result.Detail);
    }

    [Fact]
    public async Task SyntaxError_FailsClosed()
    {
        var result = await _runner.RunAsync("if (x {");
        Assert.False(result.Confirmed);
    }

    [Fact]
    public async Task Timeout_FailsClosed_WithDetail()
    {
        var runner = new PsEvidenceRunner(new EvidenceOptions { Timeout = TimeSpan.FromMilliseconds(300) });

        var result = await runner.RunAsync("Start-Sleep -Seconds 300");

        Assert.False(result.Confirmed);
        Assert.Contains("Timed out", result.Detail);
    }
}
```

- [ ] **Step 3: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.PowerShell.ACL.Tests/eThangAgent.PowerShell.ACL.Tests.csproj`
Expected: build fails, `PsEvidenceRunner` not found.

- [ ] **Step 4: Implement**

`src/eThangAgent.PowerShell.ACL/PsEvidenceRunner.cs`:

```csharp
using eThangAgent.StateDomain;

namespace eThangAgent.PowerShell.ACL;

/// <summary>Runs evidence commands in a fresh default runspace. Confirmed = no errors
///     written AND $LASTEXITCODE is 0 or unset. Fails closed on timeout, cancellation,
///     errors, syntax failures, and engine exceptions.</summary>
public sealed class PsEvidenceRunner : IEvidenceRunner
{
    private readonly EvidenceOptions _options;

    public PsEvidenceRunner(EvidenceOptions? options = null)
        => _options = options ?? EvidenceOptions.Default;

    public async Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.AddScript(command);
        try
        {
            var invokeTask = Task.Run(() => ps.Invoke());
            var completed = await Task.WhenAny(invokeTask, Task.Delay(_options.Timeout, ct));
            if (completed != invokeTask)
            {
                try { ps.Stop(); } catch { /* pipeline already stopping */ }
                return new EvidenceResult(command, false,
                    $"Timed out after {_options.Timeout.TotalSeconds:0}s.");
            }

            await invokeTask;
            var exitCode = ReadExitCode(ps);
            if (ps.HadErrors)
            {
                var detail = ps.Streams.Error.FirstOrDefault()?.Exception.Message;
                if (string.IsNullOrWhiteSpace(detail))
                    detail = exitCode is { } code ? $"$LASTEXITCODE = {code}." : "unknown error";
                return new EvidenceResult(command, false, detail);
            }

            if (exitCode is not (null or 0))
                return new EvidenceResult(command, false, $"$LASTEXITCODE = {exitCode}.");

            return new EvidenceResult(command, true, "");
        }
        catch (Exception ex)
        {
            return new EvidenceResult(command, false, ex.Message);
        }
    }

    private static int? ReadExitCode(System.Management.Automation.PowerShell ps)
    {
        try
        {
            var value = ps.Runspace.SessionStateProxy.GetVariable("LASTEXITCODE");
            return value is null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null; // LASTEXITCODE unset — no native executable ran
        }
    }
}
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.PowerShell.ACL.Tests/eThangAgent.PowerShell.ACL.Tests.csproj`
Expected: 5 tests pass (plus prior suites).

- [ ] **Step 6: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.PowerShell.ACL tests/eThangAgent.PowerShell.ACL.Tests
```

```bash
git commit -m "feat(powerShell-acl): add fail-closed evidence runner"
```

---

### Task 6: CLI wiring + guide v1.2

**Files:**

- Create: `src/eThangAgent.CLI/CwdWorkspaceContext.cs`
- Modify: `src/eThangAgent.CLI/eThangAgent.CLI.csproj` (+ Storage.ACL, State.Domain references)
- Modify: `src/eThangAgent.CLI/Program.cs` (state wiring)
- Modify: `src/eThangAgent.Tool.Domain/ExecGuide.cs` (v1.2 durable-state pointer)
- Modify: `tests/eThangAgent.Tool.Domain.Tests/ExecGuideTests.cs`

**Interfaces:**

- Consumes: everything above.
- Produces: composition root where the capability registry exposes `agent` + `state` providers; workspace identity = canonical cwd; database at the default app location (env-overridable).

- [ ] **Step 1: Update the failing guide tests**

In `tests/eThangAgent.Tool.Domain.Tests/ExecGuideTests.cs`: change `Assert.Equal("1.1", ExecGuide.Version);` to `Assert.Equal("1.2", ExecGuide.Version);` and add:

```csharp
    [Fact]
    public void Guide_DocumentsDurableState()
    {
        Assert.Contains("state.set @{", ExecGuide.Text);
        Assert.Contains("state.verify @{}", ExecGuide.Text);
    }
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests/eThangAgent.Tool.Domain.Tests.csproj`
Expected: version assert fails (still "1.1").

- [ ] **Step 3: Wire the composition root + guide**

`src/eThangAgent.CLI/CwdWorkspaceContext.cs`:

```csharp
using eThangAgent.StateDomain;

namespace eThangAgent.CLI;

public sealed class CwdWorkspaceContext : IWorkspaceContext
{
    public string WorkspaceId { get; } = Path.GetFullPath(".");
}
```

`src/eThangAgent.CLI/eThangAgent.CLI.csproj` — add:

```xml
    <ProjectReference Include="../eThangAgent.Storage.ACL/eThangAgent.Storage.ACL.csproj" />
    <ProjectReference Include="../eThangAgent.State.Domain/eThangAgent.State.Domain.csproj" />
```

`src/eThangAgent.CLI/Program.cs` — replace the registry registration block:

```csharp
            .AddSingleton<ICapabilityRegistry>(sp =>
                CapabilityRegistry.Create([sp.GetRequiredService<AgentToolsProvider>()]))
```

with:

```csharp
            .AddSingleton<IWorkspaceContext, CwdWorkspaceContext>()
            .AddSingleton<AppDatabase>()
            .AddSingleton<IStateStore, SqliteStateStore>()
            .AddSingleton<EvidenceOptions>(_ => EvidenceOptions.Default)
            .AddSingleton<IEvidenceRunner, PsEvidenceRunner>()
            .AddSingleton<IStateService, StateService>()
            .AddSingleton<StateCapabilityProvider>()
            .AddSingleton<ICapabilityRegistry>(sp =>
                CapabilityRegistry.Create(
                [
                    sp.GetRequiredService<AgentToolsProvider>(),
                    sp.GetRequiredService<StateCapabilityProvider>(),
                ]))
```

and add `using eThangAgent.Storage.ACL;` to the usings (`StateDomain` arrives via explicit using already present from P2 — verify both `using eThangAgent.CapabilityDomain;` and `using eThangAgent.StateDomain;` exist).

`src/eThangAgent.Tool.Domain/ExecGuide.cs` — set `Version = "1.2"` and extend the Providers block:

```text
    Providers:

        Get-AgentProvider

    Durable state (claims, evidence, certification):

        state.set @{ key = 'current/head'; value = 'done' }
        state.transition @{ from = 'coding'; to = 'done'; summary = 'work';
            evidence = @('dotnet build') }
        state.verify @{}
```

- [ ] **Step 4: Verify**

Run:

```
cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN
dotnet test tests/eThangAgent.Tool.Domain.Tests/eThangAgent.Tool.Domain.Tests.csproj --no-build --nologo 2>&1 | tail -2
dotnet test tests/eThangAgent.State.Domain.Tests/eThangAgent.State.Domain.Tests.csproj --no-build --nologo 2>&1 | tail -2
dotnet test tests/eThangAgent.Storage.ACL.Tests/eThangAgent.Storage.ACL.Tests.csproj --no-build --nologo 2>&1 | tail -2
dotnet test tests/eThangAgent.PowerShell.ACL.Tests/eThangAgent.PowerShell.ACL.Tests.csproj --no-build --nologo 2>&1 | tail -2
```

Expected: BUILD-CLEAN; all suites pass.

- [ ] **Step 5: Commit**

Two commits, in order:

```bash
git add src/eThangAgent.PowerShell.ACL/eThangAgent.PowerShell.ACL.csproj
```

(folded into the Task 5 commit if not yet made; otherwise:)

```bash
git add src/eThangAgent.CLI src/eThangAgent.Tool.Domain tests/eThangAgent.Tool.Domain.Tests
```

```bash
git commit -m "feat(cli): wire durable state provider and document it in guide v1.2"
```

---

### Task 7: E2E — the discipline loop

**Files:**

- Modify: `tests/eThangAgent.CLI.Tests/E2ETests.cs`

**Interfaces:**

- Consumes: `ExecToolCall` helper, `StartCli`, `ReadUntil`, `MockOpenRouterServer`.
- Produces: proof that the generated reference carries `state.*`, and that the full discipline loop (set → transition → verify) certifies on passing evidence and violates with blocking reasons on failing evidence — against an **isolated temp database** (never the real app database).

- [ ] **Step 1: Give StartCli an optional database override**

Change the signature and body:

```csharp
    private static Process StartCli(MockOpenRouterServer mock, string? databasePath = null)
    {
        var projectDir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "eThangAgent.CLI"));

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run --no-build",
            WorkingDirectory = projectDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.EnvironmentVariables["OPENROUTER_API_KEY"] = "test-key";
        startInfo.EnvironmentVariables["OPENROUTER_BASE_URL"] = mock.BaseUrl;
        if (databasePath is not null)
            startInfo.EnvironmentVariables["ETHANG_AGENT_DB"] = databasePath;

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }
```

- [ ] **Step 2: Extend the guide E2E with state-reference assertions**

In `Repl_SendsExecGuide_InSystemPrompt`, add before `/quit`:

```csharp
        Assert.Contains("state.get(key: String): Read a durable state value.", mock.LastChatRequestBody);
        Assert.Contains(
            "state.verify(ids: String[]): Run attached evidence fail-closed and certify.",
            mock.LastChatRequestBody);
```

- [ ] **Step 3: Add the discipline-loop facts**

```csharp
    [Fact]
    public async Task Repl_StateDisciplineLoop_Certifies()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();
        var db = Path.Combine(Path.GetTempPath(), $"ethang-state-{Guid.NewGuid():N}.db");

        var program = """
            $null = state.set @{ key = 'current/head'; value = 'done' }
            $null = state.transition @{ from = 'coding'; to = 'done'; summary = 'work'; evidence = @('Write-Output evidence-ok') }
            state.verify @{}
            """;
        var execArgs = System.Text.Json.JsonSerializer.Serialize(new { program });
        mock.Returns(ExecToolCall("call_1", execArgs));
        mock.Returns("""{"choices":[{"message":{"content":"certified"}}]}""");

        using var process = StartCli(mock, db);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("track the work");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.Contains("certified", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Certified\":true", mock.RequestBodies[1]);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.Equal(0, process.ExitCode);

        try { File.Delete(db); } catch { }
    }

    [Fact]
    public async Task Repl_StateDisciplineLoop_Violated_OnFailingEvidence()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();
        var db = Path.Combine(Path.GetTempPath(), $"ethang-state-{Guid.NewGuid():N}.db");

        var program = """
            $null = state.set @{ key = 'current/head'; value = 'done' }
            $null = state.transition @{ from = 'coding'; to = 'done'; summary = 'work'; evidence = @('Write-Error boom') }
            state.verify @{}
            """;
        var execArgs = System.Text.Json.JsonSerializer.Serialize(new { program });
        mock.Returns(ExecToolCall("call_1", execArgs));
        mock.Returns("""{"choices":[{"message":{"content":"violated"}}]}""");

        using var process = StartCli(mock, db);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("track the work");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.Contains("violated", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Certified\":false", mock.RequestBodies[1]);
        Assert.Contains("\"Violated\":true", mock.RequestBodies[1]);
        Assert.Contains("boom", mock.RequestBodies[1]);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.Equal(0, process.ExitCode);

        try { File.Delete(db); } catch { }
    }
```

Note: the raw string literals here are valid multi-line form (opening `"""` followed by a newline, closing `"""` on its own line) — do not collapse them onto one line.

- [ ] **Step 4: Verify + commit**

Run:

```
cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN
dotnet test tests/eThangAgent.CLI.Tests/eThangAgent.CLI.Tests.csproj --no-build --nologo 2>&1 | tail -2
```

Expected: BUILD-CLEAN; all CLI tests pass including the two new discipline loops.

```bash
git add tests/eThangAgent.CLI.Tests/E2ETests.cs
```

```bash
git commit -m "test(cli): cover state discipline loop certification and violation e2e"
```

---

### Task 8: Full verification

**Files:** none created — solution-level verification.

- [ ] **Step 1: Full build with warning scan**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|warning|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN (fix, never suppress).

- [ ] **Step 2: Full test suite**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet test --nologo 2>&1 | rg 'Passed!|Failed!' | head -20`
Expected: every project green — P2's totals plus State.Domain (~19), Storage.ACL (9), PsEvidenceRunner (5).

- [ ] **Step 3: Coverage**

Run:

```
dotnet test tests/eThangAgent.State.Domain.Tests/eThangAgent.State.Domain.Tests.csproj --collect:'XPlat Code Coverage' --nologo 2>&1 | tail -2
fd --no-ignore 'coverage.cobertura.xml' tests/eThangAgent.State.Domain.Tests | head -3
rg -o 'name="eThangAgent.StateDomain.[A-Za-z]+"[^>]*line-rate="[0-9.]+"' <newest-file> | head -15
```

(P1 lesson: `TestResults` is gitignored — `fd --no-ignore`.)
Expected: State.Domain ~100% (StateService ≥ 95% acceptable); re-check Storage.ACL ≥ 80% the same way. If below, add targeted tests — never weaken assertions.

- [ ] **Step 4: Spec cross-check**

Run: `rg -n 'VersionConflict|state.violated|state.certified|LASTEXITCODE' src/eThangAgent.State.Domain/StateService.cs src/eThangAgent.PowerShell.ACL/PsEvidenceRunner.cs | head -10`
Expected: CAS conflict naming, both durable event kinds, and the LASTEXITCODE confirmation rule all present verbatim.

- [ ] **Step 5: Final commit (if anything is pending)**

```bash
git status --short
```

If clean, done; otherwise `chore:` commit describing the remainder.

---

## Spec Coverage Map

| Spec item | Where implemented |
| --- | --- |
| D1 scope B: CAS KV + certification engine | Tasks 2–3 (provider + StateService) |
| D2 in-app SQLite, app-owned, manual edits blocked | Task 4 (Storage.ACL, `%LOCALAPPDATA%` default) |
| D3 workspace-keyed multi-project | Task 1 (`IWorkspaceContext`), Task 6 (CwdWorkspaceContext), Task 4 isolation test |
| Fail-closed certification + head revocation ordering | Task 3 (StateService + ordering test) |
| Evidence semantics (no errors + LASTEXITCODE 0/unset) | Task 5 (PsEvidenceRunner + matrix) |
| Capability surface (8 actions, generated docs) | Task 2 (descriptors), Task 6 (wiring), Task 7 (E2E reference asserts) |
| Enforcement planned as future work | Spec Future Work section; single mutation choke point (`StateService`) preserved |

## Out of Scope (next cycles)

Enforcement modes (future work, user-required), MCP provider, nested exec (P4), desktop UI/kanban/supervisor tables (Storage.ACL is their beachhead), discovery-first token knob (P5+).

## Status update — 2026-08-21 (wave 3 complete)

- Tasks 7–8 complete: discipline-loop E2Es green; full solution 14/14 suites pass.
- Fixes shipped: engine mints both bare and provider-qualified wrapper names (mirrors Resolve);
  CLI registers IStateService mapping. E2E asserts decode tool-message JSON before matching.
- Integration facts compose registry + real store + real runner in-process (broker and outer engine).
