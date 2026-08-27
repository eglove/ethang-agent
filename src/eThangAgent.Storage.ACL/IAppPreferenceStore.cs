namespace eThangAgent.Storage.ACL;

/// <summary>App-scoped (cross-workspace) preference storage: durable host settings that
///     belong to no single workspace, e.g. the last-selected AI provider. The interface
///     lives in this ACL alongside its SQLite implementation because app configuration
///     has no bounded context of its own yet — consumers (composition root, desktop
///     host) already reference this project, and the seam stays swappable.</summary>
public interface IAppPreferenceStore
{
  /// <summary>Returns the stored value for <paramref name="key"/>, or null when unset.</summary>
  Task<string?> GetAsync(string key, CancellationToken ct = default);

  /// <summary>Stores the value, overwriting any previous one. Returns false when the
  ///     write did not land (never throws for storage failures).</summary>
  Task<bool> SetAsync(string key, string value, CancellationToken ct = default);
}
