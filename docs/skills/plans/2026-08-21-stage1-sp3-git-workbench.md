# Stage 1 / SP3 — Git Workbench Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the model typed, strictly-validated git tools — `git_status`, `working_diff`, `git_commit` (conventional / gitmoji / none styles, native emoji table, no npm) — through the PowerShell ACL, per spec `docs/skills/specs/2026-08-21-stage-1-methodology-port-design.md` (SP3).

**Architecture:** Message assembly is pure domain logic (`CommitMessage` record + `GitmojiCatalog`): validate → render → hand the finished string to the ACL. Execution is a new narrow `IGitQueryAccess` / `IGitCommitAccess` pair in Tool Domain, implemented by `PowerShellGitAccess` in FileSystem.ACL on the SAME open-runspace pattern as `PowerShellFileSystemAccess` (spec ruling: git is a shell command; the PowerShell ACL is the seam; no new ACL project). All commands run with the workspace root as working directory; a non-repo workspace fails with `NotAGitRepository`, never auto-init. Commits never stage anything: an empty index is `NothingStaged` with an actionable hint.

**Tech Stack:** C# / .NET 10, xUnit, embedded TSV resource (gitmoji table), PowerShell runspaces invoking git.exe, real scratch repositories for integration tests.

**Spec:** `docs/skills/specs/2026-08-21-stage-1-methodology-port-design.md`

## Global Constraints

- Strict boundaries: required parameters required; enums exact (`Conventional | Gitmoji | None`, case-sensitive ordinal); unknown parameters rejected; violations are validation ERRORS, never clamps or silent defaults. `style` is always explicit.
- Conditional requirements: `type` required iff style=Conventional; `emoji_key` required iff style=Gitmoji; supplying them under other styles is an error.
- `description`: trimmed non-empty, single line (reject `\n`), ≤ 72 chars — violation is an error naming the limit.
- `scope`: optional; ^[a-z0-9-]+$ when present.
- Never auto-stage; never amend; never push. The tool surface is status/diff/commit only.
- Domain code contains no `git` strings beyond message grammar — all execution lives behind the access interfaces.
- Every task ends green; DI wiring only in Program.cs; README updated in the final task.
- Namespaces: `eThangAgent.ToolDomain`; ACL class in `eThangAgent.FileSystem.ACL`.
- Integration tests arrange real scratch repositories (temp dir → `git init` → config user.email/name) using a small test-only helper that shells out to git; assertions never depend on network.

## File Structure

```text
src/eThangAgent.Tool.Domain/
  GitmojiCatalog.cs              # NEW — embedded gitmoji.tsv, exact-key lookup
  gitmoji.tsv                    # NEW — embedded data (canonical gitmoji set)
  CommitMessage.cs               # NEW — styles, validation, deterministic rendering
  GitStatus.cs                   # NEW — entry + page records
  GitDiff.cs                     # NEW — stats + bounded patch page
  GitCommitOutcome.cs            # NEW — hash + branch + rendered message
  IGitQueryAccess.cs             # NEW — status/diff queries
  IGitCommitAccess.cs            # NEW — commit command
  GitStatusTool.cs               # NEW (zero-param)
  WorkingDiffInput.cs / WorkingDiffTool.cs
  GitCommitInput.cs / GitCommitTool.cs
src/eThangAgent.FileSystem.ACL/
  PowerShellGitAccess.cs         # NEW — three scripts on open runspace + semaphore
src/eThangAgent.CLI/
  Program.cs                     # MODIFY — registrations + three bindings
README.md                         # MODIFY
tests/eThangAgent.Tool.Domain.Tests/
  GitmojiCatalogTests.cs / CommitMessageTests.cs
  GitStatusToolTests.cs / WorkingDiffToolTests.cs / GitCommitToolTests.cs
tests/eThangAgent.FileSystem.ACL.Tests/
  PowerShellGitAccessIntegrationTests.cs
```

---

### Task 1: Gitmoji catalog

**Files:**

- Create: `src/eThangAgent.Tool.Domain/gitmoji.tsv`, `src/eThangAgent.Tool.Domain/GitmojiCatalog.cs`, csproj EmbeddedResource entry
- Test: `tests/eThangAgent.Tool.Domain.Tests/GitmojiCatalogTests.cs`

**Interfaces:**

- Produces: `static class GitmojiCatalog { Result<Gitmoji> Lookup(string key); IReadOnlyList<Gitmoji> All { get; } }` where `sealed record Gitmoji(string Key, string Emoji, string Description)`. Keys are colon-wrapped (`:tada:`); lookup is EXACT ordinal — bare `tada` is rejected with an error stating the `:name:` format.

Error codes: `UnknownEmojiKey` (message lists 3 example valid keys, states total count).

- [ ] **Step 1: Embed the canonical table.** Create `gitmoji.tsv` (TAB-separated, header row `key\temoji\tdescription`) with EXACTLY these 66 rows:

```text
key emoji description
:tada: 🎉 Initial commit
:sparkles: ✨ Introduce new features
:bug: 🐛 Fix a bug
:memo: 📝 Add or update documentation
:lipstick: 💄 Add or update the UI and style files
:zap: ⚡ Improve performance
:fire: 🔥 Remove code or files
:ambulance: 🚑 Critical hotfix
:white_check_mark: ✅ Add, update, or pass tests
:lock: 🔒 Fix security issues
:bookmark: 🔖 Release / version tags
:rotating_light: 🚨 Fix compiler / linter warnings
:construction: 🚧 Work in progress
:green_heart: 💚 Fix CI build
:arrow_down: ⬇️ Downgrade dependencies
:arrow_up: ⬆️ Upgrade dependencies
:pushpin: 📌 Pin dependencies to specific versions
:recycle: ♻️ Refactor code
:heavy_plus_sign: ➕ Add a dependency
:heavy_minus_sign: ➖ Remove a dependency
:wrench: 🔧 Add or update configuration files
:hammer: 🔨 Add or update development scripts
:globe_with_meridians: 🌐 Internationalization and localization
:pencil2: ✏️ Fix typos in text
:rewind: ⏪ Revert changes
:twisted_rightwards_arrows: 🔀 Merge branches
:package: 📦 Add or update compiled files or packages
:alien: 👽 Update code due to external API changes
:truck: 🚚 Move or rename resources
:page_facing_up: 📄 Add or update license
:boom: 💥 Introduce breaking changes
:bento: 🍱 Add or update assets
:wheelchair: ♿ Improve accessibility
:bulb: 💡 Add or update comments in source code
:speech_balloon: 💬 Add or update text and literals
:card_file_box: 🗃️ Perform database related changes
:loud_sound: 🔊 Add or update logs
:mute: 🔇 Remove logs
:children_crossing: 🚸 Improve user experience / usability
:building_construction: 🏗️ Make architectural changes
:iphone: 📱 Work on responsive design
:clown_face: 🤡 Mock things
:see_no_evil: 🙈 Add or update a .gitignore file
:alembic: ⚗️ Perform experiments
:mag: 🔍 Improve SEO
:label: 🏷️ Add or update types
:seedling: 🌱 Add or update seed files
:triangular_flag_on_post: 🚩 Add, update, or remove feature flags
:goal_net: 🥅 Catch errors
:dizzy: 💫 Add or update animations and transitions
:wastebasket: 🗑️ Deprecate code that needs to be cleaned up
:passport_control: 🛂 Work on code related to authorization, roles and permissions
:adhesive_bandage: 🩹 Simple fix for a non-critical issue
:monocle_face: 🧐 Data exploration / inspection
:coffin: ⚰️ Remove dead code
:test_tube: 🧪 Add a failing test
:necktie: 👔 Add or update business logic
:stethoscope: 🩺 Add or update healthcheck
:technologist: 🧑‍💻 Improve developer experience
:money_with_wings: 💸 Add sponsorships or money related infrastructure
:thread: 🧵 Add or update code related to multithreading or concurrency
:poop: 💩 Write bad code that needs to be improved
:beers: 🍻 Write code drunkenly
:egg: 🥚 Add an easter egg
:camera_flash: 📸 Add or update snapshots
```

csproj:

```xml
<ItemGroup>
  <EmbeddedResource Include="gitmoji.tsv" />
</ItemGroup>
```

- [ ] **Step 2: Write failing tests** — All_LoadsExactly66Entries; Lookup_KnownKey_ReturnsExactRecord (':tada:' → 🎉 / 'Initial commit'); Lookup_BareName_Rejected (tada → UnknownEmojiKey, message mentions ':name:' format); Lookup_UnknownKey_ErrorListsExamplesAndCount; Lookup_CaseSensitive_Rejected (':TADA:'); All_KeysUnique; All_EmojiAndDescriptionsNonEmpty.

- [ ] **Step 3: Run red**, then implement: parse the embedded TSV once lazily (skip header; split on TAB; throw InvalidOperationException on malformed row — packaging defect); dictionary keyed by Key, StringComparer.Ordinal.

- [ ] **Step 4: Green + commit** — `feat(tools): embedded gitmoji catalog with exact-key lookup`

### Task 2: `CommitMessage` — validation + deterministic rendering

**Files:**

- Create: `src/eThangAgent.Tool.Domain/CommitMessage.cs`
- Test: `tests/eThangAgent.Tool.Domain.Tests/CommitMessageTests.cs`

**Interfaces:**

- Consumes: `GitmojiCatalog` (Task 1).
- Produces:

```csharp
public enum CommitStyle { Conventional, Gitmoji, None }

public sealed record CommitMessage(string Rendered, string Subject)
{
    public static Result<CommitMessage> Create(
        string style, string? type, string? scope, string? emojiKey,
        string description, string? body);
}
```

Validation rules (each violation its own error, checked in this order):

1. `style` must be exactly `Conventional | Gitmoji | None` (ordinal) → `InvalidStyle` listing the three.
2. Conventional: `type` required (`TypeRequired`) against the fixed set feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert (`UnknownType` listing the set); `emojiKey` present → `ParameterNotAllowed`.
3. Gitmoji: `emoji_key` required (`EmojiKeyRequired`); `GitmojiCatalog.Lookup` failure surfaces verbatim; `type` or `scope` present → `ParameterNotAllowed`.
4. None: `type`, `scope`, `emojiKey` all present → `ParameterNotAllowed` (message names which).
5. `description`: null/empty → `MissingDescription`; contains newline → `MultilineDescription`; trimmed length > 72 → `DescriptionTooLong` naming limit and actual; stored TRIMMED.
6. `scope` (Conventional only): must match ^[a-z0-9-]+$ → `InvalidScope`; stored as given.
7. `body`: may be null/empty (→ none); stored verbatim otherwise (multi-line allowed).

Rendering (deterministic):

- Subject: Conventional → `<type>(<scope>): <desc>` / `<type>: <desc>`; Gitmoji → `<emoji> <desc>`; None → `<desc>`.
- Body: appended as blank-line + body; rendered string ends with a single trailing `\n`.

- [ ] **Step 1: Write failing tests** covering EVERY rule above with exact expected subjects/rendered strings (e.g. Conventional+scope: subject `feat(tools): add write tool`; Gitmoji `:sparkles:` + 'Introduce new features' → subject `✨ Introduce new features`; None plain; body rendering `subject\n\nbody\n`). ~20 cases total, full assertions.

- [ ] **Step 2: Red**, implement (pure static factory; no clock, no IO), green.

- [ ] **Step 3: Commit** — `feat(tools): commit message assembly with conventional and gitmoji styles`

---

### Task 3: Git access seams + PowerShell implementation

**Files:**

- Create: `src/eThangAgent.Tool.Domain/GitStatus.cs`, `GitDiff.cs`, `GitCommitOutcome.cs`, `IGitQueryAccess.cs`, `IGitCommitAccess.cs`
- Create: `src/eThangAgent.FileSystem.ACL/PowerShellGitAccess.cs`
- Test: `tests/eThangAgent.FileSystem.ACL.Tests/PowerShellGitAccessIntegrationTests.cs`

**Interfaces:**

```csharp
public sealed record GitStatusEntry(string Code, string Path);          // two-char porcelain XY
public sealed record GitStatus(string Branch, IReadOnlyList<GitStatusEntry> Staged,
    IReadOnlyList<GitStatusEntry> Unstaged, IReadOnlyList<string> Untracked);
public sealed record GitDiffStats(int Files, int Additions, int Deletions);
public sealed record GitDiff(GitDiffStats Stats, string Patch, bool Truncated, int TotalChars);
public sealed record GitCommitOutcome(string Hash, string Branch, string Message);

public interface IGitQueryAccess
{
    Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default);
    Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default);
}
public interface IGitCommitAccess
{
    /// <summary>Commits the CURRENT INDEX with the finished message. Never stages.</summary>
    Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default);
}
```

Error codes: `NotAGitRepository`, `NothingStaged` (commit with empty index; hint: stage via exec git add), `GitError` (non-zero git exit with stderr text).

Scripts (one per operation, on the class's own open runspace + semaphore — mirror `PowerShellFileSystemAccess` construction exactly):

- **status**: `git -C $Root rev-parse --abbrev-ref HEAD` (branch; failure → NotAGitRepository via stderr match or exit code) then `git -C $Root status --porcelain`; parse lines: first two chars = XY; `??` → untracked (path is rest of line minus prefix); renames `R  old -> new` keep FULL `old -> new` string as Path in staged; everything else split by whether X != ' ' (staged) or Y != ' ' (unstaged); both dirty (X!=' ',Y!=' ') appears in BOTH lists.
- **diff**: scope Staged → `git diff --cached --numstat` + `git diff --cached`; Unstaged → plain; All → both patches concatenated with a `\n### staged ###\n` / `\n### unstaged ###\n` separator line between numstat sections aggregated per requested set. numstat rows: `add\tdelete\tpath` (binary → `-\t-` counted as 0/0). Patch bounded at MaxPatchChars = 20000: if longer, cut at last complete line before cap, Truncated=true, TotalChars=actual full length.
- **commit**: check `git diff --cached --name-only` output empty → NothingStaged; else write message to temp file ([System.IO.Path]::GetTempFileName()), `git commit -F <tmp>` capturing output, delete temp file, then `git rev-parse --short HEAD` and branch. Any non-zero exit → GitError with stderr.
All three: run with working directory = repoPath (script param Root used via -C flag so no cwd juggling).

- [ ] **Step 1: Test helper** (in the integration test file): `static string InitRepo()` — temp dir, `git init`, `git config user.email test@local`, `git config user.name test`, returns path. Arrange commits use `System.Diagnostics.Process` directly (test-only). Dispose deletes tree.

- [ ] **Step 2: Failing integration tests** (~14 cases): status on fresh repo (Branch main/master-or-default, clean lists); status after stage+untracked+modify mix (exact group membership incl. both-dirty dual-listing and rename staging); NotAGitRepository on plain temp dir; diff empty stats each scope; diff staged vs unstaged content separation (patch contains the right added line, not the other); All-scope separator present; truncation flag + TotalChars accounting on a >cap patch (generate a file with many long lines); commit happy path (hash non-empty, branch matches, message round-trips exactly incl. body and trailing newline); NothingStaged on fresh repo AND after committing everything; commit does NOT include unstaged edits (assert committed file content excludes them); NotAGitRepository commit.

- [ ] **Step 3: Red**, implement, green. Run FULL FileSystem.ACL.Tests (existing suites stay green).

- [ ] **Step 4: Commit** — `feat(fs-acl): git queries and index-only commit over runspace`

### Task 4: The three tools

**Files:**

- Create: `src/eThangAgent.Tool.Domain/GitStatusTool.cs`, `WorkingDiffInput.cs`, `WorkingDiffTool.cs`, `GitCommitInput.cs`, `GitCommitTool.cs`
- Test: `tests/eThangAgent.Tool.Domain.Tests/GitStatusToolTests.cs`, `WorkingDiffToolTests.cs`, `GitCommitToolTests.cs`

**Interfaces:**

- Consumes: `WorkspacePathResolver` (resolve the tool's implicit root: resolver.Resolve(".")), `IGitQueryAccess`, `IGitCommitAccess`, `CommitMessage.Create`.
- Produces:

`git_status` — zero parameters (reject any unknown key; `{}` accepted). Output contract:

```text
[git-status <branch>: S staged, U unstaged, T untracked]
staged:
M  src/a.cs
unstaged:
 M src/b.cs
untracked:
?? notes.txt
```

Empty groups are omitted; fully clean repo → `[git-status <branch>: clean]`.

`working_diff` — input (house pattern): `scope` required, exactly `Staged | Unstaged | All`; `path` optional non-empty string, resolved via resolver (`PathOutsideWorkspace` surfaces). Output contract:

```text
[working-diff scope=<scope> path=<path|none>: N file(s), +A/-D lines]
<patch verbatim>
[warning] truncated at 20000 chars; total 45123 — narrow with path/scope
```

No changes → `[working-diff ...: no differences]`.

`git_commit` — input: `style` (required), `type`, `scope`, `emoji_key`, `description` (required), `body` (optional). Unknown keys rejected. Flow: `CommitMessage.Create(...)` → failure surfaces its codes verbatim → resolve root → `_commits.CommitAsync(root, msg.Rendered)`. Output contract:

```text
[git-commit <hash>] committed on <branch>
<message exactly as committed>
```

Backend errors (`NothingStaged`, `NotAGitRepository`, `GitError`) surface verbatim with their hints.

- [ ] **Step 1: Failing tests** (fakes over both access seams + synthetic-root resolver):

git_status (~7): clean formatting; mixed groups exact full-string output; empty groups omitted; backend NotAGitRepository surfaced; backend GitError surfaced; unknown parameter rejected; arguments must be empty-or-missing-object (pass `"{}"`).

working_diff (~10): missing/invalid scope enum; path outside workspace; success header math from fake stats (+3/-1, 2 files); patch passthrough verbatim; truncation warning line exact with cap number and TotalChars; no-differences contract; backend errors surfaced; unknown param; path resolution passes resolved absolute to fake (captured).

git_commit (~12): happy conventional with scope (fake captures EXACT rendered string incl. trailing newline; output annotation `[git-commit abc1234] committed on main` + message block); gitmoji rendering through to captured message; style None; every CommitMessage validation code surfaced verbatim (spot 5: InvalidStyle, UnknownType, TypeRequired, ParameterNotAllowed, DescriptionTooLong); description missing; NothingStaged surfaced; body flows through.

- [ ] **Step 2: Red**, implement, green. Full Tool.Domain suite green.

- [ ] **Step 3: Commit** — `feat(tools): git_status, working_diff, and typed git_commit tools`

---

### Task 5: Wire, README, verify

**Files:**

- Modify: `src/eThangAgent.CLI/Program.cs`, `README.md`

- [ ] **Step 1: Registrations + bindings.** One shared instance pattern like SP1:

```csharp
.AddSingleton<PowerShellGitAccess>()
.AddSingleton<IGitQueryAccess>(sp => sp.GetRequiredService<PowerShellGitAccess>())
.AddSingleton<IGitCommitAccess>(sp => sp.GetRequiredService<PowerShellGitAccess>())
```

Bindings after todo:

```csharp
new AgentToolBinding(
    new GitStatusTool(
        sp.GetRequiredService<WorkspacePathResolver>(),
        sp.GetRequiredService<IGitQueryAccess>()),
    "Show branch and working-tree status."),
new AgentToolBinding(
    new WorkingDiffTool(
        sp.GetRequiredService<WorkspacePathResolver>(),
        sp.GetRequiredService<IGitQueryAccess>()),
    "Show staged/unstaged/all working-tree diff, bounded."),
new AgentToolBinding(
    new GitCommitTool(
        sp.GetRequiredService<WorkspacePathResolver>(),
        sp.GetRequiredService<IGitCommitAccess>()),
    "Commit the current index with a validated conventional or gitmoji message."),
```

(Adjust constructor argument orders to whatever Task 4 implemented — brief's interfaces govern.)

- [ ] **Step 2: README** — after the search_files bullet add:

```markdown
- `git_status` / `working_diff` tools — inspect branch state and bounded diffs
- `git_commit` tool — index-only commits with validated conventional or gitmoji messages
```

- [ ] **Step 3: Full verification** — build 0 errors; FULL test suite green; report exact totals.

- [ ] **Step 4: Commit** — `feat(cli): expose git workbench at composition root; document in readme`

---

## Plan Self-Review

- **Spec coverage:** CQRS split (IGitQueryAccess vs IGitCommitAccess) ✓; porcelain status parse ✓; diff scopes Staged/Unstaged/All with bounded continuation accounting ✓; commit styles explicit with conditional type/emoji requirements ✓; ≤72 error-not-clamp ✓; native emoji table, no npm ✓ (66 canonical entries); nothing-staged refusal with hint, never auto-stages ✓; hash+message result contract ✓; markdown-commit-skill retirement = nothing to remove (no such skill was ever ported) — noted here so an executor does not hunt for it.
- **Placeholder scan:** Task 1 table is complete data; Tasks 2–4 specify every rule/case precisely; Task 4 case lists name expected outputs for each. No TBDs.
- **Type consistency:** GitStatusEntry/Page, GitDiffStats/Patch page, GitCommitOutcome used identically across Tasks 3–5; CommitMessage.Create signature consistent between Tasks 2 and 4; IGitQueryAccess consumed by both query tools.
- **Known risk flagged:** Task 3 rename-path parsing keeps `old -> new` verbatim (porcelain quoting edge cases exist); acceptable at Stage 1, revisit if renames render oddly.
