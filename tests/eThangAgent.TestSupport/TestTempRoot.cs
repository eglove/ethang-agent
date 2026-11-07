using System.Globalization;

namespace eThangAgent.TestSupport;

/// <summary>The one directory a test process writes scratch state under, and the one place it
///     is removed from.
///     <para>Every scratch file, workspace and database a test creates lands under
///     <see cref="Root"/>, so a whole run's debris is a single directory that
///     <see cref="Delete"/> removes wholesale at assembly teardown. That replaces thousands of
///     per-test best-effort deletes - whose failures were swallowed - with one recursive delete
///     whose failure is reported (issue #371).</para>
///     <para>The root is created once per process and named for it, so two suites running side
///     by side, or a run started while an abandoned one is still being swept, never share a
///     directory. The root is the only thing left behind by a run that dies without reaching
///     teardown, and it is named so a person can find it.</para>
///     <para>A path built here may not exceed <see cref="MaxPathLength"/> characters: tests
///     hand these paths to git, to child hosts and to the file system, and a prefix long enough
///     to push a nested path past the operating system's limit fails deep inside a tool instead
///     of at the call that chose it.</para></summary>
public static class TestTempRoot
{
  /// <summary>The longest path a test may build under the root.</summary>
  internal const int MaxPathLength = 260;

  private const string Prefix = "ethang-testrun-";

  /// <summary>How long a delete keeps retrying a directory that is still held open.</summary>
  private static readonly TimeSpan DeleteBudget = TimeSpan.FromSeconds(30);

  /// <summary>How long a delete waits between attempts.</summary>
  private static readonly TimeSpan DeleteRetryInterval = TimeSpan.FromMilliseconds(250);

  private static readonly Lazy<string> RootPath = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

  /// <summary>The run's temp root: an absolute path that exists for as long as the process
  ///     does.</summary>
  public static string Root => RootPath.Value;

  /// <summary>Creates a uniquely named directory under the root.</summary>
  public static DirectoryInfo CreateDirectory(string prefix)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
    string path = Path.Combine(Root, prefix + "-" + Guid.NewGuid().ToString("N"));
    return Directory.CreateDirectory(Guarded(path, prefix));
  }

  /// <summary>A path for a file under the root. The file is not created.</summary>
  public static string FilePath(string fileName)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
    return Guarded(Path.Combine(Root, fileName), fileName);
  }

  /// <summary>Removes the root and everything under it. A root that cannot be removed throws,
  ///     naming the directory and the failure that stopped it: debris nothing reclaims is the
  ///     defect this type exists for, so a failed sweep may not report green.</summary>
  public static void Delete()
  {
    if (!RootPath.IsValueCreated)
    {
      return;
    }

    string root = RootPath.Value;
    if (!Directory.Exists(root))
    {
      return;
    }

    // A pooled database connection keeps its file open after the connection object is
    // disposed, so the file handle outlives the test that opened it.
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    while (true)
    {
      try
      {
        Directory.Delete(root, recursive: true);
        return;
      }
      catch (IOException ex)
      {
        RetryOrThrow(root, ex, clock);
      }
      catch (UnauthorizedAccessException ex)
      {
        RetryOrThrow(root, ex, clock);
      }
    }
  }

  /// <summary>Waits for one more attempt, or throws once the budget is spent. A surviving
  ///     child host still holding a SQLite file is the ordinary reason an attempt fails, so a
  ///     short retry distinguishes a reap in progress from debris nothing will ever reclaim.
  ///     </summary>
  private static void RetryOrThrow(string root, Exception failure, System.Diagnostics.Stopwatch clock)
  {
    if (clock.Elapsed >= DeleteBudget)
    {
      throw new InvalidOperationException(
          "the test temp root could not be removed: '" + root + "' (" + failure.GetType().Name
          + ": " + failure.Message + ").",
          failure);
    }

    ClearReadOnlyAttributes(root);
    Thread.Sleep(DeleteRetryInterval);
  }

  /// <summary>Clears the read-only bit across the tree: a read-only file makes
  ///     <see cref="Directory.Delete(string, bool)"/> refuse the whole directory, and a test
  ///     that marked a file read-only has no other way to be swept. An entry this pass cannot
  ///     reach is left to the retry that follows.</summary>
  private static void ClearReadOnlyAttributes(string root)
  {
    foreach (string entry in ReadableEntries(root))
    {
      _ = TryClearReadOnly(entry);
    }
  }

  private static List<string> ReadableEntries(string root)
  {
    EnumerationOptions options = new()
    {
      RecurseSubdirectories = true,
      IgnoreInaccessible = true,
      AttributesToSkip = FileAttributes.None,
    };

    try
    {
      return [.. Directory.EnumerateFileSystemEntries(root, "*", options)];
    }
    catch (IOException)
    {
      return [];
    }
    catch (UnauthorizedAccessException)
    {
      return [];
    }
  }

  private static bool TryClearReadOnly(string entry)
  {
    try
    {
      File.SetAttributes(entry, FileAttributes.Normal);
      return true;
    }
    catch (IOException)
    {
      return false;
    }
    catch (UnauthorizedAccessException)
    {
      return false;
    }
  }

  private static string Guarded(string path, string prefix) =>
      path.Length <= MaxPathLength
          ? path
          : throw new InvalidOperationException(
              "a test temp path may not exceed " + MaxPathLength.ToString(CultureInfo.InvariantCulture)
              + " characters: '" + path + "' is " + path.Length.ToString(CultureInfo.InvariantCulture)
              + ". Shorten the '" + prefix + "' prefix.");

  private static string Create()
  {
    string root = Path.Combine(Path.GetTempPath(),
        Prefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
    return Directory.CreateDirectory(root).FullName;
  }
}
