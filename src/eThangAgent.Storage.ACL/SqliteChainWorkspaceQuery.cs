using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>Cross-workspace chain read for the orphan check (issue #317 D7).
///     <para>Deliberately UNSCOPED, like the identity store it joins: the question 'which chains
///     lost their workspace' spans workspaces by definition, so binding this to one workspace
///     would make it unable to answer. It is READ-ONLY - the orphan check never mutates a chain,
///     which is what makes D8's 'retention never deletes an orphaned chain' enforceable by simply
///     not writing anything.</para>
///     <para>The LEFT JOIN is load-bearing: a chain whose identity row is GONE must still be
///     reported (with a null root), not silently dropped by an inner join. That chain is the
///     strongest orphan there is, and hiding it would be the vanishing D7 forbids.</para></summary>
public sealed class SqliteChainWorkspaceQuery(AppDatabase database) : IChainWorkspaceQuery
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  public async Task<Result<IReadOnlyList<ChainWorkspaceCandidate>>> ListCandidatesAsync(
      CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
          SELECT c.id, c.workspace_id, i.root, c.title, c.status
          FROM chains c
          LEFT JOIN workspace_identities i ON i.id = c.workspace_id
          ORDER BY c.id DESC;
          """;
      List<ChainWorkspaceCandidate> candidates = [];
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        string rawStatus = reader.GetString(4);
        // A status outside the enum makes this row unreadable, not orphaned: reporting it as
        // orphaned would blame a missing workspace for a corrupt column.
        if (!Enum.TryParse(rawStatus, ignoreCase: false, out ChainStatus status) || !Enum.IsDefined(status))
        {
          continue;
        }

        candidates.Add(new ChainWorkspaceCandidate(
            reader.GetInt32(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : reader.GetString(2),
            reader.GetString(3),
            status));
      }

      return Result.Success<IReadOnlyList<ChainWorkspaceCandidate>>(candidates);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<IReadOnlyList<ChainWorkspaceCandidate>>(new DomainError("StorageUnavailable",
          $"the chain orphan check could not read the database: {ex.Message}"));
    }
  }
}
