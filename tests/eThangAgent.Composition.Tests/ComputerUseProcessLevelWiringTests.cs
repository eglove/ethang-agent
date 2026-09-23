using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031

namespace eThangAgent.Composition.Tests;

/// <summary>Fix round 5 (F4): the computer-use registry + access provider are
///     PROCESS-level singletons - the factory mints ONE BrokerComputerAccessProvider
///     and every session container (create AND resume) resolves the same instance,
///     so two sessions over one workspace share the ObservationLedger/FrameRegistry
///     and the per-root access cache. Per-session providers would give each session
///     its own ledger over one broker connection, defeating fail-closed indexing.
///     ForWorkspace never spawns a broker (lazy), so this test stays unit-clean.</summary>
public class ComputerUseProcessLevelWiringTests
{
  private static AgentSettings Settings(bool computerUse) => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new ZaiSettings(null, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2),
      ComputerUse: computerUse);

  private static (AgentSessionFactory Factory, string DbPath) CreateFactory(AgentSettings settings)
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-f4-{Guid.NewGuid():N}.db");
    return (new AgentSessionFactory(settings, new AppDatabase(dbPath)), dbPath);
  }

  [Fact]
  public async Task TwoSessions_ResolveTheSameComputerAccessProvider_ProcessLevel()
  {
    (AgentSessionFactory factory, string dbPath) = CreateFactory(Settings(computerUse: true));
    try
    {
      DirectoryInfo dirA = Directory.CreateTempSubdirectory("ethang-f4-ws-a");
      DirectoryInfo dirB = Directory.CreateTempSubdirectory("ethang-f4-ws-b");
      try
      {
        Result<AgentSession> openA = await factory.CreateAsync(dirA.FullName, Providers.OpenRouter,
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(openA.IsSuccess, openA.Error?.Message ?? "open failed");
        Result<AgentSession> openB = await factory.CreateAsync(dirB.FullName, Providers.OpenRouter,
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(openB.IsSuccess, openB.Error?.Message ?? "open failed");

        IComputerAccessProvider providerA = openA.Value.Services.GetRequiredService<IComputerAccessProvider>();
        IComputerAccessProvider providerB = openB.Value.Services.GetRequiredService<IComputerAccessProvider>();
        Assert.Same(providerA, providerB);

        // The shared provider also serves ONE access per workspace root across
        // sessions: a session over the same root gets the same ledger/frames.
        Assert.Same(providerA.ForWorkspace(dirA.FullName)!, providerB.ForWorkspace(dirA.FullName)!);
        Assert.NotSame(providerA.ForWorkspace(dirA.FullName)!, providerA.ForWorkspace(dirB.FullName)!);
      }
      finally
      {
        Directory.Delete(dirA.FullName, recursive: true);
        Directory.Delete(dirB.FullName, recursive: true);
      }
    }
    finally
    {
      TryDelete(dbPath);
    }
  }

  private static void TryDelete(string path)
  {
    try
    {
      File.Delete(path);
    }
    catch (IOException)
    {
      // the pooled sqlite connection may still hold the file; temp cleanup is best effort
    }
  }
}
