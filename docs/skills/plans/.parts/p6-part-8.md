
### Task 8: E2E seed-and-recall, full gate, outcome docs

**Files:**

- Test: `tests/eThangAgent.CLI.Tests/E2ETests.cs` — new `Repl_MemoryRecall_AgainstMockServer`
- Modify: `docs/skills/plans/2026-08-21-memory-recall.md` (this file — checkboxes + Outcome)

**Interfaces:** none new — verification task.

- [ ] **Step 1:** New E2E against the mock server: parent script turn 1 answers an exchange containing the distinctive phrase `xylophone harvest`; turn 2 runs `memory.sessions @{ limit = 50 }`; turn 3 runs `memory.recall @{ query = 'xylophone'; scope = 'global' }`; final text `recalled.`. Assert decoded tool messages: a `session=` line whose depth is 0 (the persisted root); a `[mem]` hit whose content contains `xylophone harvest`; the footer regex `--- memory: \d+ hits, page 1/\d+ ---`. Run this E2E alone first; SWEEP (`taskkill //F //IM testhost.exe 2>/dev/null; taskkill //F //IM eThangAgent.CLI.exe 2>/dev/null`) after EVERY dotnet test invocation.
- [ ] **Step 2:** Full CLI.Tests green (incl. E2Es); full solution `dotnet test` green — record totals.
- [ ] **Step 3:** Coverage probe: `dotnet test tests/eThangAgent.Memory.Domain.Tests --collect:'XPlat Code Coverage'`; per-class line-rate for `eThangAgent.MemoryDomain.*` classes ≥ 0.80 — add targeted tokenizer/regex/search tests if short, noting additions.
- [ ] **Step 4:** Mark this task's checkboxes `- [x]`, append `## Outcome` with suite totals, coverage actuals, deviations (if any), and the commit list; commit `git commit -am "docs(plan): P6 memory recall complete"`.

---
