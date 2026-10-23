using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The port the new_session tool uses to start a fresh session
///     (issue #234 task 1): the Tool Domain owns the contract; the adapter
///     (a later task) wires it to the session factory. The port returns the
///     new session's id, or a typed DomainError naming the real cause.</summary>
public interface INewSessionPort
{
  /// <summary>Starts a fresh session for <paramref name="prompt"/>. The
  ///     <paramref name="handoffSummary"/> seeds the new session when present:
  ///     the new session is seeded with it as a System message before the prompt
  ///     runs (the compaction handoff, issue #234). Returns the new session's id,
  ///     or a typed failure.</summary>
  Task<Result<NewSessionPortResult>> StartAsync(string prompt, string? handoffSummary, CancellationToken ct = default);
}

/// <summary>The port's outcome: the new session's id.</summary>
public sealed record NewSessionPortResult(string SessionId);
