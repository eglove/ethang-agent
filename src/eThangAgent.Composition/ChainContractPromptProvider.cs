using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>States the chain contract in the system prompt (issue #311 D1): what this session
///     is assigned, that the harness starts the next session when it marks its step done, that
///     the ledger is shared across the chain, and that the gate must be fresh before any
///     done-claim.
///     <para>WHY THE PROMPT AND NOT ONLY THE BRIEF: the brief (#302) is a seeded MESSAGE, so it
///     scrolls out of attention and a compaction can evict it. The prompt is in every request.
///     A session that has forgotten it is a chain step is a session that will try to start the
///     next step itself, or finish without the gate.</para>
///     <para>D2 — IT RENDERS EMPTY WHEN THERE IS NO CHAIN. A session a user started by hand is
///     not a chain step, and it must get a byte-identical prompt to the one it got before this
///     provider existed. The composite prompt drops empty entries, so 'empty' is the whole
///     mechanism — the same one <see cref="TrustedDirectoriesPromptProvider"/> uses.</para>
///     <para>D3 — PURE. The contract is a value passed at CONSTRUCTION, never a read performed
///     inside <see cref="Build"/>: the prompt is the first bytes of every request, and a
///     provider that consulted a store would change its answer mid-turn and silently invalidate
///     the provider's prompt cache (issue #267). The chain a session belongs to is fixed for
///     that session's life, exactly like the workspace root and the session files.</para>
///     <para>D4 — IT CARRIES NO PROCEDURE. The chain skill (#308) says how to work; this says
///     what the session is. Restating the skill here would put a copy of it in every request,
///     where it is both paid for on every iteration and free to drift from the skill.</para></summary>
public sealed class ChainContractPromptProvider(ChainContract? contract) : ISystemPromptProvider
{
  /// <summary>The contract this session runs under; null for a session that is not a chain
  ///     step. Snapshotted at construction, which is what makes <see cref="Build"/> pure.</summary>
  private readonly ChainContract? _contract = contract;

  /// <summary>The rendered contract, or EMPTY for a session outside a chain.</summary>
  public string Build() =>
      _contract is { } assigned ? ChainContractRenderer.Render(assigned) : string.Empty;
}
