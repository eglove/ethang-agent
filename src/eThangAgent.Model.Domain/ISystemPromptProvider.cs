namespace eThangAgent.ModelDomain;

/// <summary>Supplies the system prompt for a session. THE PURITY CONTRACT (issue #267):
///     <see cref="Build"/> must be a pure render — two calls with no intervening
///     invalidation return byte-identical strings, and a call performs no I/O that could
///     change its answer.
///     <para>This is a cache-correctness rule, not a style preference. The system prompt
///     is the FIRST bytes of every request, so a prompt that drifts between iterations
///     silently invalidates the whole prompt cache from its first changed character —
///     every later token is re-billed at full input price and the harness has no way to
///     notice. Providers that read a store or a file must therefore snapshot at
///     CONSTRUCTION (see <c>SessionFilesPromptProvider</c>) rather than inside
///     <see cref="Build"/>; providers whose content legitimately changes mid-session
///     (the skills listing, after a hot reload) change it at a TURN BOUNDARY, which the
///     agent loop enforces by snapshotting the composed prompt once per turn
///     (<c>Agent.RunTurnAsync</c>) and reusing that snapshot for every iteration.</para>
///     <para>Nothing in this seam may emit a timestamp, a counter, or a request id: a
///     value that changes per call is the one thing guaranteed to break the prefix.</para></summary>
public interface ISystemPromptProvider
{
  /// <summary>Renders the prompt. Pure by contract: identical output for identical
  ///     state, and no state-changing I/O.</summary>
  string Build();
}
