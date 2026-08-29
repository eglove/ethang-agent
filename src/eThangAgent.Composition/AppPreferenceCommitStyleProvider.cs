using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Resolves the commit style from the app-scoped preference store: the
///     user's host setting, read live per call so a settings change applies to the
///     next commit of an open session. Unset resolves to the documented
///     Conventional default; a corrupt stored value fails typed — the git_commit
///     tool surfaces it instead of silently falling back. Registered over the same
///     shared <see cref="AppDatabase"/> every session container and the Desktop
///     host use, so both see the same preference row.</summary>
public sealed class AppPreferenceCommitStyleProvider(IAppPreferenceStore preferences) : ICommitStyleProvider
{
  /// <summary>App-preference key the Desktop stores the commit style under.</summary>
  public const string PreferenceKey = "commit_style";

  private readonly IAppPreferenceStore _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));

  public async Task<Result<CommitStyle>> GetAsync(CancellationToken ct = default)
  {
    string? stored = await _preferences.GetAsync(PreferenceKey, ct).ConfigureAwait(false);
    return CommitStylePreference.Resolve(stored);
  }
}
