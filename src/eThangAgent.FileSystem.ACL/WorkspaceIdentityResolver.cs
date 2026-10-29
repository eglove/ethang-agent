using System.Globalization;
using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

/// <summary>Resolves a directory's STABLE workspace identity (issue #317 D5/D6) by reading and
///     writing a marker file inside it, and asking the identity store what that marker means.
///     <para>THE MARKER IS THE POINT. A path-keyed identity cannot survive <c>mv repo repo2</c>,
///     so the identity has to live somewhere that moves WITH the directory - and the only thing
///     that moves with a directory is a file inside it. <c>.ethang/workspace.json</c> follows the
///     <c>.ethang/images/</c> precedent (#230): the harness's own artifacts live under
///     <c>.ethang/</c> in the workspace, so one directory carries one recognizable name.</para>
///     <para>A COPY is detected rather than prevented (D6): a copied directory carries the marker
///     too, so on open the id is already bound to a DIFFERENT path that still exists - which is
///     the signature of a copy, and the copy takes a fresh identity with a notice. A MOVE is the
///     other case: the bound path is gone, so the id re-binds to the new location and every
///     record follows it. Both decisions live in <see cref="WorkspaceIdentity.Decide"/> as a pure
///     function; this class only supplies the three facts it needs.</para>
///     <para>Every failure is a typed <see cref="DomainError"/>: an unreadable marker, a marker
///     that is not JSON, and a store fault are each NAMED, because a workspace that silently
///     opened with a fresh identity would look exactly like data loss.</para></summary>
public sealed class WorkspaceIdentityResolver(IWorkspaceIdentityStore store)
    : IWorkspaceIdentityResolver
{
  /// <summary>The marker file, workspace-relative and forward-slashed (the transcript names
  ///     paths in this form so they read the same everywhere).</summary>
  public const string MarkerDirectory = ".ethang";

  /// <summary>The marker file's name inside <see cref="MarkerDirectory"/>.</summary>
  public const string MarkerFileName = "workspace.json";

  /// <summary>The marker file's workspace-relative path.</summary>
  public const string MarkerRelativePath = ".ethang/workspace.json";

  private readonly IWorkspaceIdentityStore _store = store ?? throw new ArgumentNullException(nameof(store));

  public async Task<Result<WorkspaceIdentityResolution>> ResolveAsync(string root,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(root);
    string normalized = WorkspaceIdentity.Normalize(root);

    string markerPath = Path.Combine(normalized, MarkerDirectory, MarkerFileName);
    Result<string?> marker = ReadMarkerId(markerPath);
    if (!marker.IsSuccess)
    {
      return Result.Failure<WorkspaceIdentityResolution>(marker.Error);
    }

    // The binding is looked up by the MARKER's id when there is one, and by the PATH otherwise:
    // a directory with no marker may still be a pre-#317 workspace whose id IS its path, and
    // re-minting that would orphan every record it already has. That lookup is what makes the
    // migration's backfill visible to the resolver instead of a second identity for one folder.
    Result<WorkspaceIdentityBinding?> existing = marker.Value is { } markerId
        ? await _store.FindByIdAsync(markerId, ct).ConfigureAwait(false)
        : await _store.FindByRootAsync(normalized, ct).ConfigureAwait(false);
    if (!existing.IsSuccess)
    {
      return Result.Failure<WorkspaceIdentityResolution>(existing.Error);
    }

    // A pre-#317 directory: no marker, but its path is already bound as an id. Adopt that id
    // rather than minting - the records under it are the workspace's history.
    if (marker.Value is null && existing.Value is { } legacy)
    {
      Result<WorkspaceIdentityResolution> adopted = await WriteMarkerAsync(markerPath, legacy.Id, ct)
          .ConfigureAwait(false);
      return adopted.IsSuccess
          ? Result.Success(new WorkspaceIdentityResolution(legacy.Id, WorkspaceIdentityOutcome.Bound, null))
          : adopted;
    }

    bool boundRootExists = existing.Value is { } binding && Directory.Exists(binding.Root);
    WorkspaceIdentityResolution decision = WorkspaceIdentity.Decide(
        marker.Value, normalized, existing.Value, boundRootExists, MintId);

    Result<WorkspaceIdentityBinding> bound = await _store.BindAsync(decision.Id, normalized, ct)
        .ConfigureAwait(false);
    if (!bound.IsSuccess)
    {
      return Result.Failure<WorkspaceIdentityResolution>(bound.Error);
    }

    Result<WorkspaceIdentityResolution> written = await WriteMarkerAsync(markerPath, decision.Id, ct)
        .ConfigureAwait(false);
    return written.IsSuccess ? Result.Success(decision) : written;
  }

  /// <summary>A fresh identity. A GUID rather than a path-derived value on purpose: the id must
  ///     not leak the directory it was minted for, or a copy would look like a move to anything
  ///     that compared the two.</summary>
  private static string MintId() => "ws-" + Guid.NewGuid().ToString("N");

  /// <summary>Reads the marker's id, or null when the file is absent. An unreadable or
  ///     malformed marker is a NAMED failure: silently treating it as absent would mint a fresh
  ///     identity and orphan the workspace's records, which is the data loss this issue exists
  ///     to prevent.</summary>
  private static Result<string?> ReadMarkerId(string markerPath)
  {
    if (!File.Exists(markerPath))
    {
      return Result.Success<string?>(null);
    }

    string json;
    try
    {
      json = File.ReadAllText(markerPath);
    }
    catch (IOException ex)
    {
      return Result.Failure<string?>(new DomainError("WorkspaceMarkerUnreadable",
          $"the workspace marker '{markerPath}' could not be read: {ex.Message}"));
    }
    catch (UnauthorizedAccessException ex)
    {
      return Result.Failure<string?>(new DomainError("WorkspaceMarkerUnreadable",
          $"the workspace marker '{markerPath}' could not be read: {ex.Message}"));
    }

    try
    {
      using JsonDocument document = JsonDocument.Parse(json);
      return document.RootElement.ValueKind == JsonValueKind.Object
          && document.RootElement.TryGetProperty("workspaceId", out JsonElement id)
          && id.ValueKind == JsonValueKind.String
          && id.GetString() is { Length: > 0 } value
          ? Result.Success<string?>(value)
          : Result.Failure<string?>(new DomainError("WorkspaceMarkerInvalid",
              $"the workspace marker '{markerPath}' does not carry a non-empty 'workspaceId' string."));
    }
    catch (JsonException ex)
    {
      return Result.Failure<string?>(new DomainError("WorkspaceMarkerInvalid",
          $"the workspace marker '{markerPath}' is not valid JSON: {ex.Message}"));
    }
  }

  /// <summary>Writes the marker, creating <c>.ethang/</c> on demand (the file-write seam
  ///     deliberately never creates parents). Overwrites: the marker states the identity the
  ///     directory HAS, so a move or a copy must be able to correct it.</summary>
  private static async Task<Result<WorkspaceIdentityResolution>> WriteMarkerAsync(string markerPath,
      string id, CancellationToken ct)
  {
    string? directory = Path.GetDirectoryName(markerPath);
    if (directory is not null)
    {
      _ = Directory.CreateDirectory(directory);
    }

    string json = JsonSerializer.Serialize(new
    {
      workspaceId = id,
      boundAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    });

    try
    {
      await File.WriteAllTextAsync(markerPath, json, ct).ConfigureAwait(false);
      return Result.Success(new WorkspaceIdentityResolution(id, WorkspaceIdentityOutcome.Bound, null));
    }
    catch (IOException ex)
    {
      return Result.Failure<WorkspaceIdentityResolution>(new DomainError("WorkspaceMarkerUnwritable",
          $"the workspace marker '{markerPath}' could not be written: {ex.Message}"));
    }
    catch (UnauthorizedAccessException ex)
    {
      return Result.Failure<WorkspaceIdentityResolution>(new DomainError("WorkspaceMarkerUnwritable",
          $"the workspace marker '{markerPath}' could not be written: {ex.Message}"));
    }
  }
}
