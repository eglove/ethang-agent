using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #92 wiring: the receipt ledger must be LIVE in a real container — the
///     registration is eager for a reason (DI is lazy and nothing else resolves the
///     ledger, so a plain registration would never subscribe and every receipt would read
///     NotFound). This pins the subscription against the container's own event stream, and
///     pins that agent.send's receipt names a handle the provider can then answer for.
///     The lesson from issue #269 is the reason this is a container test and not just a
///     domain test: a collaborator wired only in the type's own unit tests can be dead in
///     the container that actually serves the model.</summary>
public class MessageReceiptWiringTests
{
  private static ServiceProvider Build()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    return new ServiceCollection()
      .AddEThangAgentCore(
          new AgentSettings(
              new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
              new SubAgentOptions(null, 2)),
          Providers.OpenRouter,
          ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
          new AgentHostOptions(
              new FixedWorkspaceContext("app"), new UnrootedPathResolver()), db.Database)
      .BuildServiceProvider();
  }

  [Fact]
  public void Container_ResolvesMessageReceiptLedger_AsSingleton()
  {
    using ServiceProvider services = Build();
    MessageReceiptLedger ledger = services.GetRequiredService<MessageReceiptLedger>();

    Assert.Same(ledger, services.GetRequiredService<MessageReceiptLedger>());
  }

  [Fact]
  public void Ledger_IsSubscribedToTheContainersEventStream()
  {
    using ServiceProvider services = Build();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    MessageReceiptLedger ledger = services.GetRequiredService<MessageReceiptLedger>();
    AgentId child = AgentId.NewId();
    Guid messageId = Guid.NewGuid();

    events.Publish(new MessageDeliveredEvent(child, DateTimeOffset.UtcNow, "inbound", 0, 5, messageId));

    MessageReceipt? receipt = ledger.Find(child, messageId);
    Assert.NotNull(receipt);
    Assert.Equal(MessageReceiptState.Queued, receipt.State);
  }

  [Fact]
  public void Ledger_TracksTheDrainFactTheLoopPublishes()
  {
    using ServiceProvider services = Build();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    MessageReceiptLedger ledger = services.GetRequiredService<MessageReceiptLedger>();
    AgentId child = AgentId.NewId();
    Guid messageId = Guid.NewGuid();
    events.Publish(new MessageDeliveredEvent(child, DateTimeOffset.UtcNow, "inbound", 0, 5, messageId));

    events.Publish(new MailboxDrainedEvent(child, DateTimeOffset.UtcNow, [messageId]));

    Assert.Equal(MessageReceiptState.Received, ledger.Find(child, messageId)!.State);
  }

  [Fact]
  public void Ledger_TracksTheSettleRemainderFact()
  {
    using ServiceProvider services = Build();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    MessageReceiptLedger ledger = services.GetRequiredService<MessageReceiptLedger>();
    AgentId child = AgentId.NewId();
    Guid messageId = Guid.NewGuid();
    events.Publish(new MessageDeliveredEvent(child, DateTimeOffset.UtcNow, "inbound", 0, 5, messageId));

    events.Publish(new MailboxUndeliveredEvent(child, DateTimeOffset.UtcNow, [messageId]));

    Assert.Equal(MessageReceiptState.Undelivered, ledger.Find(child, messageId)!.State);
  }

  /// <summary>The provider the model actually dispatches through carries the ledger: the
  ///     action surface advertises agent.receipts and the dispatch answers with the
  ///     NotAvailable contract rather than an unknown action, which is what a provider
  ///     built without the wiring would do.</summary>
  [Fact]
  public async Task Container_Provider_AnswersReceipts()
  {
    using ServiceProvider services = Build();
    AgentCapabilityProvider provider = services.GetRequiredService<AgentCapabilityProvider>();

    Assert.Contains("receipts", AgentCapabilityProvider.ActionNames);
    CapabilityInvocationResult result = await provider.InvokeAsync("receipts",
        /*lang=json,strict*/ "{\"id\":\"" + AgentId.NewId() + "\"}",
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.EndsWith("receipts=0", result.Content, StringComparison.Ordinal);
  }
}
