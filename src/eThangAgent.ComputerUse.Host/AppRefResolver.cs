namespace eThangAgent.ComputerUse.Host;

/// <summary>The candidate snapshot the resolver matches over: one row per visible top-level
///     window (handle, pid, title, AUMID-or-null).</summary>
public sealed record Candidate(nint Handle, int Pid, string Title, string? Aumid);

/// <summary>The typed resolution failures (spec 2.2: app_not_found / ambiguous_app).</summary>
public enum AppRefFailureKind
{
  None,
  NotFound,
  Ambiguous,
}

/// <summary>A failed resolution: the kind plus a model-facing message.</summary>
public sealed record AppRefResolutionFailure(AppRefFailureKind Kind, string Message);

/// <summary>The successful resolution: target window handle plus its pid.</summary>
public sealed record AppRefResolution(nint Window, int Pid);

/// <summary>App-ref resolution (controller ruling): by-pid pins the first visible window of that
///     pid; by-name matches the window title case-insensitively (exact match); by-aumid matches
///     the resolved AUMID of each candidate window's process. No match => AppNotFound; by-name
///     matching multiple distinct pids => AmbiguousApp. window_id pins as before.</summary>
public static class AppRefResolver
{
  /// <summary>Attempts resolution. Returns true with <paramref name="resolution"/> on success;
  ///     false with <paramref name="failure"/> carrying the typed kind + message.</summary>
  public static bool TryResolve(IReadOnlyList<Candidate> candidates, int pid, string? name, string? aumid, int? windowId, out AppRefResolution resolution, out AppRefResolutionFailure? failure)
  {
    resolution = new AppRefResolution(0, 0);
    failure = null;

    if (windowId is { } pinned && pinned > 0)
    {
      Candidate? pinnedMatch = candidates.FirstOrDefault(c => c.Handle == pinned);
      if (pinnedMatch is null)
      {
        failure = new AppRefResolutionFailure(AppRefFailureKind.NotFound, "no visible window matches window_id " + pinned + ".");
        return false;
      }

      resolution = new AppRefResolution(pinnedMatch.Handle, pinnedMatch.Pid);
      return true;
    }

    if (pid > 0)
    {
      List<Candidate> byPid = [.. candidates.Where(c => c.Pid == pid)];
      if (byPid.Count == 0)
      {
        failure = new AppRefResolutionFailure(AppRefFailureKind.NotFound, "no visible window for pid " + pid + ".");
        return false;
      }

      resolution = new AppRefResolution(byPid[0].Handle, pid);
      return true;
    }

    if (!string.IsNullOrWhiteSpace(name))
    {
      List<Candidate> byName = [.. candidates.Where(c => string.Equals(c.Title, name, StringComparison.OrdinalIgnoreCase))];
      if (byName.Count == 0)
      {
        failure = new AppRefResolutionFailure(AppRefFailureKind.NotFound, "no window titled '" + name + "'.");
        return false;
      }

      List<int> namePids = [.. byName.Select(c => c.Pid).Distinct()];
      if (namePids.Count > 1)
      {
        failure = new AppRefResolutionFailure(AppRefFailureKind.Ambiguous,
          "multiple apps titled '" + name + "' (pids " + string.Join(", ", namePids) + "); add window_id or use pid.");
        return false;
      }

      resolution = new AppRefResolution(byName[0].Handle, byName[0].Pid);
      return true;
    }

    if (!string.IsNullOrWhiteSpace(aumid))
    {
      List<Candidate> byAumid = [.. candidates.Where(c => string.Equals(c.Aumid, aumid, StringComparison.OrdinalIgnoreCase))];
      if (byAumid.Count == 0)
      {
        failure = new AppRefResolutionFailure(AppRefFailureKind.NotFound, "no app with aumid '" + aumid + "'.");
        return false;
      }

      List<int> aumidPids = [.. byAumid.Select(c => c.Pid).Distinct()];
      if (aumidPids.Count > 1)
      {
        failure = new AppRefResolutionFailure(AppRefFailureKind.Ambiguous,
          "multiple apps with aumid '" + aumid + "' (pids " + string.Join(", ", aumidPids) + "); add window_id or use pid.");
        return false;
      }

      resolution = new AppRefResolution(byAumid[0].Handle, byAumid[0].Pid);
      return true;
    }

    failure = new AppRefResolutionFailure(AppRefFailureKind.NotFound, "app_ref carries no selector.");
    return false;
  }
}
