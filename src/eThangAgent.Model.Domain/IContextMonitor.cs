namespace eThangAgent.ModelDomain;

/// <summary>Receives per-provider-call token usage and keeps the running context status.
/// Implemented in the domain; wired onto the agent loop as an optional collaborator.</summary>
public interface IContextMonitor
{
  /// <summary>Records one provider call's usage together with the request's composition
  ///     (the character sizes of its three cost buckets). Usage null never reaches here:
  ///     the agent reports only provider-scored truth.</summary>
  void OnRequestUsage(TokenUsage usage, ContextComposition composition);

  /// <summary>The accounting state after the most recent report; zeroed defaults before the first.</summary>
  ContextStatus Status { get; }

  /// <summary>The estimated per-bucket breakdown of the last request's input tokens,
  ///     or null before the first report.</summary>
  ContextBreakdown? Breakdown { get; }
}
