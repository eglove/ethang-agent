namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #313 D7: the role vocabulary is exactly implement | review, and a persisted
///     role decodes strictly - a corrupt column is reported, never read as an implementer.
///     <para>A ROLE CARRIES NO MODEL: a step's session runs on the workspace's model choice,
///     so there is nothing about a model to declare here.</para></summary>
public class ChainStepRoleTests
{
  [Fact]
  public void Implement_IsTheDefault()
  {
    Assert.Equal(ChainStepRoleKind.Implement, ChainStepRole.Implement.Kind);
    Assert.False(ChainStepRole.Implement.IsReview);
  }

  [Fact]
  public void Review_IsAReview()
  {
    Assert.Equal(ChainStepRoleKind.Review, ChainStepRole.Review.Kind);
    Assert.True(ChainStepRole.Review.IsReview);
  }

  [Fact]
  public void Encoded_IsTheEnumMemberName()
  {
    Assert.Equal("Implement", ChainStepRole.Implement.Encoded);
    Assert.Equal("Review", ChainStepRole.Review.Encoded);
  }

  [Theory]
  [InlineData("Implement", ChainStepRoleKind.Implement)]
  [InlineData("Review", ChainStepRoleKind.Review)]
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
  [InlineData("2")]
  [InlineData("Nonsense")]
  public void TryDecode_RefusesAnythingButADefinedMember(string? encoded)
  {
    // '2' is the case Enum.TryParse alone would accept: it yields an UNDEFINED member, which
    // would smuggle a value outside the enum into the domain.
    Assert.False(ChainStepRole.TryDecode(encoded, out ChainStepRole? role));
    Assert.Null(role);
  }

  [Fact]
  public void Pending_CarriesTheAssignedRole()
  {
    ChainStep step = ChainStep.Pending(2, ChainStepRole.Review);

    Assert.Equal(2, step.Position);
    Assert.Equal(ChainStepStatus.Pending, step.Status);
    Assert.Equal(ChainStepRoleKind.Review, step.Role.Kind);
  }

  [Fact]
  public void Pending_DefaultsToTheImplementerRole()
      => Assert.Equal(ChainStepRole.Implement, ChainStep.Pending(1).Role);
}
