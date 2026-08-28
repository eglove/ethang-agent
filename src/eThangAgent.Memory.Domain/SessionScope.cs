using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain;

/// <summary>Recall breadth: everything persisted.</summary>
public sealed record AllSessionsScope : SessionScope;

/// <summary>Recall breadth: exactly one agent session.</summary>
public sealed record SingleSessionScope(AgentId Id) : SessionScope;

/// <summary>
/// Recall breadth: everything persisted, or exactly one agent session.
/// </summary>
public abstract record SessionScope
{
  /// <summary>
  /// Parses the wire form strictly — no silent fallbacks. Null or "global"
  /// (case-insensitive) yields <see cref="AllSessionsScope"/>; "session:&lt;guid&gt;" with the
  /// remainder in exact 'D' format yields <see cref="SingleSessionScope"/>; anything else fails
  /// with the raw input echoed and both valid forms named.
  /// </summary>
  public static Result<SessionScope> Parse(string? raw)
  {
    if (raw is null || string.Equals(raw, "global", StringComparison.OrdinalIgnoreCase))
    {
      return Result.Success<SessionScope>(new AllSessionsScope());
    }

    if (!raw.StartsWith("session:", StringComparison.Ordinal) ||
        !Guid.TryParseExact(raw["session:".Length..], "D", out Guid id))
    {
      return Result.Failure<SessionScope>(new DomainError(
          "InvalidScope",
          $"Unknown scope '{raw}'. Valid scopes: global | session:<agentId>."));
    }

    SingleSessionScope scope = new(new AgentId(id));
    return Result.Success<SessionScope>(scope);
  }
}
