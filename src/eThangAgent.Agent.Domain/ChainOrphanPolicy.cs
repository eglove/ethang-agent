using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Decides which chains have LOST their workspace (issue #317 D7), as a PURE function
///     over the candidates and a directory-existence probe.
///     <para>THE RULE IS ONE LINE AND THAT IS THE POINT: a chain is orphaned when the directory
///     its workspace is bound to no longer exists, or when nothing binds it at all. A chain whose
///     workspace MOVED is NOT orphaned - the identity survived the move and the chain followed it,
///     which is the whole reason #317 D5 chose a marker-file identity over a path. Getting that
///     distinction wrong would report every moved workspace as data loss.</para>
///     <para>The existence probe is injected so the rule is testable without a file system, and
///     so the caller controls whether it costs a stat per distinct root (it is cached per root
///     inside <see cref="Detect"/>: a workspace holds many chains).</para></summary>
public static class ChainOrphanPolicy
{
  /// <summary>The orphaned chains among <paramref name="candidates"/>, in input order.
  ///     <paramref name="rootExists"/> answers 'is this directory still there'.</summary>
  public static IReadOnlyList<OrphanedChain> Detect(IReadOnlyList<ChainWorkspaceCandidate> candidates,
      Func<string, bool> rootExists)
  {
    ArgumentNullException.ThrowIfNull(candidates);
    ArgumentNullException.ThrowIfNull(rootExists);

    // One stat per DISTINCT root: a workspace holds many chains, and a per-chain probe would
    // turn one rail refresh into hundreds of file-system calls.
    Dictionary<string, bool> seen = new(StringComparer.OrdinalIgnoreCase);
    List<OrphanedChain> orphaned = [];
    foreach (ChainWorkspaceCandidate candidate in candidates)
    {
      if (candidate.WorkspaceRoot is { } root)
      {
        if (!seen.TryGetValue(root, out bool exists))
        {
          exists = rootExists(root);
          seen[root] = exists;
        }

        if (exists)
        {
          continue;
        }
      }

      // No binding at all, or a binding whose directory is gone: either way the chain's
      // workspace cannot be opened, which is what orphaned means.
      orphaned.Add(new OrphanedChain(candidate.ChainId, candidate.WorkspaceId,
          candidate.WorkspaceRoot, candidate.Title, candidate.Status));
    }

    return orphaned;
  }
}

/// <summary>Reads the orphan question against the database (issue #317 D7): every chain, joined
///     to the identity table, filtered to those whose workspace directory is gone.
///     <para>Deliberately UNSCOPED for the same reason the identity store is: the question spans
///     workspaces by definition. It is read-only, and it is the only chain read that crosses a
///     workspace boundary.</para></summary>
public sealed class ChainOrphanQuery(IChainWorkspaceQuery candidates)
{
  private readonly IChainWorkspaceQuery _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));

  /// <summary>The orphaned chains, newest id first. A read fault is a named failure - an orphan
  ///     check that silently reported nothing would say 'all your chains are fine' when it simply
  ///     could not look.</summary>
  public async Task<Result<IReadOnlyList<OrphanedChain>>> ListAsync(CancellationToken ct = default)
  {
    Result<IReadOnlyList<ChainWorkspaceCandidate>> read = await _candidates.ListCandidatesAsync(ct)
        .ConfigureAwait(false);
    return read.IsSuccess
        ? Result.Success(ChainOrphanPolicy.Detect(read.Value, Directory.Exists))
        : Result.Failure<IReadOnlyList<OrphanedChain>>(read.Error);
  }
}
