using System.Globalization;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed archive for oversized tool results (the store-and-read-back
///     context policy's storage side), over the V15 <c>tool_output_archive</c> table.
///     Rows are keyed by (workspace, handle); the handle is the byte-stable
///     <c>arch:</c> + SHA-256-prefix form the Tool Domain's format contract derives,
///     so identical content dedupes to one row and a handle read back always resolves
///     the same bytes. An FTS5 index (kept in sync by migration triggers) keeps
///     archived content lexically searchable after it left the context. Expected
///     failures flow as DomainErrors through Result; storage faults surface as
///     StorageUnavailable, never an exception.</summary>
public sealed class SqliteToolOutputArchive(AppDatabase database, string workspaceId) : IToolOutputArchive
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly string _workspaceId = string.IsNullOrWhiteSpace(workspaceId)
      ? throw new ArgumentException("Workspace id must be a non-empty string.", nameof(workspaceId))
      : workspaceId;

  public async Task<Result<string>> ArchiveAsync(string content, CancellationToken ct = default)
  {
    if (string.IsNullOrEmpty(content))
    {
      return Result.Failure<string>(new DomainError("InvalidParameterValue",
          "Archived content must be non-empty."));
    }

    string handle = ToolOutputArchiveFormat.HandleOf(content);
    string sha256 = Sha256Hex(content);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand insert = connection.CreateCommand();
      insert.CommandText = """
            INSERT INTO tool_output_archive (workspace_id, handle, sha256, content, total_chars, line_count, archived_at)
            VALUES (@ws, @handle, @sha256, @content, @totalChars, @lineCount, @archivedAt)
            ON CONFLICT (workspace_id, handle) DO NOTHING;
            """;
      Add(insert, "@ws", _workspaceId);
      Add(insert, "@handle", handle);
      Add(insert, "@sha256", sha256);
      Add(insert, "@content", content);
      Add(insert, "@totalChars", content.Length);
      Add(insert, "@lineCount", CountLines(content));
      Add(insert, "@archivedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
      _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      return Result.Success(handle);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<string>(Unavailable(ex));
    }
  }

  public string Excerpt(string content, int headChars, int tailChars)
  {
    ArgumentNullException.ThrowIfNull(content);
    ArgumentOutOfRangeException.ThrowIfNegative(headChars);
    ArgumentOutOfRangeException.ThrowIfNegative(tailChars);
    if (ToolOutputArchiveFormat.FitsWithin(content, headChars, tailChars))
    {
      return content;
    }

    int omitted = content.Length - headChars - tailChars;
    string marker = ToolOutputArchiveFormat.MarkerLine(ToolOutputArchiveFormat.HandleOf(content), omitted);
    string body = ToolOutputArchiveFormat.ExcerptBody(content, headChars, tailChars);
    return marker + "\n" + body;
  }

  public async Task<Result<ArchivePage>> ReadBackAsync(string handle, int offset, int maxChars,
      CancellationToken ct = default)
  {
    if (!ToolOutputArchiveFormat.IsWellFormedHandle(handle))
    {
      return Result.Failure<ArchivePage>(new DomainError("InvalidParameterValue",
          "'handle' must be an arch: handle (arch: plus 16 hex characters)."));
    }

    if (offset < 0 || maxChars < 1)
    {
      return Result.Failure<ArchivePage>(new DomainError("InvalidParameterValue",
          offset < 0 ? "'offset' must be non-negative." : "'maxChars' must be at least 1."));
    }

    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
            SELECT content, total_chars FROM tool_output_archive
            WHERE workspace_id = @ws AND handle = @handle;
            """;
      Add(command, "@ws", _workspaceId);
      Add(command, "@handle", handle);
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      if (!await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        return Result.Failure<ArchivePage>(new DomainError("ArchiveNotFound",
            $"No archived tool output with handle '{handle}' is recorded in this workspace."));
      }

      string content = reader.GetString(0);
      long total = reader.GetInt64(1);
      if (offset >= content.Length)
      {
        return Result.Failure<ArchivePage>(new DomainError("InvalidParameterValue",
            $"'offset' {offset} is at or beyond the archived length ({total} chars)."));
      }

      int take = Math.Min(maxChars, content.Length - offset);
      string text = content.Substring(offset, take);
      (int startLine, int endLine) = LineRange(content, offset, take);
      return Result.Success(new ArchivePage(handle, total, offset, text, startLine, endLine,
          offset + take < content.Length));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<ArchivePage>(Unavailable(ex));
    }
  }

  /// <summary>Global 1-based line range covering the page: the line of the first
  ///     character and the line of the last. Newline-normalized counting (CRLF is one
  ///     break) matches the read-back tool's gutter rendering.</summary>
  private static (int StartLine, int EndLine) LineRange(string content, int offset, int take)
  {
    int startLine = CountLines(content[..offset]);
    int endLine = CountLines(content[..(offset + take)]);
    return (Math.Max(1, startLine), Math.Max(1, endLine));
  }

  private static int CountLines(string text)
  {
    if (text.Length == 0)
    {
      return 1;
    }

    int lines = 1;
    for (int i = 0; i < text.Length; i++)
    {
      char c = text[i];
      // A newline break is LF, or a CR not followed by LF (CRLF counts once).
      if (c == '\n' || (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
      {
        lines++;
      }
    }

    return lines;
  }

  private static string Sha256Hex(string content)
  {
    byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
    // Named decision (CA1308): lowercase hex matches the handle contract's shape.
#pragma warning disable CA1308 // Normalize strings to uppercase
    return Convert.ToHexString(hash).ToLowerInvariant();
#pragma warning restore CA1308 // Normalize strings to uppercase
  }

  private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);

  private static DomainError Unavailable(SqliteException ex) => new("StorageUnavailable", ex.Message);
}
