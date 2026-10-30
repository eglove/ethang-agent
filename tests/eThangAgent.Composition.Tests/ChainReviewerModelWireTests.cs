using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #218: the reviewer's model SURVIVES the launch path.
///     <para>THE SEPARATION RULE IS ONLY TRUE IF THE SESSION ACTUALLY RUNS ON THE REVIEWER'S
///     MODEL. Recording the id on the chain step and comparing it there is half the mechanism;
///     the other half is that it reaches the app, which is where a session's model is set. A test
///     on the policy alone would pass while every reviewer served on the implementer's model -
///     exactly the condition the rule exists to remove.</para>
///     <para>THE REAL PORT AND THE REAL CHANNEL, so this pins the translation rather than
///     restating the payload shape: the emitter captures what actually goes on the wire.</para>
///     </summary>
public sealed class ChainReviewerModelWireTests
{
  /// <summary>Connects a real channel, captures the emitted payload, and answers it.</summary>
  private static async Task<SessionRequestChainStep?> EmittedStepAsync(ChainStepRole role)
  {
    NewSessionRequestChannel channel = new();
    SessionRequestPayload? captured = null;
    channel.Connect(json =>
    {
      SessionRequestPayload payload = JsonSerializer.Deserialize<SessionRequestPayload>(json)!;
      captured = payload;
      channel.Complete(payload.RequestId, Result.Success("session-reviewer"));
    });

    Result<string> started = await new WireChainStartPort(channel).StartFirstStepAsync(
        new ChainStepAssignment(7, 104, 2, null).WithRole(role),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(started.IsSuccess, started.Error?.Message ?? "the port must answer");
    Assert.NotNull(captured);
    return captured.ChainStep;
  }

  [Fact]
  public async Task StartFirstStepAsync_AReviewStep_SendsTheReviewersModelBesideTheRole()
  {
    SessionRequestChainStep? step = await EmittedStepAsync(ChainStepRole.Review("vendor/reviewer"));

    Assert.NotNull(step);
    Assert.Equal("Review", step.Role);
    // WITHOUT THIS the app has no way to know which model the reviewer must run on, so the
    // session would inherit this workspace's choice - the implementer's model in a single-model
    // run.
    Assert.Equal("vendor/reviewer", step.Model);
    Assert.Equal(2, step.Position);
  }

  [Fact]
  public async Task StartFirstStepAsync_AnImplementerStep_SendsNoModel()
  {
    // B6: an implementer step keeps the workspace's model choice, so nothing is forced onto it.
    SessionRequestChainStep? step = await EmittedStepAsync(ChainStepRole.Implement);

    Assert.NotNull(step);
    Assert.Equal("Implement", step.Role);
    Assert.Null(step.Model);
  }

  [Fact]
  public void ARoundTrippedReviewStep_DecodesToTheReviewerRoleWithItsModel()
  {
    // The app decodes the wire shape into a role; that round trip is what carries the model from
    // the record to the session that serves.
    SessionRequestChainStep step = new(7, 104, 2, "Review", "vendor/reviewer");

    Assert.True(ChainStepRole.TryDecode(step.Role, step.Model, out ChainStepRole? role));
    Assert.True(role.IsReview);
    Assert.Equal("vendor/reviewer", role.Model);
  }

  [Fact]
  public void ASessionRequestChainStep_FromAnOlderHost_CarriesNoModel_AndStillParses()
  {
    // Absent means 'no reviewer model', which is the shape every implementer step sends and the
    // shape an older host emits. It must not fail to parse.
    SessionRequestChainStep step = new(7, 104, 1, "Implement");

    Assert.Null(step.Model);
    Assert.True(ChainStepRole.TryDecode(step.Role, step.Model, out ChainStepRole? role));
    Assert.False(role.IsReview);
  }
}
