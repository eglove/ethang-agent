using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>W3.2 remote half: the proxy forwards Deliver over the runtime seam and
///     leaves the owner-side members deliberately non-meaningful (Drain empty,
///     UnreadCount 0) — the doc contract, pinned.</summary>
public class RemoteMailboxProxyTests
{
  private sealed class RecordingRuntime : IAgentRuntime
  {
    public List<(Guid Id, string Text, string Sender)> Delivered { get; } = [];
    public bool Running { get; set; } = true;

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not used")));

    public Result<bool> Deliver(AgentId id, PendingMessage message)
    {
      if (!Running)
      {
        return Result.Failure<bool>(new DomainError("NotRunning", "gone"));
      }

      Delivered.Add((id.Value, message.Text, message.Sender));
      return Result.Success(true);
    }

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null) { }
  }

  [Fact]
  public void Deliver_ForwardsOverTheRuntime_WithMessageIntact()
  {
    RecordingRuntime runtime = new();
    Guid remoteChild = Guid.NewGuid();
    RemoteMailboxProxy proxy = new(runtime, new AgentId(remoteChild));

    Result<bool> delivered = proxy.Deliver(new PendingMessage("over the wire",
        MessageUrgency.Urgent, DateTimeOffset.UtcNow, "parent:root session"));

    Assert.True(delivered.IsSuccess);
    (Guid id, string text, string sender) = Assert.Single(runtime.Delivered);
    Assert.Equal(remoteChild, id);
    Assert.Equal("over the wire", text);
    Assert.Equal("parent:root session", sender);
  }

  [Fact]
  public void Deliver_FailureSurfacesUnchanged_ToTheSender()
  {
    RecordingRuntime runtime = new() { Running = false };
    RemoteMailboxProxy proxy = new(runtime, new AgentId(Guid.NewGuid()));

    Result<bool> delivered = proxy.Deliver(new PendingMessage("x", MessageUrgency.Normal,
        DateTimeOffset.UtcNow, "parent"));

    Assert.False(delivered.IsSuccess);
    Assert.Equal("NotRunning", delivered.Error?.Code);
  }

  [Fact]
  public void OwnerSideMembers_AreDeliberatelyNonMeaningful()
  {
    RemoteMailboxProxy proxy = new(new RecordingRuntime(), new AgentId(Guid.NewGuid()));

    Assert.Empty(proxy.Drain());
    Assert.Equal(0, proxy.UnreadCount);
  }
}
