namespace eThangAgent.AgentDomain;

/// <summary>Who made a chain transition (issue #300 D6). A TYPED value, not a nullable
///     session id, because the harness advancing a chain has no session and "which session
///     decided to start the next one" must be answerable with the honest answer "none of
///     them" - a null would be indistinguishable from "we forgot to record it".</summary>
public sealed record ChainActor
{
  /// <summary>The harness itself: the only actor on a transition no session caused.</summary>
  public const string HarnessToken = "harness";

  /// <summary>A human acting through the app.</summary>
  public const string UserToken = "user";

  private const string SessionPrefix = "session:";

  private ChainActor(string encoded, string? sessionId)
  {
    Encoded = encoded;
    SessionId = sessionId;
  }

  /// <summary>The harness acted.</summary>
  public static ChainActor Harness { get; } = new(HarnessToken, null);

  /// <summary>The user acted.</summary>
  public static ChainActor User { get; } = new(UserToken, null);

  /// <summary>The persisted form: <c>harness</c>, <c>user</c>, or <c>session:&lt;id&gt;</c>.</summary>
  public string Encoded { get; }

  /// <summary>The acting session's id; null for the harness and the user.</summary>
  public string? SessionId { get; }

  /// <summary>True when the harness is the actor.</summary>
  public bool IsHarness => string.Equals(Encoded, HarnessToken, StringComparison.Ordinal);

  /// <summary>A session acted.</summary>
  public static ChainActor Session(string sessionId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    return new ChainActor(SessionPrefix + sessionId.Trim(), sessionId.Trim());
  }

  /// <summary>Reads a persisted actor. STRICT: only the three documented shapes parse, so
  ///     a corrupt column is reported rather than silently read as the harness - an actor
  ///     nobody can name is worse than a named failure.</summary>
  public static bool TryDecode(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChainActor? actor)
  {
    actor = null;
    if (string.IsNullOrWhiteSpace(text))
    {
      return false;
    }

    string trimmed = text.Trim();
    if (string.Equals(trimmed, HarnessToken, StringComparison.Ordinal))
    {
      actor = Harness;
      return true;
    }

    if (string.Equals(trimmed, UserToken, StringComparison.Ordinal))
    {
      actor = User;
      return true;
    }

    if (trimmed.StartsWith(SessionPrefix, StringComparison.Ordinal) && trimmed.Length > SessionPrefix.Length)
    {
      string sessionId = trimmed[SessionPrefix.Length..].Trim();
      if (sessionId.Length > 0)
      {
        actor = Session(sessionId);
        return true;
      }
    }

    return false;
  }

  public override string ToString() => Encoded;
}
