using eThangAgent.AgentDomain;

namespace eThangAgent.AgentInfrastructure.Tests;

/// <summary>Unit tests for the in-memory heartbeat: first-read miss, beat-then-read returns
/// provider time, forget removes the entry, null clock is rejected. Fakes only.</summary>
public class InMemoryAgentHeartbeatTests
{
  private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
  }

  [Fact]
  public void TryGetLastBeat_WithoutBeat_ReturnsFalse()
  {
    InMemoryAgentHeartbeat heartbeat = new(new StubTimeProvider(DateTimeOffset.UnixEpoch));
    Assert.False(heartbeat.TryGetLastBeat(new AgentId(Guid.NewGuid()), out DateTimeOffset _));
  }

  [Fact]
  public void Beat_ThenTryGetLastBeat_ReturnsProviderNow()
  {
    StubTimeProvider clock = new(new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero));
    InMemoryAgentHeartbeat heartbeat = new(clock);
    AgentId id = new(Guid.NewGuid());
    heartbeat.Beat(id);
    Assert.True(heartbeat.TryGetLastBeat(id, out DateTimeOffset beat));
    Assert.Equal(clock.Now, beat);
  }

  [Fact]
  public void Forget_AfterBeat_RemovesEntry()
  {
    InMemoryAgentHeartbeat heartbeat = new(new StubTimeProvider(DateTimeOffset.UnixEpoch));
    AgentId id = new(Guid.NewGuid());
    heartbeat.Beat(id);
    heartbeat.Forget(id);
    Assert.False(heartbeat.TryGetLastBeat(id, out DateTimeOffset _));
  }

  [Fact]
  public void Constructor_NullTimeProvider_Throws()
    => Assert.Throws<ArgumentNullException>(() => new InMemoryAgentHeartbeat(null!));
}
