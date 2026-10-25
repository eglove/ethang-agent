using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>W3.2 wiring: one locator instance serves every container the host opens;
///     each container registers its OWN live-mailbox view at construction; the session's
///     capability provider receives the SAME shared locator. A link to another session's
///     live child therefore resolves cross-container through the composed surface.
///     <para>Both sessions must come from ONE factory: the locator is per-FACTORY state
///     (the desktop process holds one factory), so two factories would each mint their own
///     - which is exactly what this test asserts against.</para>
///     <para>Derives from <see cref="SessionOwningTest"/> (issue #284 item 3): two sessions
///     here own two real ChildHost processes, and disposal as the last statement of the test
///     body is skipped whenever an assertion throws above it.</para></summary>
[Collection("EnvironmentSensitive")]
public class MailboxLocatorWiringTests : SessionOwningTest
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2));

  [Fact]
  public async Task Two_Sessions_Share_One_Locator_And_Resolve_Each_Others_Children()
  {
    AgentSettings settings = Settings();
    string dbPath = Track(Path.Combine(Path.GetTempPath(),
        "ethang-locatorwiring-" + Guid.NewGuid().ToString("N") + ".db"));
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    string ws = Track(Directory.CreateTempSubdirectory("ethang-locatorws").FullName);

    // ONE factory, two sessions: the shared locator is factory-scoped state.
    AgentSessionFactory factory = new(settings, new AppDatabase(dbPath));
    Result<AgentSession> openedA = await factory.CreateAsync(ws, Providers.OpenRouter,
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(openedA.IsSuccess, openedA.IsSuccess ? "" : openedA.Error.Code);
    TrackSession(openedA);

    Result<AgentSession> openedB = await factory.CreateAsync(ws, Providers.OpenRouter,
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(openedB.IsSuccess, openedB.IsSuccess ? "" : openedB.Error.Code);
    TrackSession(openedB);

    AgentSession a = openedA.Value;
    AgentSession b = openedB.Value;

    // The locator is ONE instance shared by both containers.
    ProcessMailboxLocator shared = a.Services.GetRequiredService<ProcessMailboxLocator>();
    Assert.Same(shared, b.Services.GetRequiredService<ProcessMailboxLocator>());

    // TWO sources per container: its live-mailbox registry, plus the wire-forwarding
    // proxy over its remote runtime's owned children. Every conversation runs in its own
    // host now (spec D6/D7), so the remote source is not opt-in and both containers
    // contribute it - four sources for the pair.
    Assert.Equal(4, shared.SourceCount);

    // A child of session B (registered directly in B's registry - the runtime does
    // exactly this at BeginRun) is resolvable through the locator from OUTSIDE B.
    Guid bChild = Guid.NewGuid();
    BoundedAgentMailbox mailbox = new();
    b.Services.GetRequiredService<ChildMailboxRegistry>().Register(new AgentId(bChild), mailbox);

    IAgentMailbox? resolved = shared.TryGet(new AgentId(bChild));
    Assert.Same(mailbox, resolved);

    // The locator answers with B's event stream (target-side audit surface).
    Assert.Same(b.Services.GetRequiredService<IAgentEvents>(), shared.EventsFor(new AgentId(bChild)));
  }
}
