#pragma warning disable JSON002 // Probable JSON string detected

using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>agent.launch (issue #338): the model-facing act that starts a SIBLING agent.
///     <para>IT RETURNS AN ID AND RETURNS. Nothing here awaits the new agent, holds its
///     outcome, or names a parent, which is what separates starting an agent from delegating
///     to one. These tests pin the output contract, the strict argument gate, the typed
///     failures, and - structurally - that the port is never asked for anything but a prompt,
///     a label, and a model.</para></summary>
public class AgentLaunchCapabilityTests
{
  private sealed class NoQueries : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");
  }

  /// <summary>Records what it was asked to start and answers with an id, so a test reads the
  ///     REQUEST rather than trusting the rendering.</summary>
  private sealed class FakeLauncher(AgentId? id = null, DomainError? error = null) : IAgentLaunchCommand
  {
    public List<AgentLaunchRequest> Requests { get; } = [];

    public Task<Result<AgentId>> StartAsync(AgentLaunchRequest request, CancellationToken ct = default)
    {
      Requests.Add(request);
      return Task.FromResult(error is null
          ? Result.Success(id ?? AgentId.NewId())
          : Result.Failure<AgentId>(error));
    }
  }

  private static AgentCapabilityProvider Make(IAgentLaunchCommand? launcher)
      => new(new AgentProviderServices(new NoQueries(), Launcher: launcher));

  private static Task<CapabilityInvocationResult> Launch(AgentCapabilityProvider provider, string json)
      => provider.InvokeAsync("launch", json, TestContext.Current.CancellationToken);

  [Fact]
  public async Task Launch_ReturnsTheNewAgentsId()
  {
    AgentId created = AgentId.NewId();
    FakeLauncher launcher = new(created);
    AgentCapabilityProvider provider = Make(launcher);

    CapabilityInvocationResult result = await Launch(provider,
        """{"prompt":"audit the retry path"}""");

    Assert.False(result.IsError, result.Content);
    Assert.Equal($"id={created} status=running", result.Content);
  }

  /// <summary>THE REQUEST NAMES NOTHING BUT THE WORK, THE LABEL, AND THE MODEL. A launch
  ///     request that grew a parent, an owner, or a depth would fail here first.</summary>
  [Fact]
  public async Task Launch_CarriesPromptLabelAndModel_AndNothingElse()
  {
    FakeLauncher launcher = new();
    AgentCapabilityProvider provider = Make(launcher);

    _ = await Launch(provider,
        "{\"prompt\":\"review the diff\",\"label\":\"reviewer\",\"model\":\"prov/m\"}");

    AgentLaunchRequest sent = Assert.Single(launcher.Requests);
    Assert.Equal("review the diff", sent.Prompt);
    Assert.Equal("reviewer", sent.Label);
    Assert.Equal("prov/m", sent.Model);
    string[] members = [.. typeof(AgentLaunchRequest).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)];
    Assert.Equal(["IsLaunchable", "Label", "Model", "Prompt"], members);
  }

  [Fact]
  public async Task Launch_OmittingLabelAndModel_SendsNulls()
  {
    FakeLauncher launcher = new();
    AgentCapabilityProvider provider = Make(launcher);

    _ = await Launch(provider, "{\"prompt\":\"go\"}");

    AgentLaunchRequest sent = Assert.Single(launcher.Requests);
    Assert.Null(sent.Label);
    Assert.Null(sent.Model);
  }

  [Theory]
  [InlineData("""{}""")]
  [InlineData("""{"prompt":""}""")]
  [InlineData("""{"prompt":"   "}""")]
  [InlineData("""{"prompt":1}""")]
  public async Task Launch_WithMissingOrEmptyPrompt_FailsInvalidActionInput_AndStartsNothing(string json)
  {
    FakeLauncher launcher = new();
    AgentCapabilityProvider provider = Make(launcher);

    CapabilityInvocationResult result = await Launch(provider, json);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(launcher.Requests);
  }

  [Fact]
  public async Task Launch_WithAnUnknownMember_IsRefusedByName_AndStartsNothing()
  {
    FakeLauncher launcher = new();
    AgentCapabilityProvider provider = Make(launcher);

    CapabilityInvocationResult result = await Launch(provider,
        """{"prompt":"go","parentId":"x"}""");

    Assert.True(result.IsError);
    Assert.Contains("parentId", result.Content, StringComparison.Ordinal);
    Assert.Empty(launcher.Requests);
  }

  /// <summary>An unreachable launch fails with the CHANNEL's own typed code, immediately -
  ///     the acceptance rule that it never hangs.</summary>
  [Fact]
  public async Task Launch_Unreachable_FailsWithThePortsTypedCode()
  {
    FakeLauncher launcher = new(error: new DomainError("SessionRequestUnreachable",
        "no live turn is connected to carry the session-request to the app."));
    AgentCapabilityProvider provider = Make(launcher);

    CapabilityInvocationResult result = await Launch(provider, """{"prompt":"go"}""");

    Assert.True(result.IsError);
    Assert.Equal("Error [SessionRequestUnreachable]: no live turn is connected to carry the session-request to the app.",
        result.Content);
  }

  /// <summary>A host with no launch seam says so: reporting a launch that never happened is
  ///     the failure this branch prevents.</summary>
  [Fact]
  public async Task Launch_WithNoPortWired_FailsLaunchUnavailable()
  {
    CapabilityInvocationResult result = await Launch(Make(launcher: null), """{"prompt":"go"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [LaunchUnavailable]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Launch_IsADeclaredAction_WithAPromptRequired()
  {
    ActionDescriptor launch = Assert.Single(Make(new FakeLauncher()).Actions,
        a => a.Name == "launch");

    Assert.Equal(["prompt"], launch.RequiredParameters);
    Assert.Contains("SIBLING", launch.Description, StringComparison.Ordinal);
    // Issue #338 F3: the output contract states a status the action actually renders. A launch
    // creates a session and its host; it touches no admission gate, so there is no queued state
    // for this action to reach, and the first line below is the only thing it can print.
    Assert.Contains("id=<id> status=running", launch.Description, StringComparison.Ordinal);
    Assert.DoesNotContain("queued", launch.Description, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("HOST CAPACITY", launch.Description, StringComparison.Ordinal);
    // The descriptor must never teach delegation: the retired vocabulary stays out of it.
    foreach (string retired in new[] { "parent", "subordinate agent", "delegate" })
    {
      Assert.DoesNotContain("you may " + retired, launch.Description, StringComparison.OrdinalIgnoreCase);
    }
  }
}
