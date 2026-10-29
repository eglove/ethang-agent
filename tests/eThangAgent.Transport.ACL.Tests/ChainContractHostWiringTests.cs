using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.ChildHost;
using eThangAgent.Composition;
using eThangAgent.ModelDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The chain contract reaches the process that renders the prompt (issue #311).
///     <para>WHY THIS IS NOT THE PROVIDER TEST: the provider test proves the provider renders.
///     It cannot prove the contract ARRIVES, and every production turn is served by the
///     ChildHost — a separate process built from a settings JSON. A contract resolved app-side
///     and never shipped would leave the prompt silent in the only place it is read, and the
///     provider test would stay green.</para>
///     <para>THE SETTINGS ARE SERIALIZED FROM THE REAL <see cref="AgentSettings"/> the
///     supervisor writes, so this also pins the WIRE SHAPE: a contract the serializer could not
///     round-trip would be a contract the host never sees.</para>
///     <para>THE ASSERTION IS ON THE HOST'S OWN COMPOSED PROMPT, built through
///     <see cref="SessionHost.Create"/> over a real settings file — the same path production
///     takes, so a registration this test forgot cannot hide.</para></summary>
public sealed class ChainContractHostWiringTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  /// <summary>Writes the settings file the supervisor would write, and returns its path. The
  ///     workspace it names is a fresh temp directory so the host anchors somewhere real.</summary>
  private static string WriteSettings(ChainContract? contract)
  {
    string workspace = Directory.CreateTempSubdirectory("ethang-chain-contract-ws").FullName;
    AgentSettings settings = new AgentSettings(
        new OpenRouterSettings("sk-test", BaseUrl),
        new SubAgentOptions(null, 1),
        WorkspaceRoot: workspace).WithChainContract(contract);
    string path = Path.Combine(Path.GetTempPath(),
        "ethang-chain-contract-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(path, JsonSerializer.Serialize(settings));
    return path;
  }

  /// <summary>The host built over <paramref name="settingsPath"/>, with its own database file so
  ///     two tests never share a schema.</summary>
  private static SessionHost Host(string settingsPath) =>
      SessionHost.Create(settingsPath,
          Path.Combine(Path.GetTempPath(), "ethang-cc-" + Guid.NewGuid().ToString("N") + ".db"));

  [Fact]
  public void TheHostPrompt_CarriesTheContractShippedInTheSettings()
  {
    ChainContract contract = new(7, 104, 2, "sci.dashboard-truth/ledger", ["dotnet build"]);
    string path = WriteSettings(contract);
    try
    {
      string prompt = Host(path).Services.GetRequiredService<ISystemPromptProvider>().Build();

      Assert.Contains(ChainContractRenderer.Prefix, prompt, StringComparison.Ordinal);
      Assert.Contains("You are step 2 of chain #7", prompt, StringComparison.Ordinal);
      Assert.Contains("sci.dashboard-truth/ledger", prompt, StringComparison.Ordinal);
      Assert.Contains("    dotnet build", prompt, StringComparison.Ordinal);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public void TheHostPrompt_WithoutAContract_SaysNothingAboutAChain()
  {
    // The ordinary session: a user opened a workspace and typed. Its prompt must not mention a
    // chain at all — this is the host-side half of the byte-identical criterion.
    string path = WriteSettings(contract: null);
    try
    {
      string prompt = Host(path).Services.GetRequiredService<ISystemPromptProvider>().Build();

      Assert.DoesNotContain(ChainContractRenderer.Prefix, prompt, StringComparison.Ordinal);
      Assert.DoesNotContain("executes one step of a chain", prompt, StringComparison.Ordinal);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public void TheContract_RoundTripsThroughTheSettingsJson()
  {
    // The wire shape, pinned on its own: the host reads the contract back out of the file the
    // supervisor wrote, so a member the serializer dropped would be a fact the host never had.
    ChainContract contract = new(7, 104, 2, "sci.dashboard-truth/ledger", ["dotnet build"]);
    AgentSettings settings = new AgentSettings(
        new OpenRouterSettings("sk-test", BaseUrl), new SubAgentOptions(null, 1))
        .WithChainContract(contract);

    ChainContract? roundTripped =
        JsonSerializer.Deserialize<AgentSettings>(JsonSerializer.Serialize(settings))?.ChainContract;

    Assert.NotNull(roundTripped);
    Assert.Equal(contract.ChainId, roundTripped.ChainId);
    Assert.Equal(contract.PlanId, roundTripped.PlanId);
    Assert.Equal(contract.Position, roundTripped.Position);
    Assert.Equal(contract.LedgerKey, roundTripped.LedgerKey, StringComparer.Ordinal);
    Assert.Equal(contract.Gates, roundTripped.Gates);
  }
}
