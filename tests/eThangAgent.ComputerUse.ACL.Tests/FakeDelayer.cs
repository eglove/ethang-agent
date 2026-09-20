namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Fake clock delayer: records scheduled delays instead of waiting them.</summary>
internal sealed class FakeDelayer : INotReadyDelayer
{
  public System.Collections.ObjectModel.Collection<TimeSpan> Delays { get; } = [];

  public Task DelayAsync(TimeSpan delay, CancellationToken ct = default)
  {
    Delays.Add(delay);
    return Task.CompletedTask;
  }
}
