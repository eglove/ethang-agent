
namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Physical-input serialization (task 16): while one input operation is in
///     flight, a second input call is REJECTED before dispatch (input_busy, nothing
///     dispatched, queue nothing). The End/dispose path frees the gate.</summary>
public class InputSerializerTests
{
  [Fact]
  public void SecondInput_WhileFirstInFlight_IsRejected()
  {
    InputSerializer serializer = new();
    InputOperation? first = serializer.TryBegin("click");
    Assert.NotNull(first);
    InputOperation? second = serializer.TryBegin("type_text");
    Assert.Null(second);
  }

  [Fact]
  public void Rejection_ExposesBusyOwnerMethod_ForDetails()
  {
    InputSerializer serializer = new();
    using InputOperation? first = serializer.TryBegin("press_key");
    InputOperation? second = serializer.TryBegin("click");
    Assert.Null(second);
    Assert.Equal("press_key", serializer.CurrentMethod);
  }

  [Fact]
  public void End_ReleasesGate_NextInputProceeds_QueueNothing()
  {
    InputSerializer serializer = new();
    InputOperation first = serializer.TryBegin("scroll")!;
    first.End();
    InputOperation? second = serializer.TryBegin("scroll");
    Assert.NotNull(second);
    second.End();
    Assert.False(serializer.IsBusy);
  }

  [Fact]
  public void ConcurrentTryBegin_ExactlyOneWinner()
  {
    InputSerializer serializer = new();
    const int racers = 16;
    using Barrier barrier = new(racers);
    int winners = 0;
    _ = Parallel.For(0, racers, racer =>
    {
      barrier.SignalAndWait();
      if (serializer.TryBegin("click") is not null)
      {
        _ = Interlocked.Increment(ref winners);
      }
    });
    Assert.Equal(1, winners);
  }

  [Fact]
  public void IsBusy_ReflectsInFlightOperation()
  {
    InputSerializer serializer = new();
    Assert.False(serializer.IsBusy);
    InputOperation op = serializer.TryBegin("drag")!;
    Assert.True(serializer.IsBusy);
    op.End();
    Assert.False(serializer.IsBusy);
  }
}
