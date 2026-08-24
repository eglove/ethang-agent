# P2 Capability Registry — Design Specification

**Date:** 2026-08-21 · **Status:** Approved in review (sections 1–4) · **Phase:** 2 of 7 (pi-fabric native port)

## Context & Goals

P1 shipped the exec core: one model-facing `exec` tool running model-authored PowerShell in a fresh in-process runspace, with tools exposed as script functions through a broker over `IToolRegistry`.

P2 generalizes what scripts can call. Grounded in pi-fabric's shape — providers contribute **action descriptors** (name, description, parameters), resolved as namespaced refs (`provider.action`), with generic discovery — minus its component-plane machinery (leases, staged commits, effect conflicts), which we deliberately do not port.

### Approved decisions

- **D1 — Collapse the model-facing surface (user decision B):** the model sees exactly one tool, `exec`. Everything else becomes a registry action invoked inside scripts. Native per-tool schemas disappear from the tool list; documentation becomes load-bearing and is carried in two layers (below).
- **D2 — New bounded context (user decision A):** `eThangAgent.Capability.Domain` owns the capability language. Tool.Domain keeps `ExecTool` as the only model-facing `ITool`; `ReadTool`'s implementation is untouched and surfaces as an action through an adapter.
- **D3 — Documentation is generated, never hand-maintained:** the system prompt carries a static teaching guide plus an action reference rendered from the live registry at prompt-build time; full descriptors are available on demand inside scripts.

## Architecture

```text
eThangAgent.Capability.Domain (NEW)      eThangAgent.Tool.Domain
  ActionDescriptor (name, summary,        ExecTool : ITool  ← the ONLY model-facing tool
    description, parameters)              ReadTool : ITool  ← implementation unchanged
  ICapabilityProvider                       ▲ adapter
  ICapabilityRegistry                     AgentToolsProvider (Capability.Domain)
  CapabilityRegistry                        
      ▲ consumed by                       
  eThangAgent.PowerShell.ACL            
  (broker, wrappers, introspection switch
   from IToolRegistry to ICapabilityRegistry)
```

- `ActionDescriptor`: name, explicit `Summary` (one line), full `Description`, parameter list shaped like `ToolParameter`.
- `ICapabilityProvider`: `Id`, `Actions`, `InvokeAsync(actionName, jsonArgs, ct)` → content string + `IsError`.
- `ICapabilityRegistry`: `Resolve(nameOrRef)`, `Providers`, `AllActions`; strict construction rules below.
- Production provider for P2: **`AgentToolsProvider`** (id `agent`) wrapping existing `ITool`s — read keeps its behavior, format contract, and tests with zero rewrite.
- The exec engine's broker, wrapper generation, and introspection consume `ICapabilityRegistry`; engine mechanics (fresh runspace per call, timeout/stop, state isolation, budgets, artifact overflow) are unchanged.
- Composition root wires `AgentToolsProvider([ReadTool])` → `CapabilityRegistry` → engine, and `ToolRegistry([ExecTool])` → agent. `ModelRequest.Tools` carries exec alone; agent loop and OpenRouter translation are untouched.

## Model-Facing Contract & Documentation

- Tool list: exactly `exec` (`program: String, required`) — unchanged shape.
- System prompt exec section = static guide (unchanged) + **action reference generated from the registry**: one line per action, `name(param: Type, …): Summary`. `ExecGuidePromptProvider` becomes registry-aware.
- `ActionDescriptor.Summary` is explicit and required at composition time (e.g. read → "Read lines from a text file."); no truncation heuristics.
- In-script docs on demand: `Get-AgentAction <name-or-ref>` → full descriptor (description incl. format contract + per-parameter docs); `Get-AgentProvider` → provider ids + action counts; `Get-AgentTool` remains the compact listing.
- Token-economy knob (discovery-first reference) deliberately deferred to P5+.

## Refs, Registration & Guardrails

- Refs are `provider.action` (`agent.read`). Wrapper functions are named by bare action name — unambiguous because registration enforces uniqueness.
- `Invoke-AgentTool -Name` and `Get-AgentAction` accept bare name or full ref; unknown names return actionable errors listing available actions (bounded).
- Registration (composition time, fail fast): duplicate action names across providers → throw (programmer error); provider ids non-empty; action names restricted to `[A-Za-z0-9_]` (they become PowerShell function names); empty action sets rejected.
- Carried forward unchanged: terminating-error gutters for action failures (`try/catch` works as documented); exec-level budgets; **no nested exec** — `exec` is never registered as an action; what the registry exposes is a composition-root decision.

## Testing Strategy

- **Unit — Capability.Domain (new test project, fakes only):** registration validation matrix (invalid names, empty ids/sets, duplicate names throw); resolution by bare name and ref; unknown-name errors listing actions; adapter mapping (summary/description/parameters from `ITool.Definition`, faithful `IsError`); reference renderer output against a fake registry.
- **Integration — PowerShell.ACL:** full stack registry → provider → adapter → ReadTool inside scripts; dispatcher with bare name and full ref; `Get-AgentAction` returns format-contract text; `Get-AgentProvider` lists `agent`; all P1 engine tests migrate to registry wiring with unchanged behavior.
- **E2E:** first request's `tools` array contains only `exec`; system prompt contains the generated reference line for read; exec-calling-read works end-to-end; self-correction and guide-injection carry forward.
- **Definition of done:** build green; full suite green (209 + new); coverage Capability.Domain ~100%, PowerShell.ACL ≥ 80%; deviations recorded in the plan header.

## Out of Scope (P2)

MCP provider (own ACL, later phase), nested/recursive exec (P4), additional providers beyond `agent` (state/schema arrive with P3+, memory with P6), discovery-first token knob (P5+), pi-fabric component-plane machinery (leases, staged commits, effect conflicts — not ported).
