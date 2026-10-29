namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #313 D7: the role vocabulary is exactly implement | review, and a persisted
///     role decodes strictly - a corrupt column is reported, never read as an implementer.</summary>
public class ChainStepRoleTests
{
  [Fact]
  public void Implement_IsTheDefault_AndCarriesNoModel()
  {
    Assert.Equal(ChainStepRoleKind.Implement, ChainStepRole.Implement.Kind);
    Assert.Null(ChainStepRole.Implement.Model);
    Assert.False(ChainStepRole.Implement.IsReview);
  }

  [Fact]
  public void Review_CarriesTheReviewerModel_Trimmed()
  {
    ChainStepRole role = ChainStepRole.Review("  vendor/reviewer  ");

    Assert.Equal(ChainStepRoleKind.Review, role.Kind);
    Assert.Equal("vendor/reviewer", role.Model);
    Assert.True(role.IsReview);
  }

  [Fact]
  public void Review_WithNoModel_IsAllowed_AtCreation()
  {
    // #218 refuses a review step with no reviewer model AT SESSION START, where the
    // implementer's model is known and separation can be evaluated. Creation records the
    // declaration; it cannot check separation against a model it has not resolved.
    Assert.Null(ChainStepRole.Review().Model);
    Assert.Null(ChainStepRole.Review("   ").Model);
  }

  [Fact]
  public void Encoded_IsTheEnumMemberName()
  {
    Assert.Equal("Implement", ChainStepRole.Implement.Encoded);
    Assert.Equal("Review", ChainStepRole.Review("m").Encoded);
  }

  [Theory]
  [InlineData("Implement", null, ChainStepRoleKind.Implement)]
  [InlineData("Review", "vendor/model", ChainStepRoleKind.Review)]
  [InlineData("Review", null, ChainStepRoleKind.Review)]
  public void TryDecode_RoundTripsEveryStoredShape(string encoded, string? model,
      ChainStepRoleKind expected)
  {
    Assert.True(ChainStepRole.TryDecode(encoded, model, out ChainStepRole? role));
    Assert.Equal(expected, role.Kind);
    Assert.Equal(model, role.Model);
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
    Assert.False(ChainStepRole.TryDecode(encoded, null, out ChainStepRole? role));
    Assert.Null(role);
  }

  [Fact]
  public void Pending_CarriesTheAssignedRole()
  {
    ChainStep step = ChainStep.Pending(2, ChainStepRole.Review("vendor/reviewer"));

    Assert.Equal(2, step.Position);
    Assert.Equal(ChainStepStatus.Pending, step.Status);
    Assert.Equal(ChainStepRoleKind.Review, step.Role.Kind);
    Assert.Equal("vendor/reviewer", step.Role.Model);
  }

  [Fact]
  public void Pending_DefaultsToTheImplementerRole()
      => Assert.Equal(ChainStepRole.Implement, ChainStep.Pending(1).Role);
}
