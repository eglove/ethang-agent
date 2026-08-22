
### Task 1: New Memory.Domain project — tokenizer + query planner

**Files:**

- Create: `src/eThangAgent.Memory.Domain/eThangAgent.Memory.Domain.csproj` (net10.0; ProjectReferences: SharedKernel, Conversation.Domain)
- Create: `src/eThangAgent.Memory.Domain/LexicalTokenizer.cs`
- Create: `src/eThangAgent.Memory.Domain/MemoryQueryPlan.cs`
- Create: `tests/eThangAgent.Memory.Domain.Tests/eThangAgent.Memory.Domain.Tests.csproj` + `GlobalUsings.cs` (`global using Xunit;`)
- Test: `tests/eThangAgent.Memory.Domain.Tests/LexicalTokenizerTests.cs`, `MemoryQueryPlanTests.cs`
- Modify: solution (dotnet sln add both)

**Interfaces:**

- Produces: `LexicalTokenizer.Tokenize(string text) : IReadOnlyList<string>`; `abstract record MemoryQueryPlan` with nested `Browse`, `Terms(IReadOnlyList<string> Tokens)`, `RegexPattern(string Pattern)`; `MemoryQueryPlan.Plan(string? query, string queryMode = "literal")`.

Semantics ported from pi-fabric `tokenize.ts`: NFKC normalize → matches of `[\p{L}\p{N}_]+` → lowercase (invariant). `Plan`: null/whitespace → `Browse`; `queryMode="literal"` → `Terms` with DISTINCT tokens in first-occurrence order (literal input never compiled as regex — "a.c" yields tokens `[a, c]`); `"regex"` → `RegexPattern(query)` raw, unvalidated here; unknown mode → `ArgumentException` (programmer error — capability layer validates strings before calling).

- [ ] **Step 1:** Failing tests — tokenizer: ASCII words, Unicode letters (Cyrillic), digits+underscore kept, punctuation splits, NFKC ligature `ﬁ`→`fi`, case fold; planner: whitespace/null → Browse; literal metacharacters → terms; distinct-first-order terms; regex passthrough.
- [ ] **Step 2:** RED — projects do not exist.
- [ ] **Step 3:** Implement exactly the semantics above.
- [ ] **Step 4:** GREEN — full new test project passes.
- [ ] **Step 5:** Commit — `feat(memory-domain): lexical tokenizer and query planning`

---

### Task 2: Bounded regex executor

**Files:**

- Create: `src/eThangAgent.Memory.Domain/BoundedRegex.cs`
- Test: `tests/eThangAgent.Memory.Domain.Tests/BoundedRegexTests.cs`

**Interfaces:**

- Consumes: SharedKernel `Result<T>`.
- Produces: `static class BoundedRegex { public const int MaxPatternBytes = 1024; public const int MaxHaystackBytes = 2 * 1024 * 1024; public const int TimeoutMs = 250; public static Result<IReadOnlyList<int>> Execute(string pattern, IReadOnlyList<string> haystacks) }` — returns Ok(matched indices) or Fail with EXACT strings: `Error [regex_pattern_too_large]: Regex pattern exceeds 1024 bytes.` / `Error [invalid_regex]: <message>` / `Error [regex_timeout]: Regex exceeded the 250 ms budget.`

Semantics: UTF-8 pattern byte check BEFORE compile; compile `new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs))` catching `ArgumentException` → invalid_regex; per-haystack content longer than MaxHaystackBytes is TRUNCATED to the cap before testing (documented deviation-for-simplicity: pi-fabric enforces the budget across the batch; ours is per-entry, seam noted for batch accounting); `RegexMatchTimeoutException` → regex_timeout (never propagates). Match = regex.IsMatch per haystack; return indices in order.

- [ ] **Step 1:** Failing tests — oversized ASCII pattern (1100 × "a") → too_large; `(\[)` → invalid_regex whose message contains "Invalid pattern"; catastrophic `(a+)+$` against 5000 `a`s + trailing `b` under default budget → regex_timeout within ~2 s wall clock; `/hello/i`-style case-insensitive match across indices; haystack >2 MiB truncated so a match beyond the cap is NOT found.
- [ ] **Step 2:** RED → **Step 3:** implement → **Step 4:** GREEN.
- [ ] **Step 5:** Commit — `feat(memory-domain): bounded regex executor with typed failures`

---
