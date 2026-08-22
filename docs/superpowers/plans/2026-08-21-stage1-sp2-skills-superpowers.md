# Stage 1 / SP2 — Skill Subsystem + Superpowers Port Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the Skill Domain (`eThangAgent.Skill.Domain`): 14 verbatim embedded superpowers skills + tool mapping, `skill_list`/`skill_view`/`skill_manage` tools over built-in + SQLite-backed learned stores, session-start bootstrap injection through `CompositeSystemPromptProvider`, and the `clarify`/`todo` tools the mapping binds to — per spec `docs/superpowers/specs/2026-08-21-stage-1-methodology-port-design.md` (SP2).

**Architecture:** New bounded context owns skill content as data: built-ins ship as embedded resources read verbatim from upstream (superpowers rule: never reword skill bodies); harness adaptation happens ONLY in (a) the bootstrap prompt provider and (b) the inline tool-mapping table it injects. Learned skills persist in AppDatabase via a V3 migration (current row + version history + usage rows). The three skill tools are ordinary `ITool` bindings like read/write. `clarify` renders through Terminal ACL types but the domain depends only on a new `IClarifyChannel` seam; `todo` persists one JSON document under reserved namespace `todo` in the existing State Domain store.

**Tech Stack:** C# / .NET 10, xUnit, embedded resources, SQLite via existing AppDatabase migrations, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-08-21-stage-1-methodology-port-design.md`

## Global Constraints

- Skill bodies are copied byte-for-byte from `C:\Users\glove\projects\temp-resources\superpowers\skills\<name>\SKILL.md`. NO wording edits to any SKILL.md — ever, including during later tasks.
- All new domain types live in namespace `eThangAgent.SkillDomain` (project `src/eThangAgent.Skill.Domain`). Tools stay in `eThangAgent.ToolDomain` (project Tool.Domain) referencing Skill.Domain — same direction Agent.Application references domains; never the reverse.
- Strict boundaries: required parameters required, unknown parameters rejected, errors returned as `Error [Code]: message` tool results. The Input Parsing House Pattern (Appendix) is applied verbatim everywhere.
- Built-in names are authoritative: a learned skill may never shadow or mutate one.
- Every task ends green: `dotnet build` + targeted tests; full suite at wiring tasks.
- DI wiring only in `src/eThangAgent.CLI/Program.cs`.
- Upstream source of truth: `C:/Users/glove/projects/temp-resources/superpowers/` (skills under `skills/`).
- README updated in Task 12.

## File Structure

```text
src/eThangAgent.Skill.Domain/            # NEW PROJECT
  eThangAgent.Skill.Domain.csproj        # refs SharedKernel; embeds Skills/**
  SkillDefinition.cs                     # immutable record
  SkillSource.cs                         # BuiltIn | Learned
  Specifications/SkillSpecifications.cs  # name charset, non-empty body/description
  SkillMarkdown.cs                       # frontmatter parser (verbatim body out)
  ISkillCatalog.cs                       # built-in seam
  EmbeddedSkillCatalog.cs                # assembly-resource catalog
  ILearnedSkillStore.cs                  # persistence seam (learned + usage)
skills/                                  # EMBEDDED CONTENT (verbatim copies)
  <14 skill dirs>/SKILL.md
  EthangToolsMapping.md                  # our tool mapping (ours to write)
src/eThangAgent.Storage.ACL/
  AppDatabase.cs                         # MODIFY — ApplyV3
  SqliteLearnedSkillStore.cs             # NEW
src/eThangAgent.Tool.Domain/
  SkillListTool.cs / SkillViewTool.cs / SkillManageTool.cs   # NEW (+ inputs)
  ClarifyInput.cs / ClarifyTool.cs / IClarifyChannel.cs      # NEW
  TodoInput.cs / TodoTool.cs / TodoDocument.cs               # NEW
src/eThangAgent.CLI/
  InteractiveClarifyChannel.cs           # NEW (Terminal ACL adapter)
  PipedClarifyChannel.cs                 # NEW (stdin line protocol)
  SuperpowersBootstrapPromptProvider.cs  # NEW
  Program.cs                             # MODIFY — all registrations
README.md                                # MODIFY — Task 12

tests/eThangAgent.Skill.Domain.Tests/    # NEW PROJECT
  SkillSpecificationsTests.cs / SkillMarkdownTests.cs / EmbeddedSkillCatalogTests.cs
tests/eThangAgent.Storage.ACL.Tests/
  SqliteLearnedSkillStoreTests.cs        # NEW
tests/eThangAgent.Tool.Domain.Tests/
  SkillListToolTests.cs / SkillViewToolTests.cs / SkillManageToolTests.cs
  ClarifyToolTests.cs / TodoToolTests.cs
tests/eThangAgent.CLI.Tests/
  E2ETests.cs                            # MODIFY — bootstrap assertion test
```

## Appendix: Input Parsing House Pattern

Every JSON-input record below applies this exact skeleton (from `ReadToolInput.Create`, proven across SP1). Only the allowed-set, required-field checks, and value rules differ; each task lists its deltas as concrete code. The skeleton:

1. `JsonDocument.Parse` inside try/catch → `InvalidJsonArguments` on JsonException; root must be object.
2. Unknown-parameter rejection against an ordinal `HashSet` allowed-set, listing allowed names in the error.
3. Per field: `TryGetProperty` → `Missing(name)`; `ValueKind` check → `WrongType(name, expected, actual)`; value rules → `InvalidParameterValue`.
4. Helpers `Missing`/`WrongType`/`Fail` exactly as in `WriteToolInput.cs` (read it once; copy shape).

String arrays parse as: element kind must be String each; empty array allowed only where stated.

---

### Task 1: Skill Domain scaffold + core records

**Files:**

- Create project: `src/eThangAgent.Skill.Domain/` (+ add to `eThangAgent.slnx`, reference SharedKernel)
- Create test project: `tests/eThangAgent.Skill.Domain.Tests/` (+ slnx, ref Skill.Domain; copy csproj shape + GlobalUsings (`global using Xunit;`) from Tool.Domain tests)
- Create: `SkillSource.cs`, `SkillDefinition.cs`, `Specifications/SkillSpecifications.cs`
- Test: `SkillSpecificationsTests.cs`

**Interfaces:**

- Produces: `enum SkillSource { BuiltIn, Learned }`; `sealed record SkillDefinition(string Name, string Description, string Body, int Version, SkillSource Source, string? ProvenanceSessionId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)`; static class `SkillSpecifications` with reusable specifications used by tools/store: `ValidName` (^[a-z0-9][a-z0-9-]{0,63}$), `HasDescription`, `HasBody`.

- [ ] **Step 1: Scaffold projects**

Run:

```powershell
dotnet new classlib -o src/eThangAgent.Skill.Domain -f net10.0
dotnet new xunit -o tests/eThangAgent.Skill.Domain.Tests -f net10.0
dotnet sln eThangAgent.slnx add src/eThangAgent.Skill.Domain tests/eThangAgent.Skill.Domain.Tests
dotnet add tests/eThangAgent.Skill.Domain.Tests reference src/eThangAgent.Skill.Domain
dotnet add src/eThangAgent.Skill.Domain reference src/eThangAgent.SharedKernel
```

Delete template files (`Class1.cs`, `UnitTest1.cs`). Match test csproj to Tool.Domain's (no explicit TFM needed if Directory.Build.props governs; keep consistent with sibling test projects).

- [ ] **Step 2: Write failing spec tests**

```csharp
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

public class SkillSpecificationsTests
{
    [Theory]
    [InlineData("brainstorming")]
    [InlineData("a")]
    [InlineData("abc-123")]
    public void ValidNames_Pass(string name) =>
        Assert.True(SkillSpecifications.ValidName.IsMatch(name));

    [Theory]
    [InlineData("")]
    [InlineData("Brainstorming")]
    [InlineData("-lead")]
    [InlineData("has space")]
    [InlineData("way-too-long-0123456789012345678901234567890123456789012345678901234567890123")]
    public void InvalidNames_Fail(string name) =>
        Assert.False(SkillSpecifications.ValidName.IsMatch(name));
}
```

- [ ] **Step 3: Run red** — compile error, types missing.

- [ ] **Step 4: Implement**

```csharp
namespace eThangAgent.SkillDomain;

public enum SkillSource { BuiltIn, Learned }
```

```csharp
namespace eThangAgent.SkillDomain;

/// <summary>A methodology skill: built-ins ship verbatim with the app;
/// learned skills are created by the agent itself (provenance-tracked).</summary>
public sealed record SkillDefinition(
    string Name,
    string Description,
    string Body,
    int Version,
    SkillSource Source,
    string? ProvenanceSessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

```csharp
using System.Text.RegularExpressions;

namespace eThangAgent.SkillDomain;

public static partial class SkillSpecifications
{
    // Lowercase alphanumeric + hyphens; never starts with a hyphen; ≤ 64 chars.
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    public static partial Regex ValidName { get; }
}
```

- [ ] **Step 5: Green + commit**

```powershell
dotnet test tests/eThangAgent.Skill.Domain.Tests
git add -A src/eThangAgent.Skill.Domain tests/eThangAgent.Skill.Domain.Tests eThangAgent.slnx
git commit -m "feat(skill-domain): scaffold bounded context with definition records"
```

### Task 2: SkillMarkdown frontmatter parser

**Files:**

- Create: `src/eThangAgent.Skill.Domain/SkillMarkdown.cs`
- Test: `tests/eThangAgent.Skill.Domain.Tests/SkillMarkdownTests.cs`

**Interfaces:**

- Produces: `static class SkillMarkdown { Result<ParsedSkill> Parse(string text) }` where `sealed record ParsedSkill(string Name, string Description, string Body)`.

Parsing rules (strict about structure, forward-compatible about unknown keys):

- Text must start with a `---` fence line (tolerates leading UTF-8 BOM and leading whitespace-free newline).
- Within the frontmatter block, `name:` and `description:` keys are REQUIRED (first occurrence wins); values are the trimmed remainder of the line.
- Unrecognized top-level keys are IGNORED (documented decision: forward-compatible reading of upstream metadata; this is content parsing, not user-input coercion).
- Body = everything after the closing `---` line, with exactly one leading newline removed if present; otherwise byte-for-byte verbatim.

Error codes: `MissingFrontmatter`, `MissingKey` (names the key), `EmptyDescription`.

- [ ] **Step 1: Write failing tests**

```csharp
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

public class SkillMarkdownTests
{
    private const string Doc = "---\nname: test-skill\ndescription: Does things.\n---\n\n# Body here\n\nLine two";

    [Fact]
    public void WellFormedDoc_ParsesNameDescriptionAndVerbatimBody()
    {
        var r = SkillMarkdown.Parse(Doc);
        Assert.True(r.IsSuccess);
        Assert.Equal("test-skill", r.Value!.Name);
        Assert.Equal("Does things.", r.Value.Description);
        Assert.Equal("# Body here\n\nLine two", r.Value.Body);
    }

    [Fact]
    public void CrlfDocs_Parse()
    {
        var r = SkillMarkdown.Parse(Doc.Replace("\n", "\r\n"));
        Assert.True(r.IsSuccess);
        Assert.Equal("test-skill", r.Value!.Name);
    }

    [Fact]
    public void MissingOpeningFence_Fails()
    {
        var r = SkillMarkdown.Parse("name: x\n---\nbody");
        Assert.False(r.IsSuccess);
        Assert.Equal("MissingFrontmatter", r.Error!.Code);
    }

    [Theory]
    [InlineData("---\ndescription: d\n---\nb")]
    [InlineData("---\nname: n\n---\nb")]
    public void MissingRequiredKey_Fails(string doc)
    {
        var r = SkillMarkdown.Parse(doc);
        Assert.False(r.IsSuccess);
        Assert.Equal("MissingKey", r.Error!.Code);
    }

    [Fact]
    public void EmptyDescription_Fails()
    {
        var r = SkillMarkdown.Parse("---\nname: n\ndescription:\n---\nb");
        Assert.False(r.IsSuccess);
        Assert.Equal("EmptyDescription", r.Error!.Code);
    }

    [Fact]
    public void UnknownKeys_Tolerated()
    {
        var doc = "---\nname: n\ndescription: d\nversion: 9\nsomething-else: x\n---\nB";
        var r = SkillMarkdown.Parse(doc);
        Assert.True(r.IsSuccess);
        Assert.Equal("B", r.Value!.Body);
    }

    [Fact]
    public void NoClosingFence_Fails()
    {
        var r = SkillMarkdown.Parse("---\nname: n\ndescription: d\nno fence");
        Assert.False(r.IsSuccess);
        Assert.Equal("MissingFrontmatter", r.Error!.Code);
    }
}
```

- [ ] **Step 2: Run red** — compile error.

- [ ] **Step 3: Implement**

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

public static class SkillMarkdown
{
    public sealed record ParsedSkill(string Name, string Description, string Body);

    public static Result<ParsedSkill> Parse(string text)
    {
        if (text.StartsWith("\uFEFF")) text = text[1..];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2 || lines[0].TrimEnd() != "---")
            return Fail(new Error("MissingFrontmatter",
                "Skill file must open with a '---' frontmatter fence."));

        string? name = null, description = null;
        int close = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---") { close = i; break; }
            var idx = lines[i].IndexOf(':');
            if (idx <= 0) continue;
            var key = lines[i][..idx].Trim();
            var value = lines[i][(idx + 1)..].Trim();
            switch (key)
            {
                case "name" when name is null: name = value; break;
                case "description" when description is null: description = value; break;
            }
        }
        if (close < 0)
            return Fail(new Error("MissingFrontmatter",
                "Frontmatter is never closed; expected a second '---' line."));
        if (name is null) return Fail(new Error("MissingKey", "Frontmatter requires a 'name:' key."));
        if (description is null) return Fail(new Error("MissingKey", "Frontmatter requires a 'description:' key."));
        if (description.Length == 0) return Fail(new Error("EmptyDescription", "'description' must be non-empty."));

        var bodyLines = lines[(close + 1)..];
        if (bodyLines.Length > 0 && bodyLines[0].Length == 0) bodyLines = bodyLines[1..];
        return Result<ParsedSkill>.Success(new ParsedSkill(name, description, string.Join('\n', bodyLines)));
    }

    private static Result<ParsedSkill> Fail(Error error) => Result<ParsedSkill>.Failure(error);
}
```

Note: CRLF input is normalized to `\n` for parsing AND for the returned body — embedded files are LF on disk in git; this keeps bodies deterministic across checkouts. Documented as part of the format contract.

- [ ] **Step 4: Green + commit**

```powershell
dotnet test tests/eThangAgent.Skill.Domain.Tests --filter SkillMarkdownTests
git add src/eThangAgent.Skill.Domain/SkillMarkdown.cs tests/eThangAgent.Skill.Domain.Tests/SkillMarkdownTests.cs
git commit -m "feat(skill-domain): strict frontmatter parser preserving verbatim bodies"
```

---

### Task 3: Embed the 14 skills verbatim + tool mapping + catalog

**Files:**

- Create: `skills/` tree inside `src/eThangAgent.Skill.Domain/` (14 upstream copies + our mapping doc)
- Modify: `src/eThangAgent.Skill.Domain/eThangAgent.Skill.Domain.csproj` (EmbeddedResource glob)
- Create: `ISkillCatalog.cs`, `EmbeddedSkillCatalog.cs`
- Test: `EmbeddedSkillCatalogTests.cs`

**Interfaces:**

- Consumes: `SkillMarkdown` (Task 2).
- Produces: `interface ISkillCatalog { Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default); Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default); }`; `EmbeddedSkillCatalog : ISkillCatalog` reading `Skills.<dir>.SKILL.md` resources. Built-in versions are always `1`; timestamps are assembly-build-stable constants (`DateTimeOffset.UnixEpoch`) so output is deterministic.

- [ ] **Step 1: Copy upstream skills verbatim**

Run (PowerShell, from repo root):

```powershell
$upstream = 'C:/Users/glove/projects/temp-resources/superpowers/skills'
$dest = 'src/eThangAgent.Skill.Domain/skills'
New-Item -ItemType Directory -Force $dest | Out-Null
Get-ChildItem $upstream -Directory | ForEach-Object {
    $target = Join-Path $dest $_.Name
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $_.FullName 'SKILL.md') $target
}
(Get-ChildItem $dest -Recurse -Filter SKILL.md).Count   # expect 14
```

Then create `src/eThangAgent.Skill.Domain/skills/EthangToolsMapping.md` with EXACTLY this content (this file is OURS — harness adaptation lives here and in the bootstrap constant of Task 8; keep them in sync):

```markdown
---
name: ethang-tools-mapping
description: How superpowers action names bind to real eThang Agent tools.
---

# eThang Agent Tool Mapping

Skills name actions; this harness binds them to real tools:

| Action (as named by skills) | Binding |
| --- | --- |
| Read a file | `read` |
| Write / edit files | `write` / `edit` |
| Search files | `search_files` |
| Run shell commands / tests / git plumbing | `exec` (PowerShell) |
| Dispatch a subagent | spawn sub-agent capability |
| Create/update todos | `todo` tool |
| Invoke a skill / load its content | `skill_view` tool (never read raw skill paths; the skill store IS the mechanism) |
| List available skills | `skill_list` tool |
| Ask the human partner a clarifying question | `clarify` tool (MANDATORY for brainstorming) |
| Track plan progress | `todo` tool plus plan-file checkboxes |

All scripts are PowerShell (.ps1). Windows-native. Tests: xUnit via dotnet test.
```

Update the csproj to embed everything:

```xml
<ItemGroup>
  <EmbeddedResource Include="skills/**/*" />
</ItemGroup>
```

- [ ] **Step 2: Write failing catalog tests**

```csharp
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

public class EmbeddedSkillCatalogTests
{
    private readonly EmbeddedSkillCatalog _catalog = new();

    private static readonly string[] ExpectedNames =
    [
        "brainstorming", "dispatching-parallel-agents", "executing-plans",
        "finishing-a-development-branch", "receiving-code-review",
        "requesting-code-review", "subagent-driven-development",
        "systematic-debugging", "test-driven-development", "using-git-worktrees",
        "using-superpowers", "verification-before-completion", "writing-plans",
        "writing-skills",
    ];

    [Fact]
    public async Task Lists_AllFourteenSkills_WithMetadata()
    {
        var r = await _catalog.ListAsync();
        Assert.True(r.IsSuccess);
        var names = r.Value!.Select(s => s.Name).OrderBy(n => n).ToArray();
        Assert.Equal(ExpectedNames, names);
        Assert.All(r.Value!, s =>
        {
            Assert.Equal(SkillSource.BuiltIn, s.Source);
            Assert.Equal(1, s.Version);
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
        });
    }

    [Fact]
    public async Task Get_ReturnsVerbatimBody_MarkersIntact()
    {
        var r = await _catalog.GetAsync("brainstorming");
        Assert.True(r.IsSuccess);
        Assert.Contains("HARD-GATE", r.Value!.Body);          // verbatim upstream marker
        Assert.StartsWith("---", r.Value.Body);               // frontmatter preserved
    }

    [Fact]
    public async Task Get_UnknownName_Fails()
    {
        var r = await _catalog.GetAsync("not-a-skill");
        Assert.False(r.IsSuccess);
        Assert.Equal("SkillNotFound", r.Error!.Code);
    }

    [Fact]
    public async Task MappingReference_IsListed_AndViewable()
    {
        var list = await _catalog.ListAsync();
        Assert.Contains(list.Value!, s => s.Name == "ethang-tools-mapping");
        var get = await _catalog.GetAsync("ethang-tools-mapping");
        Assert.True(get.IsSuccess);
        Assert.Contains("skill_view", get.Value!.Body);
    }
}
```

- [ ] **Step 3: Run red**, then implement:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>Built-in skill seam. Built-ins are authoritative: learned skills
/// may never shadow these names.</summary>
public interface ISkillCatalog
{
    Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default);
    Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default);
}
```

```csharp
using System.Reflection;
using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>Serves SKILL.md resources embedded in the Skill Domain assembly,
/// byte-verbatim from upstream. Parsing happens once, lazily, cached.</summary>
public sealed class EmbeddedSkillCatalog : ISkillCatalog
{
    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, SkillDefinition>? _cache;

    public async Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return Result<IReadOnlyList<SkillDefinition>>.Success(
            all.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList());
    }

    public async Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return all.TryGetValue(name, out var skill)
            ? Result<SkillDefinition>.Success(skill)
            : Result<SkillDefinition>.Failure(new Error("SkillNotFound",
                $"No built-in skill named '{name}'. Use skill_list to see available skills."));
    }

    private static Task<IReadOnlyDictionary<string, SkillDefinition>> LoadAllAsync(CancellationToken ct)
    {
        lock (Gate)
        {
            if (_cache is not null) return Task.FromResult(_cache)!;
        }

        var assembly = typeof(EmbeddedSkillCatalog).Assembly;
        var prefix = assembly.GetName().Name + ".skills.";
        var byName = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith("SKILL.md", StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var parsed = SkillMarkdown.Parse(reader.ReadToEndAsync(ct).GetAwaiter().GetResult());
            if (!parsed.IsSuccess)
                throw new InvalidOperationException(
                    $"Embedded skill resource '{resourceName}' failed frontmatter parsing: " +
                    parsed.Error!.Message);
            var definition = new SkillDefinition(
                parsed.Value!.Name, parsed.Value.Description, parsed.Value.Body,
                Version: 1, SkillSource.BuiltIn, ProvenanceSessionId: null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            byName[definition.Name] = definition;
        }

        lock (Gate) { _cache = byName; }
        return Task.FromResult(_cache)!;
    }
}
```

Implementation notes:

- The resource scan filters names ending `.md` under the `skills.` prefix (so `EthangToolsMapping.md` is included; its frontmatter supplies its listable name).
- A malformed embedded skill throws `InvalidOperationException` — that is a build/packaging defect (programmer error), not a runtime domain failure.
- Cache once per process; skills are immutable built-ins.

- [ ] **Step 4: Run green**

Run: `dotnet test tests/eThangAgent.Skill.Domain.Tests --filter EmbeddedSkillCatalogTests`
Expected: PASS (all 4). If the count assertion fails, re-run the copy step — exactly 14 SKILL.md files plus `EthangToolsMapping.md` must exist under `src/eThangAgent.Skill.Domain/skills/`.

Verify no upstream body drifted: `git status --short src/eThangAgent.Skill.Domain/skills` shows only additions (new files), and spot-check one file's first lines against upstream (`brainstorming/SKILL.md` must begin `---\nname: brainstorming`).

- [ ] **Step 5: Commit**

```powershell
git add src/eThangAgent.Skill.Domain
git commit -m "feat(skill-domain): embed 14 verbatim superpowers skills + tool mapping"
```

### Task 4: Learned-skill persistence — V3 migration + store

**Files:**

- Modify: `src/eThangAgent.Storage.ACL/AppDatabase.cs` (add `ApplyV3`)
- Create: `src/eThangAgent.Storage.ACL/SqliteLearnedSkillStore.cs`
- Test: `tests/eThangAgent.Storage.ACL.Tests/SqliteLearnedSkillStoreTests.cs`

**Interfaces:**

- Consumes: `AppDatabase` migration pattern (read `ApplyV1` first — copy its transaction style exactly).
- Produces: `SqliteLearnedSkillStore(AppDatabase) : ILearnedSkillStore` where `ILearnedSkillStore` (in Skill.Domain):

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>Persistence for agent-created skills and their version history.
/// Global scope by design: methodology knowledge transcends workspaces.
/// Single-writer CLI, so updates are last-write-wins; history preserves audit.</summary>
public interface ILearnedSkillStore
{
    Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default);
    Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default);
    Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default);
    /// <summary>Writes the new current row AND a history row at the definition's version.</summary>
    Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default);
    /// <summary>Removes current + history rows. Usage rows survive (analytics only).</summary>
    Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default);
    Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default);
}
```

Migration V3 SQL (append `ApplyV3(connection)` + gate to `Migrate()` following the V1/V1→V2 pattern verbatim):

```sql
CREATE TABLE IF NOT EXISTS learned_skills (
    name TEXT PRIMARY KEY,
    description TEXT NOT NULL,
    body TEXT NOT NULL,
    version INTEGER NOT NULL,
    provenance_session TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS skill_versions (
    name TEXT NOT NULL,
    version INTEGER NOT NULL,
    description TEXT NOT NULL,
    body TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (name, version)
);
CREATE TABLE IF NOT EXISTS skill_usage (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    skill_name TEXT NOT NULL,
    viewed_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_skill_usage_name ON skill_usage (skill_name);
```

Error codes: `SkillExists` (create on existing name), `SkillNotFound` (update/delete/get-miss), `FileSystemError`-equivalent `StorageError` for unexpected failures.

- [ ] **Step 1: Write failing integration tests** (pattern: real temp DB per class — `new AppDatabase(Path.Combine(Path.GetTempPath(), $"ethang-skills-{Guid.NewGuid():N}.db"))`, dispose deletes file; mirror an existing Storage.ACL.Tests fixture). Required cases:

1. Create then Get returns equal definition (all fields).
2. Create duplicate name → `SkillExists`.
3. Get unknown → null success (`Result<SkillDefinition?>` success with null value).
4. Update writes new current AND a history row at old version: after v1 create + v2 update, `GetAsync` returns v2; open a raw connection via `AppDatabase.Open()` and assert `SELECT COUNT(*) FROM skill_versions WHERE name='x'` = 2 (v1 and v2 rows).
5. Update unknown name → `SkillNotFound`.
6. Delete removes current + history (count 0 afterwards); Delete unknown → `SkillNotFound`; second delete → `SkillNotFound`.
7. ListAsync returns learned skills sorted by name; empty store → empty list (not error).
8. AppendUsage increments count; usage rows persist after skill deletion.
9. Migration idempotence: constructing AppDatabase twice on same file does not throw.

Write full assertions for each (no placeholders); use one shared helper `MakeSkill(string name)` producing a valid definition (`Source = Learned`, `Version = 1`, distinct bodies per test where content matters).

- [ ] **Step 2: Run red**, implement `SqliteLearnedSkillStore` following `SqliteStateStore`'s command/parameter style exactly (parameterized SQL always; ISO-8601 timestamps as TEXT). Then run green.

- [ ] **Step 3: Commit**

```powershell
git add src/eThangAgent.Storage.ACL tests/eThangAgent.Storage.ACL.Tests/SqliteLearnedSkillStoreTests.cs
git commit -m "feat(storage): learned-skill store with version history and usage tracking"
```

---

### Task 5: `skill_list` and `skill_view` tools

**Files:**

- Create: `src/eThangAgent.Tool.Domain/SkillListTool.cs`, `SkillViewTool.cs`, `SkillViewInput.cs`
- Modify: `eThangAgent.Tool.Domain.csproj` — add project reference to Skill.Domain
- Test: `tests/eThangAgent.Tool.Domain.Tests/SkillListToolTests.cs`, `SkillViewToolTests.cs`

**Interfaces:**

- Consumes: `ISkillCatalog`, `ILearnedSkillStore`.
- Produces:
  - `skill_list`: no parameters. Merged listing (built-ins + learned; names unique by construction). Output contract — one header line plus one line per skill, sorted by name:

    ```text
    [skills: N available]
    brainstorming        builtin v1  Turn ideas into designs through dialogue...
    my-skill             learned v3  Remember deployment quirks...
    ```

    Description truncated to 60 chars with `…` when longer (visible truncation, never silent mid-word guarantee needed).
  - `skill_view`: `name` (string, required). Resolution order: built-in first, then learned (built-ins authoritative; collision impossible anyway). On hit: appends usage row (best-effort — a storage failure degrades to appending `[warning] usage not recorded` rather than failing the view; the skill content is what matters). Output contract:

    ```text
    [skill <name> | source | v<version>]
    <body byte-for-byte>
    ```

- [ ] **Step 1: Write failing tests** (fakes over both seams; required cases):

For `skill_list`: empty-everything header `[skills: 0 available]`; merged ordering built-in before learned alphabetically interleaved (assert full expected string with two entries incl. long-description `…` truncation); store failure → warning line appended, still non-error.

For `skill_view`: missing name → `MissingParameter`; unknown → `Error [SkillNotFound]`; built-in hit → annotation line format exact + body verbatim + usage recorded (fake asserts call); learned hit when catalog misses; view does NOT fail when usage recording fails (assert `[warning] usage not recorded` suffix and IsError=false).

- [ ] **Step 2: Run red**, implement both tools (house input pattern for `SkillViewInput`; list has zero params — reject ANY arguments object with unknown-parameter error? No: exec-style no-param tools accept `{}`; reject unknown keys via allowed-set of zero). Run green.

- [ ] **Step 3: Commit**

```powershell
git add src/eThangAgent.Tool.Domain tests/eThangAgent.Tool.Domain.Tests
git commit -m "feat(tools): skill_list and skill_view over built-in + learned stores"
```

### Task 6: `skill_manage` tool — create/update/delete with built-in protection

**Files:**

- Create: `src/eThangAgent.Tool.Domain/SkillManageTool.cs`, `SkillManageInput.cs`
- Test: `tests/eThangAgent.Tool.Domain.Tests/SkillManageToolTests.cs`

**Interfaces:**

- Consumes: `ISkillCatalog` (collision checks + built-in protection), `ILearnedSkillStore`, `SkillSpecifications.ValidName`.
- Produces: `ITool` named `skill_manage`. Input (house pattern): `action` required, exactly `Create | Update | Delete` (case-sensitive); `name` required (must match ValidName — violations are `InvalidParameterValue` quoting the rule); then per action:
  - Create: `description` required non-empty, `body` required non-empty, `provenanceSession` optional string. Fails `NameCollision` when a built-in exists with that name (message: built-ins are authoritative); fails `SkillExists` when learned exists.
  - Update: at least one of `description` / `body` required (else `InvalidParameterValue`); fails `BuiltInImmutable` for built-in names; fails `SkillNotFound` when no learned skill; bumps `Version = current + 1`, preserves original `CreatedAt`/`ProvenanceSessionId`, sets `UpdatedAt` to `DateTimeOffset.UtcNow`.
  - Delete: `confirm` required and must be exactly `true` (missing/false → `InvalidParameterValue` explaining the gate); fails `BuiltInImmutable` for built-ins; fails `SkillNotFound` when absent.
- Output contract: `[skill-manage] created '<name>' v1` / `[skill-manage] updated '<name>' v<N>` / `[skill-manage] deleted '<name>'`.

Timestamps: the tool injects `Func<DateTimeOffset>` clock (constructor parameter) so tests are deterministic; composition root passes `() => DateTimeOffset.UtcNow`.

- [ ] **Step 1: Write failing tests** (fakes; required cases):

1. Missing action / unknown action string → errors naming allowed actions.
2. Name charset violations (uppercase, leading hyphen, empty) → `InvalidParameterValue`.
3. Create happy path → fake store receives definition with Version 1, Source Learned, provenance passed through; output line exact.
4. Create over built-in name (`brainstorming`) → `NameCollision` mentioning authoritative built-ins; store never called.
5. Create over existing learned → `SkillExists`.
6. Create without description or without body → `MissingParameter`.
7. Update happy path → store receives Version current+1, original CreatedAt preserved, UpdatedAt = clock; output line `updated '<name>' v2`.
8. Update built-in → `BuiltInImmutable`; store never called.
9. Update unknown learned → `SkillNotFound`. Update with neither description nor body → `InvalidParameterValue`.
10. Delete without confirm / confirm:false → `InvalidParameterValue`; delete built-in → `BuiltInImmutable`; happy path → store delete called, output line exact.
11. Unknown parameter rejected.

- [ ] **Step 2: Run red**, implement, run green.

- [ ] **Step 3: Commit**

```powershell
git add src/eThangAgent.Tool.Domain tests/eThangAgent.Tool.Domain.Tests
git commit -m "feat(tools): skill_manage with built-in protection and versioned updates"
```

---

### Task 7: Wire the skill subsystem at the composition root

**Files:**

- Modify: `src/eThangAgent.CLI/Program.cs`

- [ ] **Step 1: Registrations** (following the SP1 forwarding pattern — one store instance):

```csharp
.AddSingleton<ISkillCatalog, EmbeddedSkillCatalog>()
.AddSingleton<ILearnedSkillStore, SqliteLearnedSkillStore>()
.AddSingleton<Func<DateTimeOffset>>(_ => () => DateTimeOffset.UtcNow)
```

Add three `AgentToolBinding` entries after the `search_files` binding:

```csharp
new AgentToolBinding(
    new SkillListTool(
        sp.GetRequiredService<ISkillCatalog>(),
        sp.GetRequiredService<ILearnedSkillStore>()),
    "List available skills."),
new AgentToolBinding(
    new SkillViewTool(
        sp.GetRequiredService<ISkillCatalog>(),
        sp.GetRequiredService<ILearnedSkillStore>()),
    "Load a skill's full content by name."),
new AgentToolBinding(
    new SkillManageTool(
        sp.GetRequiredService<ISkillCatalog>(),
        sp.GetRequiredService<ILearnedSkillStore>(),
        sp.GetRequiredService<Func<DateTimeOffset>>()),
    "Create, update, or delete learned skills."),
```

Note: `SqliteLearnedSkillStore` takes `AppDatabase` (already registered). The `Func<DateTimeOffset>` registration is generic-safe as shown.

- [ ] **Step 2: Verify** — `dotnet build` clean; `dotnet test` full suite green. Mechanical probe: the three new bindings reference real constructors, so compile success plus the E2E boot proves the provider graph resolves.

- [ ] **Step 3: Commit**

```powershell
git add src/eThangAgent.CLI/Program.cs
git commit -m "feat(cli): expose skill subsystem at composition root"
```

---

### Task 8: Superpowers bootstrap prompt provider

**Files:**

- Create: `src/eThangAgent.CLI/SuperpowersBootstrapPromptProvider.cs`
- Test: `tests/eThangAgent.CLI.Tests/SuperpowersBootstrapTests.cs`

**Interfaces:**

- Consumes: `ISkillCatalog` (Task 3); implements `ISystemPromptProvider` (Model Domain — check its exact member name when wiring; Program.cs already constructs providers).
- Produces: `SuperpowersBootstrapPromptProvider(ISkillCatalog)` whose build output assembles:

```text
<EXTREMELY_IMPORTANT>

{using-superpowers SKILL.md — byte-for-byte including frontmatter}

Tool mapping for this harness (eThang Agent): skills name actions; bind them:
- Read a file -> read tool; write/edit files -> write/edit; search files -> search_files
- Run shell commands/tests/git plumbing -> exec (PowerShell only)
- Dispatch a subagent -> spawn sub-agent capability
- Create/update todos -> todo tool; invoke or list skills -> skill_view / skill_list
- Ask the human a clarifying question -> clarify tool (MANDATORY during brainstorming)
- Commit work -> git_commit tool once available; never raw shell commits

The using-superpowers skill is ALREADY ACTIVE — do not load it again. Load other
skills with skill_view when they apply. This bootstrap is injected once per session.
</EXTREMELY_IMPORTANT>
```

The mapping block is a C# constant in the provider — single injection source. `EthangToolsMapping.md` (Task 3) carries the same table for on-demand viewing; keep both in sync when tools change (noted in both files).

- [ ] **Step 1: Write failing tests** (`tests/eThangAgent.CLI.Tests/SuperpowersBootstrapTests.cs`; construct the provider over `new EmbeddedSkillCatalog()` directly):

1. Output starts `<EXTREMELY_IMPORTANT>` and ends `</EXTREMELY_IMPORTANT>`.
2. Contains verbatim frontmatter marker `name: using-superpowers` and a stable body phrase from the embedded upstream file (open `src/eThangAgent.Skill.Domain/skills/using-superpowers/SKILL.md`, pick a distinctive sentence, assert `Contains`).
3. Contains every mapping key: `read`, `write`, `edit`, `search_files`, `exec`, `spawn`, `todo`, `skill_view`, `skill_list`, `clarify`.
4. Contains `ALREADY ACTIVE`.
5. Marker occurs exactly once: count of `"<EXTREMELY_IMPORTANT>"` occurrences == 1.
6. Provider over a catalog missing using-superpowers → `InvalidOperationException` (packaging defect = programmer error).

- [ ] **Step 2: Run red**, implement (thin assembly class; no caching needed — the composite builds once per session), green.

- [ ] **Step 3: Commit**

```powershell
git add src/eThangAgent.CLI/SuperpowersBootstrapPromptProvider.cs tests/eThangAgent.CLI.Tests/SuperpowersBootstrapTests.cs
git commit -m "feat(cli): superpowers bootstrap prompt provider with inline tool mapping"
```

### Task 9: Wire bootstrap + E2E injection assertion

**Files:**

- Modify: `src/eThangAgent.CLI/Program.cs` (composite gains the provider, FIRST in the list)
- Modify: `tests/eThangAgent.CLI.Tests/E2ETests.cs` (new test)

- [ ] **Step 1: Wire** — insert as first element of the `CompositeSystemPromptProvider` array:

```csharp
new SuperpowersBootstrapPromptProvider(sp.GetRequiredService<ISkillCatalog>()),
```

- [ ] **Step 2: Write the failing E2E test** — copy the choreography of `Repl_SendsConfiguredDefaultModel_ToProvider` (StartCli/ReadUntil/`/quit` teardown):

```csharp
[Fact]
public async Task Repl_InjectsSuperpowersBootstrap_OncePerSession()
{
    using var mock = new MockOpenRouterServer();
    mock.Start();
    using var process = StartCli(mock);
    var reader = process.StandardOutput;

    await ReadUntil(reader, "> ");
    await process.StandardInput.WriteLineAsync("hi");
    await process.StandardInput.FlushAsync();
    await ReadUntil(reader, "> ");

    var body = mock.LastChatRequestBody;
    Assert.NotNull(body);
    Assert.Contains("<EXTREMELY_IMPORTANT>", body);
    Assert.Contains("name: using-superpowers", body);
    Assert.Contains("ALREADY ACTIVE", body);
    Assert.Contains("skill_view", body);
    Assert.Equal(1, Regex.Count(body!, Regex.Escape("<EXTREMELY_IMPORTENT>".Replace("ENT", "ANT"))));

    await process.StandardInput.WriteLineAsync("/quit");
    await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
    Assert.Equal(0, process.ExitCode);
}
```

(Write the Regex.Count line plainly as `Regex.Count(body!, Regex.Escape("<EXTREMELY_IMPORTANT>"))` — the odd replace above is a transcription guard; do not copy it literally.)

- [ ] **Step 3: Red/green discipline for wiring tests**: comment out the composite entry from Step 1, run → red; restore, run → green.

- [ ] **Step 4: Full suite + commit**

```powershell
dotnet test
git add src/eThangAgent.CLI/Program.cs tests/eThangAgent.CLI.Tests/E2ETests.cs
git commit -m "feat(cli): inject superpowers bootstrap at session start with E2E assertion"
```

---

### Task 10: `clarify` tool with interactive + piped channels

**Files:**

- Create: `src/eThangAgent.Tool.Domain/IClarifyChannel.cs`, `ClarifyInput.cs`, `ClarifyTool.cs`
- Create: `src/eThangAgent.CLI/InteractiveClarifyChannel.cs`, `PipedClarifyChannel.cs`
- Test: `tests/eThangAgent.Tool.Domain.Tests/ClarifyToolTests.cs`, `tests/eThangAgent.CLI.Tests/PipedClarifyChannelTests.cs`

**Interfaces (Tool Domain):**

```csharp
public sealed record ClarifyQuestion(string Question, IReadOnlyList<string> Options, bool AllowFreeText);

/// <summary>Seam between the clarify tool and whatever can reach the human.</summary>
public interface IClarifyChannel
{
    Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default);
}
```

- Input (house pattern): `question` required non-empty; `options` optional string array (each element non-empty; ≥ 2 entries when present); `allowFreeText` required boolean.
- Tool flow: build question → channel returns raw answer line → integer parses as 1-based selection (returns that option text verbatim) else free text (`FreeTextNotAllowed` when disallowed). Out-of-range integers → `InvalidSelection` naming valid range. Channel failures surface verbatim. Output: `[clarify] answered: <text>`.
- `PipedClarifyChannel(TextReader)`: reads one line; EOF → `Cancelled` error.
- `InteractiveClarifyChannel(ITextWriter, IKeyReader)`: renders question + numbered options (`1) …` lines), minimal key loop (printables append, Backspace erases, Enter submits), Ctrl+C → `Cancelled`.

- [ ] **Step 1: Write failing tests** — tool cases via scripted fake channel: missing/empty question; single-option array rejected; missing allowFreeText; selection maps to option text; free text allowed/blocked (`FreeTextNotAllowed`); range violations (0 and N+1 → `InvalidSelection`); channel failure surfaced; exact output line. Piped channel via StringReader: number passthrough, text passthrough, EOF → Cancelled. Interactive channel: scripted key reader ('2', Enter) returns "2"; captured writer shows numbered options.

- [ ] **Step 2: Run red**, implement five files, green.

- [ ] **Step 3: Wire** — Program.cs registration (channel choice at composition root):

```csharp
.AddSingleton<IClarifyChannel>(_ => Console.IsInputRedirected
    ? new PipedClarifyChannel(Console.In)
    : new InteractiveClarifyChannel(/* construct exactly as LineEditor does in the interactive REPL */))
```

Binding after skill tools:

```csharp
new AgentToolBinding(
    new ClarifyTool(sp.GetRequiredService<IClarifyChannel>()),
    "Ask the human a clarifying question with structured options."),
```

- [ ] **Step 4: Full suite + commit**

```powershell
dotnet test
git add -A src tests
git commit -m "feat(tools): clarify tool with interactive and piped channels"
```

### Task 11: `todo` tool over the State Domain store

**Files:**

- Create: `src/eThangAgent.Tool.Domain/TodoDocument.cs`, `TodoInput.cs`, `TodoTool.cs`
- Modify: `eThangAgent.Tool.Domain.csproj` — reference `eThangAgent.State.Domain`
- Test: `tests/eThangAgent.Tool.Domain.Tests/TodoToolTests.cs`

**Interfaces:**

- Consumes: `IStateService` — `GetAsync(key)` → `Result<string>`, `SetAsync(key, value, expectedVersion)` → `Result<StateKeyValue>` (read `src/eThangAgent.State.Domain/IStateService.cs` first; adapt `.Version` field access to the actual `StateKeyValue` record shape).
- Storage layout: single key `todo/list`; value is a JSON array of items:

```json
[{"id":1,"description":"Write failing test","status":"Pending"}]
```

- `TodoDocument` (Tool Domain): parse/serialize the array strictly (unknown item fields rejected, status limited to `Pending | InProgress | Completed`, ids positive ints, descriptions non-empty). Missing key = empty document.
- `todo` input (house pattern): `action` required, exactly `Add | Update | Complete | Remove | List | Clear`; then per action:
  - Add: `description` required non-empty. New item id = max(existing) + 1 (empty list → 1). Status starts `Pending`.
  - Update: `id` required; at least one of `description` / `status` (status validated against enum).
  - Complete: `id` required; sets status `Completed` (idempotent on already-completed: allowed, no error).
  - Remove: `id` required.
  - List: no further params.
  - Clear: `confirm` required exactly `true` (same gate style as skill_manage delete).
- Unknown ids → `TodoNotFound` naming the id. Concurrency: read → mutate → `SetAsync(key, json, expectedVersion: versionFromRead)`; on `VersionConflict` error result tells the model to retry (rare in single-agent CLI; honest fail-closed).
- Output contract:
  - list: `[todo: N open / M total]` then one line per item `#id [status] description`, or `[todo: empty]` when none.
  - mutations: `[todo] added #3` / `[todo] updated #3` / `[todo] completed #3` / `[todo] removed #3` / `[todo] cleared`.

- [ ] **Step 1: Write failing tests** (fake `IStateService` capturing get/set calls, scripted returns; required cases):

1. Add to empty store → item #1 Pending persisted with correct JSON; output `[todo] added #1`.
2. Add id sequencing across gaps (1,2 removed, add → 3).
3. Update description / status; unknown id → `TodoNotFound`.
4. Complete existing (incl. already-completed idempotence); unknown → `TodoNotFound`.
5. Remove existing/unknown.
6. Clear requires confirm exactly true; clears to empty document.
7. List formatting exact (two items, mixed statuses) and `[todo: empty]`.
8. VersionConflict from store → surfaced as retryable error result.
9. Malformed stored JSON → `StorageCorrupt` error result (never silently reset).
10. Input rules: unknown action string, missing description on add, missing confirm on clear, unknown parameter, status outside enum on update.

- [ ] **Step 2: Run red**, implement, green.

- [ ] **Step 3: Wire** — binding after clarify:

```csharp
new AgentToolBinding(
    new TodoTool(sp.GetRequiredService<IStateService>()),
    "Track a workspace task list."),
```

- [ ] **Step 4: Full suite + commit**

```powershell
dotnet test
git add -A src tests
git commit -m "feat(tools): todo tool backed by durable state store"
```

---

### Task 12: README + final verification

**Files:**

- Modify: `README.md`

- [ ] **Step 1: README** — extend the capability bullets:

```markdown
- Skill subsystem: 14 embedded development-methodology skills (superpowers),
  session-start bootstrap injection, and `skill_list` / `skill_view` / `skill_manage` tools
- `clarify` tool — structured clarifying questions with numbered options
- `todo` tool — durable workspace task list
```

Also update the Commands/Usage section only if behavior changed (it does not — skills ride the model loop, not slash commands).

- [ ] **Step 2: Full verification** — `dotnet build && dotnet test`: every suite green including the new bootstrap E2E, skill tool suites, clarify/todo suites, and all pre-existing tests.

- [ ] **Step 3: Manual acceptance gate (documented, not CI)** — with a live `OPENROUTER_API_KEY`, start an interactive session and ask "What are your superpowers?" — the model must describe its skills without any file reads. Then "Let's make a react todo list" must trigger brainstorming before any code. Record the transcript in the PR description.

- [ ] **Step 4: Commit**

```powershell
git add README.md
git commit -m "docs: skill subsystem, clarify, and todo in readme"
```

---

## Plan Self-Review

- **Spec coverage:** verbatim skills + embedded catalog (Tasks 3), bootstrap injection via composite (Tasks 8–9), ethang tool mapping incl. mandatory clarify binding (Tasks 3, 8, 10), skill tools over built-in+learned stores (Tasks 4–6), usage tracking (Task 4 store + Task 5 view), clarify mandatory-for-brainstorming (Task 10 + mapping), todo tool (Task 11), built-in authority rules (Tasks 4–6 collision/immutable errors). Spec items deferred by design: learned-skill *creation by the agent itself* lands in SP4 using these primitives; E2E full brainstorm *conversation* flow is the Task 12 manual gate (mock-provider scripting of a full brainstorm is brittle; bootstrap presence + skill tools are mechanically asserted instead).
- **Placeholder scan:** Task 4 store implementation and Task 11 state adaptation explicitly direct the executor to read named existing files and mirror their patterns with concrete case lists and exact SQL/JSON given; every other step carries full code. No TBDs.
- **Type consistency:** `SkillDefinition(Name, Description, Body, Version, Source, ProvenanceSessionId, CreatedAt, UpdatedAt)` used identically across Tasks 1/3/4/5/6; `ISkillCatalog`/`ILearnedSkillStore` signatures match all consumers; `ClarifyQuestion`/`IClarifyChannel` consistent across Task 10 files; `IStateService` key/version usage matches the interface file quoted.
- **Known transcription hazards flagged in-place:** Task 9's Regex line (write plainly), Task 10's AnsiTerminal construction (copy REPL pattern), Task 11's `StateKeyValue` field name (read interface first).
