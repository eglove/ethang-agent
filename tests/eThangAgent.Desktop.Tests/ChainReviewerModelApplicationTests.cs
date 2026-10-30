using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #218: the reviewer's model is APPLIED to the launching session.
///     <para>THE RULE IS ONLY TRUE IF THE SESSION RUNS ON IT. Recording the reviewer's model on
///     the chain step and comparing it there is half the mechanism; without this the chain record
///     would claim a reviewer was separated while the session served on this workspace's model
///     choice - the implementer's model in a single-model run, which is the exact condition the
///     rule exists to remove.</para>
///     <para>THE ORDER IS PINNED TOO: the workspace's saved preferences are restored onto the
///     live preference BEFORE the reviewer's model is applied, so an application that ran first
///     would be silently overwritten.</para></summary>
public sealed class ChainReviewerModelApplicationTests
{
  private static AgentSessionViewModel ViewModel(SessionModelPreferences preferences) => new(
      (_, _, _, _) => Task.FromResult(Result.Success("")),
      new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
      "OpenRouter", "openrouter/auto",
      new AgentSessionViewModelOptions
      {
        WorkspaceRoot = @"C:\work\demo",
        ModelPreferences = preferences,
      });

  [Fact]
  public void AReviewRole_AppliesTheReviewersModelToTheLivePreference()
  {
    // The live preference is what the resolver reads, so this asserts the value the next turn
    // would actually serve on - not merely what the status bar shows.
    SessionModelPreferences preferences = new() { ModelId = "vendor/implementer" };
    AgentSessionViewModel vm = ViewModel(preferences);

    vm.ApplyModelChoice("vendor/reviewer", variant: null);

    Assert.Equal("vendor/reviewer", preferences.ModelId);
    Assert.Equal("vendor/reviewer", vm.Status.ModelId);
  }

  [Fact]
  public void TheAppliedReviewerModel_ClearsAnyInheritedRoutingVariant()
  {
    // The reviewer's model is a MODEL id, not a routing decision: inheriting this workspace's
    // committed variant would let a Nitro review run on a pool the record never named.
    SessionModelPreferences preferences = new()
    {
      ModelId = "vendor/implementer",
      ModelVariant = ModelVariant.Nitro,
    };
    AgentSessionViewModel vm = ViewModel(preferences);

    vm.ApplyModelChoice("vendor/reviewer", variant: null);

    Assert.Null(preferences.ModelVariant);
  }
}
