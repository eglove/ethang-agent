namespace eThangAgent.Composition;

/// <summary>Locates the ChildHost executable under a repo root: probe the live
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
    // TFM folders: the project targets net10.0-windows (the live output shape);
    // net10.0 is the pre-TFM-bump shape that lingers on dev machines only.
    string[] tfmPreference = ["net10.0-windows", "net10.0"];
    foreach (string configuration in configurationPreference)
    {
      foreach (string tfm in tfmPreference)
      {
        string candidate = Path.Combine(repoRoot, "src", "eThangAgent.ChildHost", "bin", configuration, tfm, "eThangAgent.ChildHost.exe");
        if (File.Exists(candidate))
        {
          return candidate;
        }
      }
    }

    // No built host found: return the live-shape location so the caller's
    // not-found error names where the build actually lands.
    return Path.Combine(repoRoot, "src", "eThangAgent.ChildHost", "bin", configurationPreference[0], "net10.0-windows", "eThangAgent.ChildHost.exe");
  }
}
