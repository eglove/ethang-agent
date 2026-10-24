using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #92: the send handshake at the capability surface. agent.send's
///     receipt names the message handle it enqueued, and agent.receipts answers what
///     became of it — queued, received (drained into the child's conversation), or
///     undelivered (the run settled without draining it). Before this, the receipt
///     claimed delivery and the parent had no way to learn whether the child ever saw
///     the message.</summary>
public class AgentSendHandshakeTests
{
  private static AgentRecord ParentAtDepth(int depth) =>
      AgentRecord.Spawned(AgentId.NewId(), null, depth, "prov/parent", null, "root task", DateTimeOffset.UtcNow);

  private static AgentRecord RunningChild(AgentId parent) =>
      AgentRecord.Spawned(AgentId.NewId(), parent, 1, "prov/child", null, "child task", DateTimeOffset.UtcNow)
          with
      { Status = AgentStatus.Running, CompletedAt = null };

  private sealed class FakeQueries(AgentRecord child) : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => Task.FromResult(id == child.Id
            ? Result.Success(child)
            : Result.Failure<AgentRecord>(new DomainError("NotFound", $"No agent exists with id '{id}'.")));

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("NotFound", $"No agent exists with id '{id}'.")));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }

  /// <summary>The runtime fake that also PUBLISHES the delivery fact the way the
  ///     in-process runtime does, so the ledger sees what a real send produces.</summary>
  private sealed class PublishingRuntime(MessageReceiptLedger ledger) : IAgentRuntime
  {
    public List<PendingMessage> Delivered { get; } = [];

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new AgentRunOutcome(id, AgentStatus.Completed, null, "done", "prov/child", 1)));

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
        => Task.FromResult(Result.Success(id));

    public Result<bool> Deliver(AgentId id, PendingMessage message)
    {
      Delivered.Add(message);
      ledger.OnEvent(new MessageDeliveredEvent(id, message.At, "inbound",
          (int)message.Urgency, System.Text.Encoding.UTF8.GetByteCount(message.Text), message.Id));
      return Result.Success(true);
    }

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null) { }
  }

  private static (AgentCapabilityProvider Provider, PublishingRuntime Runtime, MessageReceiptLedger Ledger, AgentRecord Child)
      MakeProvider(MessageReceiptLedger? ledger = null)
  {
    AgentRecord parent = ParentAtDepth(0);
    AgentRecord child = RunningChild(parent.Id);
    MessageReceiptLedger receipts = ledger ?? new MessageReceiptLedger();
    PublishingRuntime runtime = new(receipts);
    AgentCapabilityProvider provider = new(new AgentProviderServices(
        new UnusedSpawnCommand(), new FakeQueries(child), () => parent, runtime,
        Receipts: receipts));
    return (provider, runtime, receipts, child);
  }

  private sealed class UnusedSpawnCommand : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentId>(new DomainError("Unused", "not exercised here")));
  }

  // --- agent.send ----------------------------------------------------------

  [Fact]
  public async Task Send_Receipt_NamesTheMessageHandle_AndItsQueuedState()
  {
    (AgentCapabilityProvider provider, PublishingRuntime runtime, MessageReceiptLedger ledger, AgentRecord child) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("send",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\",\"text\":\"check the file\"}",
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    // The receipt names the handle the caller can ask about later, and states the
    // honest fact: the message is ENQUEUED, not yet received.
    Assert.Matches(@"^delivered to=" + child.Id + @" urgency=NORMAL msg=[0-9a-f\-]{36} state=queued$", result.Content);
    PendingMessage delivered = Assert.Single(runtime.Delivered);
    Assert.Contains($"msg={delivered.Id}", result.Content, StringComparison.Ordinal);
    Assert.Equal(MessageReceiptState.Queued, ledger.Find(child.Id, delivered.Id)!.State);
  }

  [Fact]
  public async Task Send_ThenDrain_ReportsReceived()
  {
    (AgentCapabilityProvider provider, PublishingRuntime runtime, MessageReceiptLedger ledger, AgentRecord child) = MakeProvider();
    CapabilityInvocationResult sent = await provider.InvokeAsync("send",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\",\"text\":\"check the file\"}",
        ct: TestContext.Current.CancellationToken);
    PendingMessage delivered = Assert.Single(runtime.Delivered);

    // The child's loop drains at its next safe point: the fact the handshake exists for.
    ledger.OnEvent(new MailboxDrainedEvent(child.Id, DateTimeOffset.UtcNow, [delivered.Id]));

    CapabilityInvocationResult receipt = await provider.InvokeAsync("receipts",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\",\"msg\":\"" + delivered.Id + "\"}",
        ct: TestContext.Current.CancellationToken);

    Assert.False(receipt.IsError);
    Assert.Equal($"msg={delivered.Id} child={child.Id} state=received", receipt.Content);
    Assert.False(sent.IsError);
  }

  // --- agent.receipts ------------------------------------------------------

  [Fact]
  public async Task Receipts_ForAnUndeliveredMessage_ReportsUndelivered()
  {
    (AgentCapabilityProvider provider, PublishingRuntime runtime, MessageReceiptLedger ledger, AgentRecord child) = MakeProvider();
    _ = await provider.InvokeAsync("send",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\",\"text\":\"check the file\"}",
        ct: TestContext.Current.CancellationToken);
    PendingMessage delivered = Assert.Single(runtime.Delivered);
    ledger.OnEvent(new MailboxUndeliveredEvent(child.Id, DateTimeOffset.UtcNow, [delivered.Id]));

    CapabilityInvocationResult receipt = await provider.InvokeAsync("receipts",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\",\"msg\":\"" + delivered.Id + "\"}",
        ct: TestContext.Current.CancellationToken);

    Assert.False(receipt.IsError);
    Assert.Equal($"msg={delivered.Id} child={child.Id} state=undelivered", receipt.Content);
  }

  [Fact]
  public async Task Receipts_WithoutMsg_ListsEveryReceiptForTheChild()
  {
    (AgentCapabilityProvider provider, PublishingRuntime runtime, MessageReceiptLedger _, AgentRecord child) = MakeProvider();
    _ = await provider.InvokeAsync("send",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\",\"text\":\"first\"}",
        ct: TestContext.Current.CancellationToken);
    _ = await provider.InvokeAsync("send",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\",\"text\":\"second\"}",
        ct: TestContext.Current.CancellationToken);
    Assert.Equal(2, runtime.Delivered.Count);

    CapabilityInvocationResult receipts = await provider.InvokeAsync("receipts",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\"}",
        ct: TestContext.Current.CancellationToken);

    Assert.False(receipts.IsError);
    string[] lines = receipts.Content.Split('\n');
    Assert.Equal(2, lines.Length);
    Assert.Equal($"msg={runtime.Delivered[0].Id} child={child.Id} state=queued", lines[0]);
    Assert.Equal($"msg={runtime.Delivered[1].Id} child={child.Id} state=queued", lines[1]);
  }

  [Fact]
  public async Task Receipts_ForAnUnknownMessage_ReportsNotFound()
  {
    (AgentCapabilityProvider provider, PublishingRuntime _, MessageReceiptLedger _, AgentRecord child) = MakeProvider();

    CapabilityInvocationResult receipt = await provider.InvokeAsync("receipts",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\",\"msg\":\"" + Guid.NewGuid() + "\"}",
        ct: TestContext.Current.CancellationToken);

    Assert.True(receipt.IsError);
    Assert.StartsWith("Error [NotFound]", receipt.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Receipts_WithNoChildReceipts_ReportsAnEmptyLine()
  {
    (AgentCapabilityProvider provider, PublishingRuntime _, MessageReceiptLedger _, AgentRecord child) = MakeProvider();

    CapabilityInvocationResult receipts = await provider.InvokeAsync("receipts",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\"}",
        ct: TestContext.Current.CancellationToken);

    Assert.False(receipts.IsError);
    Assert.Equal($"child={child.Id} receipts=0", receipts.Content);
  }

  [Fact]
  public async Task Receipts_WithoutALedgerWired_IsNotAvailable()
  {
    AgentRecord parent = ParentAtDepth(0);
    AgentRecord child = RunningChild(parent.Id);
    AgentCapabilityProvider provider = new(new AgentProviderServices(
        new UnusedSpawnCommand(), new FakeQueries(child), () => parent, new PublishingRuntime(new MessageReceiptLedger())));

    CapabilityInvocationResult receipts = await provider.InvokeAsync("receipts",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\"}",
        ct: TestContext.Current.CancellationToken);

    Assert.True(receipts.IsError);
    Assert.StartsWith("Error [NotAvailable]", receipts.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Receipts_IsAdvertisedInTheActionSurface()
      => Assert.Contains("receipts", AgentCapabilityProvider.ActionNames);

  [Fact]
  public async Task Send_WithNoLedgerWired_KeepsTheLegacyReceipt()
  {
    AgentRecord parent = ParentAtDepth(0);
    AgentRecord child = RunningChild(parent.Id);
    AgentCapabilityProvider provider = new(new AgentProviderServices(
        new UnusedSpawnCommand(), new FakeQueries(child), () => parent, new PublishingRuntime(new MessageReceiptLedger())));

    CapabilityInvocationResult result = await provider.InvokeAsync("send",
        /*lang=json,strict*/ "{\"id\":\"" + child.Id + "\",\"text\":\"check the file\"}",
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    // Legacy wiring (no ledger, tests, older hosts): byte-identical receipt.
    Assert.Equal($"delivered to={child.Id} urgency=NORMAL", result.Content);
  }
}
