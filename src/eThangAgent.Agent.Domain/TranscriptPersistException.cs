namespace eThangAgent.AgentDomain;

/// <summary>Terminal persistence fault carrier: a child run's terminal record or
///     transcript could not be persisted. The runtime's catch-all reads the message
///     verbatim into the failure report — an honest persist-fault line, never a
///     misleading 'A task was canceled.'.</summary>
public sealed class TranscriptPersistException : Exception
{
  public TranscriptPersistException()
  {
  }

  public TranscriptPersistException(string message)
      : base(message)
  {
  }

  public TranscriptPersistException(string message, Exception innerException)
      : base(message, innerException)
  {
  }
}
