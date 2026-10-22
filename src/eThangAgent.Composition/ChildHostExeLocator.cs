namespace eThangAgent.Composition;

/// <summary>Locates the ChildHost executable in EITHER layout: under a repo root
///     (the dev layout: probe the live output, fall back to Release) or NEXT TO THE APP
///     (the deployed layout: the host ships beside the app it serves). Configuration-
///     agnostic resolution matters because the caller's own build configuration does not
///     determine which configuration the host project was last built in — CI builds
///     Release only, dev machines usually Debug. A Debug-only probe returned a path that
///     does not exist and every remote-host session failed at spawn (run 33987882753).
///     <para>The deployed layout exists because a published app has NO repo above it: the
///     repo-only probe walked past the filesystem root and the caller dereferenced the
///     null — the 2026-10-21 crash that killed the app on workspace open.</para></summary>
public static class ChildHostExeLocator
{
  private static readonly string[] ConfigurationPreference = ["Debug", "Release"];

  /// <summary>The host executable's file name.</summary>
  public const string HostExeName = "eThangAgent.ChildHost.exe";

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

  /// <summary>The checkout that CONTAINS the given assembly, or null when the assembly
  ///     lives outside any checkout (a published app). The null is an ordinary answer,
  ///     never a fault: the deployed layout is the fallback, not a failure.</summary>
  public static string? FindRepoRoot(string assemblyLocation)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(assemblyLocation);
    DirectoryInfo? dir = new(assemblyLocation);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    return dir?.FullName;
  }

  /// <summary>The DEPLOYED layout: the host ships NEXT TO THE APP, so a published build
  ///     finds the host it was shipped with without a repo above it. The Desktop project
  ///     references the host project, so every build and publish output carries the exe
  ///     beside the app's own.</summary>
  public static string ResolveFromAppDirectory(string appDirectory)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
    return Path.Combine(appDirectory, HostExeName);
  }

  /// <summary>The resolution ORDER: the repo's live build output first (the dev layout —
  ///     what the developer last built), then the copy shipped next to the app (the
  ///     deployed layout). A null repo root is an ORDINARY case, not a failure: a
  ///     published app has no repo above it, and the deployed copy is the answer. When
  ///     neither candidate exists, the DEPLOYED path is returned so the caller's
  ///     not-found error names where the host is expected to live.</summary>
  public static string Resolve(string? repoRoot, string appDirectory)
  {
    string deployed = ResolveFromAppDirectory(appDirectory);
    if (string.IsNullOrWhiteSpace(repoRoot))
    {
      return deployed;
    }

    string fromRepo = ResolveFromRepoRoot(repoRoot);
    return File.Exists(fromRepo) ? fromRepo : deployed;
  }
}
