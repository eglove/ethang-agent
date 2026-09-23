namespace eThangAgent.ToolDomain;

/// <summary>The no-host computer access: used when computer use is ENABLED but the host cannot
///     provide desktop automation (e.g. a net10.0 host without the Windows ACL). Every command
///     fails honestly with HELPER_UNAVAILABLE - the tool renders the typed failure with the retry
///     hint, never a faked receipt. Thread-safe and stateless.</summary>
public sealed class NullComputerAccess : IComputerAccess
{
  /// <inheritdoc />
  public Task<ComputerOutcome> ExecuteAsync(ComputerCommand command, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    return Task.FromResult<ComputerOutcome>(
        new ComputerOutcome.Failure(
            ComputerErrorCodes.HelperUnavailable,
            "computer use is enabled but this host cannot provide desktop automation; no command was dispatched."));
  }
}
