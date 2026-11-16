namespace eThangAgent.AgentDomain.Tests;

/// <summary>The role vocabulary is exactly implement | review | refine, and a persisted
///     role decodes strictly - a corrupt column is reported, never read as an implementer.
///     <para>THE TWO NON-IMPLEMENTATION ROLES DO DIFFERENT JOBS: refine makes a plan executable,
///     review judges implementation and returns a pass or fail. They are separate members because
///     a plan and built work are separate artifacts.</para>
///     <para>A ROLE CARRIES NO MODEL: a step's session runs on the workspace's model choice,
///     so there is nothing about a model to declare here.</para></summary>
public class ChainStepRoleTests
{
  [Fact]
  public void Implement_IsTheDefault()
  {
    Assert.Equal(ChainStepRoleKind.Implement, ChainStepRole.Implement.Kind);
    Assert.False(ChainStepRole.Implement.IsRefine);
  }

  [Fact]
  public void Refine_IsARefineStep()
  {
    Assert.Equal(ChainStepRoleKind.Refine, ChainStepRole.Refine.Kind);
    Assert.True(ChainStepRole.Refine.IsRefine);
    Assert.False(ChainStepRole.Refine.IsReview);
  }

  [Fact]
  public void Review_IsAReviewStep()
  {
    Assert.Equal(ChainStepRoleKind.Review, ChainStepRole.Review.Kind);
    Assert.True(ChainStepRole.Review.IsReview);
    Assert.False(ChainStepRole.Review.IsRefine);
  }

  [Fact]
  public void Encoded_IsTheEnumMemberName()
  {
    Assert.Equal("Implement", ChainStepRole.Implement.Encoded);
    Assert.Equal("Review", ChainStepRole.Review.Encoded);
    Assert.Equal("Refine", ChainStepRole.Refine.Encoded);
  }

  [Theory]
  [InlineData("Implement", ChainStepRoleKind.Implement)]
  [InlineData("Review", ChainStepRoleKind.Review)]
  [InlineData("Refine", ChainStepRoleKind.Refine)]
  public void TryDecode_RoundTripsEveryStoredShape(string encoded, ChainStepRoleKind expected)
  {
    Assert.True(ChainStepRole.TryDecode(encoded, out ChainStepRole? role));
    Assert.Equal(expected, role.Kind);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("implement")]
  [InlineData("0")]
  [InlineData("2")]
  [InlineData("Nonsense")]
  public void TryDecode_RefusesAnythingButADefinedMember(string? encoded)
  {
    // A numeric string is the case Enum.TryParse alone would accept: '0' and '2' each name a
    // DEFINED member, so accepting them would smuggle a role nobody wrote into the domain.
    Assert.False(ChainStepRole.TryDecode(encoded, out ChainStepRole? role));
    Assert.Null(role);
  }

  [Fact]
  public void Pending_CarriesTheAssignedRole()
  {
    ChainStep step = ChainStep.Pending(2, ChainStepRole.Refine);

    Assert.Equal(2, step.Position);
    Assert.Equal(ChainStepStatus.Pending, step.Status);
    Assert.Equal(ChainStepRoleKind.Refine, step.Role.Kind);
  }

  [Fact]
  public void Pending_DefaultsToTheImplementerRole()
      => Assert.Equal(ChainStepRole.Implement, ChainStep.Pending(1).Role);
}
