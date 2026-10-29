using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

/// <summary>Issue #317 D5/D6: the move-versus-copy decision, pinned as a pure function.
///     <para>These are the tests that make the two decisions checkable rather than asserted in a
///     comment. D5 says a moved workspace is RECOVERABLE (the id survives, so every plan, chain,
///     and state key follows it); D6 says a COPIED workspace gets a FRESH identity rather than
///     sharing the original's records. The whole difference between them is whether the bound
///     path still exists, which is exactly what these pin.</para></summary>
public class WorkspaceIdentityTests
{
  private const string OldRoot = "C:\\work\\repo";
  private const string NewRoot = "C:\\work\\repo2";
  private const string MintedId = "ws-minted";

  private static Func<string> Mint() => () => MintedId;

  [Fact]
  public void Decide_WithNoMarker_MintsAFreshIdentity()
  {
    WorkspaceIdentityResolution result = WorkspaceIdentity.Decide(
        markerId: null, NewRoot, binding: null, boundRootExists: false, Mint());

    Assert.Equal(MintedId, result.Id);
    Assert.Equal(WorkspaceIdentityOutcome.Bound, result.Outcome);
    Assert.Null(result.Notice);
    Assert.False(result.IsNoteworthy);
  }

  [Fact]
  public void Decide_MarkerBoundToTheSamePath_KeepsTheIdentitySilently()
  {
    WorkspaceIdentityResolution result = WorkspaceIdentity.Decide(
        "ws-a", OldRoot, new WorkspaceIdentityBinding("ws-a", OldRoot), boundRootExists: true, Mint());

    Assert.Equal("ws-a", result.Id);
    Assert.Equal(WorkspaceIdentityOutcome.Bound, result.Outcome);
    Assert.Null(result.Notice);
  }

  [Fact]
  public void Decide_MarkerBoundToAVanishedPath_IsAMove_AndKeepsTheIdentity()
  {
    // D5: the bound path is GONE, so this directory is that workspace at a new location.
    WorkspaceIdentityResolution result = WorkspaceIdentity.Decide(
        "ws-a", NewRoot, new WorkspaceIdentityBinding("ws-a", OldRoot), boundRootExists: false, Mint());

    Assert.Equal("ws-a", result.Id);
    Assert.Equal(WorkspaceIdentityOutcome.Moved, result.Outcome);
    Assert.NotNull(result.Notice);
    Assert.Contains(OldRoot, result.Notice, StringComparison.Ordinal);
    Assert.True(result.IsNoteworthy);
  }

  [Fact]
  public void Decide_MarkerBoundToAPathThatStillExists_IsACopy_AndMintsAFreshIdentity()
  {
    // D6: the bound path is STILL THERE, so this directory is a copy of it. Two directories must
    // never share one record set.
    WorkspaceIdentityResolution result = WorkspaceIdentity.Decide(
        "ws-a", NewRoot, new WorkspaceIdentityBinding("ws-a", OldRoot), boundRootExists: true, Mint());

    Assert.Equal(MintedId, result.Id);
    Assert.Equal(WorkspaceIdentityOutcome.Copied, result.Outcome);
    Assert.NotNull(result.Notice);
    Assert.Contains(OldRoot, result.Notice, StringComparison.Ordinal);
    Assert.True(result.IsNoteworthy);
  }

  [Fact]
  public void Decide_MarkerNamingAnUnboundIdentity_AdoptsIt()
  {
    // The row was lost (a pruned database, a hand-copied marker). The DIRECTORY is the authority
    // on which identity it is, so the marker wins over minting a new one.
    WorkspaceIdentityResolution result = WorkspaceIdentity.Decide(
        "ws-a", NewRoot, binding: null, boundRootExists: false, Mint());

    Assert.Equal("ws-a", result.Id);
    Assert.Equal(WorkspaceIdentityOutcome.Bound, result.Outcome);
  }

  [Theory]
  [InlineData("C:\\work\\repo", "C:\\work\\repo")]
  [InlineData("C:\\work\\repo\\", "C:\\work\\repo")]
  [InlineData("C:\\WORK\\Repo", "c:\\work\\repo")]
  public void PathsEqual_IgnoresCaseAndTrailingSeparators(string left, string right)
      => Assert.True(WorkspaceIdentity.PathsEqual(left, right));

  [Fact]
  public void PathsEqual_DistinguishesSiblingsThatShareAPrefix()
      => Assert.False(WorkspaceIdentity.PathsEqual("C:\\work\\repo", "C:\\work\\repo2"));

  [Fact]
  public void Normalize_OfBlankInput_IsEmpty()
      => Assert.Equal(string.Empty, WorkspaceIdentity.Normalize("   "));
}
