using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>A session created FOR a chain step carries the contract into its prompt, and a
///     session created outside a chain does not (issue #311).
///     <para>WHY THE FACTORY AND NOT ONLY THE HOST: the factory is where a session's identity is
///     decided — it fills every construction-phase carrier — and it serves MANY sessions over one
///     workspace. A contract held on the factory rather than passed per call would be one
///     session's identity leaking onto every other, and only a test that creates two sessions
///     can see that.</para>
///     <para>THE PROMPT IS READ FROM THE CONTAINER'S OWN COMPOSITE, so this pins the whole path:
///     the argument, the carrier, the provider, and the composite's empty-entry drop.</para></summary>
public sealed class ChainContractSessionTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", BaseUrl),
      new SubAgentOptions(null, 2));

  private static ChainContract Contract() =>
      new(7, 104, 2, "sci.dashboard-truth/ledger", ["dotnet build"]);

  /// <summary>The prompt the session's own container composes — the same bytes the model
  ///     receives as the leading part of every request.</summary>
  private static string Prompt(AgentSession session) =>
      session.Services.GetRequiredService<ISystemPromptProvider>().Build();

  [Fact]
  public async Task AChainStepSession_CarriesTheContractInItsPrompt()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-contract-{Guid.NewGuid():N}.db");
    AgentSessionFactory factory = new(Settings(), new AppDatabase(dbPath));
    DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-contract-ws");
    try
    {
      Result<AgentSession> created = await factory.CreateAsync(dir.FullName, Providers.OpenRouter,
          conversationSeed: null, Contract(), TestContext.Current.CancellationToken);
      Assert.True(created.IsSuccess, created.Error?.Message);
      try
      {
        string prompt = Prompt(created.Value);

        Assert.Contains(ChainContractRenderer.Prefix, prompt, StringComparison.Ordinal);
        Assert.Contains("You are step 2 of chain #7", prompt, StringComparison.Ordinal);
        Assert.Contains("sci.dashboard-truth/ledger", prompt, StringComparison.Ordinal);
        Assert.Contains("    dotnet build", prompt, StringComparison.Ordinal);
      }
      finally
      {
        await created.Value.Services.DisposeAsync();
      }
    }
    finally
    {
      dir.Delete(true);
      TryDelete(dbPath);
    }
  }

  [Fact]
  public async Task ASessionOutsideAChain_GetsAPromptWithNoChainSection()
  {
    // The ordinary way a user begins work. Its prompt must be the one it got before this
    // issue existed — asserted as an ABSENCE, because that is what the criterion is.
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-contract-{Guid.NewGuid():N}.db");
    AgentSessionFactory factory = new(Settings(), new AppDatabase(dbPath));
    DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-contract-ws");
    try
    {
      Result<AgentSession> created = await factory.CreateAsync(dir.FullName, Providers.OpenRouter,
          ct: TestContext.Current.CancellationToken);
      Assert.True(created.IsSuccess, created.Error?.Message);
      try
      {
        string prompt = Prompt(created.Value);

        Assert.DoesNotContain(ChainContractRenderer.Prefix, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("executes one step of a chain", prompt, StringComparison.Ordinal);
      }
      finally
      {
        await created.Value.Services.DisposeAsync();
      }
    }
    finally
    {
      dir.Delete(true);
      TryDelete(dbPath);
    }
  }

  [Fact]
  public async Task OneFactoriesSessions_DoNotShareAContract()
  {
    // The contract belongs to the SESSION, not the factory: the factory serves many sessions
    // over one workspace, so a contract held on it would make the second session claim the
    // first one's chain step.
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-contract-{Guid.NewGuid():N}.db");
    AgentSessionFactory factory = new(Settings(), new AppDatabase(dbPath));
    DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-contract-ws");
    try
    {
      Result<AgentSession> chained = await factory.CreateAsync(dir.FullName, Providers.OpenRouter,
          conversationSeed: null, Contract(), TestContext.Current.CancellationToken);
      Result<AgentSession> plain = await factory.CreateAsync(dir.FullName, Providers.OpenRouter,
          ct: TestContext.Current.CancellationToken);
      Assert.True(chained.IsSuccess, chained.Error?.Message);
      Assert.True(plain.IsSuccess, plain.Error?.Message);
      try
      {
        Assert.Contains(ChainContractRenderer.Prefix, Prompt(chained.Value), StringComparison.Ordinal);
        Assert.DoesNotContain(ChainContractRenderer.Prefix, Prompt(plain.Value), StringComparison.Ordinal);
      }
      finally
      {
        await chained.Value.Services.DisposeAsync();
        await plain.Value.Services.DisposeAsync();
      }
    }
    finally
    {
      dir.Delete(true);
      TryDelete(dbPath);
    }
  }

  /// <summary>Best-effort temp-database cleanup. A leftover file under the temp root is not a
  ///     test failure, and a delete fault must not mask the assertion that ran before it.</summary>
  private static void TryDelete(string path)
  {
    try
    {
      File.Delete(path);
    }
    // Named decision (CA1031): best-effort cleanup of a temp file.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      // Swallowed deliberately: see the summary above.
    }
  }
}
