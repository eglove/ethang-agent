using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>W3.2 wiring: one locator instance serves every container the host opens;
///     each container registers its OWN live-mailbox view at construction; the session's
///     capability provider receives the SAME shared locator. A link to another session's
///     live child therefore resolves cross-container through the composed surface.</summary>
public class MailboxLocatorWiringTests
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new ZaiSettings(null, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2));

  private sealed class SilentChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Success("1"));
  }

  [Fact]
  public async Task Two_Sessions_Share_One_Locator_And_Resolve_Each_Others_Children()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), "ethang-locatorwiring-" + Guid.NewGuid().ToString("N") + ".db");
    string ws = Directory.CreateTempSubdirectory("ethang-locatorws").FullName;
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    try
    {
      AgentSessionFactory factory = new(Settings(), new AppDatabase(dbPath));
      Result<AgentSession> a = await factory.CreateAsync(ws, Providers.OpenRouter,
          new SilentChannel(), ct: TestContext.Current.CancellationToken);
      Result<AgentSession> b = await factory.CreateAsync(ws, Providers.OpenRouter,
          new SilentChannel(), ct: TestContext.Current.CancellationToken);
      Assert.True(a.IsSuccess && b.IsSuccess);

      // The locator is ONE process-wide instance, shared by both containers.
      ProcessMailboxLocator shared = a.Value.Services.GetRequiredService<ProcessMailboxLocator>();
      Assert.Same(shared, b.Value.Services.GetRequiredService<ProcessMailboxLocator>());
      Assert.Equal(2, shared.SourceCount);

      // A child of session B (registered directly in B's registry — the runtime does
      // exactly this at BeginRun) is resolvable through the locator from OUTSIDE B.
      Guid bChild = Guid.NewGuid();
      BoundedAgentMailbox mailbox = new();
      b.Value.Services.GetRequiredService<ChildMailboxRegistry>().Register(new AgentId(bChild), mailbox);

      ProcessMailboxLocator locator = shared;
      IAgentMailbox? resolved = locator.TryGet(new AgentId(bChild));
      Assert.Same(mailbox, resolved);

      // The source carries B's event stream (target-side audit surface).
      Assert.Same(b.Value.Services.GetRequiredService<IAgentEvents>(), shared.EventsFor(new AgentId(bChild)));

      await a.Value.Services.DisposeAsync().ConfigureAwait(true);
      await b.Value.Services.DisposeAsync().ConfigureAwait(true);
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
#pragma warning disable CA1031, S108 // Do not catch general exception types
      try
      {
        File.Delete(dbPath);
      }
      catch
      {
      }

      try
      {
        Directory.Delete(ws, true);
      }
      catch
      {
      }
#pragma warning restore CA1031, S108
    }
  }
}
