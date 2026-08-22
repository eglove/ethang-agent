
### Task 7: Guide v1.5 — "Recalling earlier work"

**Files:**

- Modify: `src/eThangAgent.Tool.Domain/ExecGuide.cs` — bump version constant `1.4` → `1.5` (`rg -n '1\.4' src/eThangAgent.Tool.Domain/ExecGuide.cs`); add the new section after the delegation section
- Test: locate assertions with `rg -l 'v1\.4|Delegating' tests` and extend

**Interfaces:**

- Produces: section teaching, in order: (1) `memory.sessions` lists what conversations exist — run it when resuming work or before duplicating effort; (2) `memory.recall` searches transcripts — literal default (tokens ANDed), `queryMode='regex'` optional with budget errors (`regex_pattern_too_large` / `invalid_regex` / `regex_timeout`) that mean *simplify the pattern or use literal mode*; (3) scopes `global` or `session:<id>`; branches `active` vs `all`; (4) paging for long result sets; (5) memory is READ-ONLY — nothing to save yet.

- [ ] **Step 1:** Failing test updates (version + new teaching lines) → RED.
- [ ] **Step 2:** Rewrite section → GREEN on CLI.Tests non-E2E suite + full build.
- [ ] **Step 3:** Commit — `docs(guide): v1.5 recalling earlier work`

---
