namespace eThangAgent.ToolDomain;

/// <summary>The Computer Use seam (spec 1.2): the Tool Domain's only door to native
///     desktop automation. The ComputerUse.ACL implements it over the supervised
///     broker process; the tool never knows UIA, SendInput, GDI, or the pipe
///     protocol exist. Failures return <see cref="ComputerOutcome.Failure"/> values
///     — never domain exceptions.</summary>
public interface IComputerAccess
{
  /// <summary>Executes one validated command against the desktop. Returns the
  ///     observation, the action receipt, or a typed failure value.</summary>
  Task<ComputerOutcome> ExecuteAsync(ComputerCommand command, CancellationToken ct = default);
}
