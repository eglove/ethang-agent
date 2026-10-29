using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>Issue #317 D7: a chain whose workspace directory is gone renders as ORPHANED rather
///     than vanishing.
///     <para>The rule that matters most here is the NEGATIVE one: a chain whose workspace MOVED
///     is not orphaned. D5 chose a marker-file identity precisely so a move is recoverable, and a
///     detector that reported every moved workspace as data loss would undo that decision while
///     appearing to implement this one.</para></summary>
public class ChainOrphanPolicyTests
{
  private const string LiveRoot = "C:\\work\\live";
  private const string GoneRoot = "C:\\work\\gone";

  private static ChainWorkspaceCandidate Candidate(int id, string? root, string workspaceId = "ws-a")
      => new(id, workspaceId, root, $"Chain {id}", ChainStatus.Running);

  [Fact]
  public void Detect_WithALiveWorkspace_ReportsNothing()
  {
    IReadOnlyList<OrphanedChain> orphaned = ChainOrphanPolicy.Detect(
        [Candidate(1, LiveRoot)], root => root == LiveRoot);

    Assert.Empty(orphaned);
  }

  [Fact]
  public void Detect_WithAVanishedWorkspace_ReportsItOrphaned()
  {
    IReadOnlyList<OrphanedChain> orphaned = ChainOrphanPolicy.Detect(
        [Candidate(7, GoneRoot)], _ => false);

    OrphanedChain chain = Assert.Single(orphaned);
    Assert.Equal(7, chain.ChainId);
    Assert.Equal(GoneRoot, chain.LastKnownRoot);
    Assert.Contains("ORPHANED", chain.Render(), StringComparison.Ordinal);
    Assert.Contains(GoneRoot, chain.Render(), StringComparison.Ordinal);
  }

  [Fact]
  public void Detect_WithNoIdentityRow_ReportsItOrphanedNamingThatTheLocationIsUnknown()
  {
    // The strongest orphan: nothing binds the workspace at all. It must still be REPORTED -
    // hiding it would be exactly the vanishing D7 forbids.
    IReadOnlyList<OrphanedChain> orphaned = ChainOrphanPolicy.Detect(
        [Candidate(3, root: null)], _ => true);

    OrphanedChain chain = Assert.Single(orphaned);
    Assert.Null(chain.LastKnownRoot);
    Assert.Contains("location unknown", chain.Render(), StringComparison.Ordinal);
  }

  [Fact]
  public void Detect_ProbesEachDistinctRootOnce_NotOncePerChain()
  {
    // A workspace holds many chains; a per-chain stat would turn one rail refresh into hundreds
    // of file-system calls.
    int probes = 0;
    _ = ChainOrphanPolicy.Detect(
        [Candidate(1, LiveRoot), Candidate(2, LiveRoot), Candidate(3, LiveRoot)],
        _ =>
        {
          probes++;
          return true;
        });

    Assert.Equal(1, probes);
  }

  [Fact]
  public void Detect_KeepsTheInputOrder()
  {
    IReadOnlyList<OrphanedChain> orphaned = ChainOrphanPolicy.Detect(
        [Candidate(9, GoneRoot), Candidate(4, LiveRoot), Candidate(2, GoneRoot)],
        root => root == LiveRoot);

    Assert.Equal([9, 2], orphaned.Select(c => c.ChainId));
  }

  [Fact]
  public void Render_NamesTheLastKnownRoot_SoAUserCanFindWhatTheyLost()
  {
    OrphanedChain chain = new(12, "ws-a", GoneRoot, "Ship the chain record", ChainStatus.Parked);

    Assert.Equal("#12 Ship the chain record - ORPHANED (workspace was C:\\work\\gone)", chain.Render());
  }
}
