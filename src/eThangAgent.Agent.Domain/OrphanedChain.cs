using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>One chain as the ORPHAN check sees it: the chain's identifying facts plus the
///     directory its workspace is currently bound to (issue #317 D7).
///     <para><paramref name="WorkspaceRoot"/> is null when the identity row itself is gone -
///     a chain whose workspace nothing can locate. That is the strongest form of orphan and is
///     reported rather than hidden, because a chain is the record of work that happened.</para>
///     <para>This is a READ model, not a chain: it deliberately carries no steps. The rail
///     renders an orphaned chain as a marker naming where it lived, and opening it is
///     impossible - there is no workspace to open.</para></summary>
/// <param name="ChainId">The chain's id.</param>
/// <param name="WorkspaceId">The stable workspace identity the chain is scoped to.</param>
/// <param name="WorkspaceRoot">The directory that identity was last bound to, or null when the
///     binding itself is gone.</param>
/// <param name="Title">The chain's title, so a user recognizes what was lost.</param>
/// <param name="Status">The chain's status when it was last written.</param>
public sealed record ChainWorkspaceCandidate(int ChainId, string WorkspaceId, string? WorkspaceRoot,
    string Title, ChainStatus Status);

/// <summary>A chain whose workspace directory is GONE (issue #317 D7), rendered as ORPHANED
///     rather than vanishing. The Sessions catalog already greys out entries whose workspace is
///     missing; chains behave the same way, so a user can see what they lost and where it was.
///     <para><paramref name="LastKnownRoot"/> is the directory the workspace was last bound to,
///     or null when even that is unknown - the message says which, because 'it was at C:\work'
///     and 'nothing remembers where it was' are different facts.</para></summary>
public sealed record OrphanedChain(int ChainId, string WorkspaceId, string? LastKnownRoot,
    string Title, ChainStatus Status)
{
  /// <summary>The one line a rail renders. Names the last known location when there is one, and
  ///     says plainly when there is not - a blank where a path belongs reads as a rendering bug.
  ///     </summary>
  public string Render() => LastKnownRoot is { } root
      ? $"#{ChainId} {Title} - ORPHANED (workspace was {root})"
      : $"#{ChainId} {Title} - ORPHANED (workspace location unknown)";
}

/// <summary>Cross-workspace chain read for the orphan check (issue #317 D7).
///     <para><see cref="IChainStore"/> is deliberately bound to ONE workspace - that is what
///     makes another workspace's chains invisible rather than leaked - so it structurally cannot
///     answer 'which chains lost their workspace'. This port exists for exactly that question and
///     is READ-ONLY: nothing here mutates a chain, and the orphan check never does.</para></summary>
public interface IChainWorkspaceQuery
{
  /// <summary>Every chain in the database with the root its workspace is bound to, across ALL
  ///     workspaces. A chain whose identity row is missing reports a null root.</summary>
  Task<Result<IReadOnlyList<ChainWorkspaceCandidate>>> ListCandidatesAsync(CancellationToken ct = default);
}
