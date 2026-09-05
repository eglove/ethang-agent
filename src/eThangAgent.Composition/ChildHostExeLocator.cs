namespace eThangAgent.Composition;

/// <summary>Locates the ChildHost executable under a repo root: prefer the Debug build
///     output, fall back to Release. Configuration-agnostic resolution matters because
///     the caller's own build configuration does not determine which configuration the
///     host project was last built in — CI builds Release only, dev machines usually
///     Debug. A Debug-only probe returned a path that does not exist and every
///     remote-host session failed at spawn (run 33987882753).</summary>
public static class ChildHostExeLocator
{
  private static readonly string[] ConfigurationPreference = ["Debug", "Release"];

  public static string ResolveFromRepoRoot(string repoRoot) =>
      ResolveFromRepoRoot(repoRoot, ConfigurationPreference);

  /// <summary>Core probe with the configuration order injected for tests.</summary>
  public static string ResolveFromRepoRoot(string repoRoot, IReadOnlyList<string> configurationPreference)
  {
    ArgumentNullException.ThrowIfNull(configurationPreference);
    foreach (string configuration in configurationPreference)
    {
      string candidate = Path.Combine(repoRoot, "src", "eThangAgent.ChildHost", "bin", configuration, "net10.0", "eThangAgent.ChildHost.exe");
      if (File.Exists(candidate))
      {
        return candidate;
      }
    }

    // No built host found: return the first preference so the caller's
    // not-found error names the conventional location.
    return Path.Combine(repoRoot, "src", "eThangAgent.ChildHost", "bin", configurationPreference[0], "net10.0", "eThangAgent.ChildHost.exe");
  }
}
