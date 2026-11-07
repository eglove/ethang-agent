using eThangAgent.Storage.ACL;

namespace eThangAgent.TestSupport;

/// <summary>A database file already at <see cref="AppDatabase.LatestSchemaVersion"/>, produced
///     by copying a template migrated once per test process.
///     <para>Building a database from scratch runs all 36 migrations and costs ~284 ms; copying
///     a migrated 260 KB template and opening it costs ~3 ms. The template is built lazily so
///     it always reflects the migrations in the code under test, and once rather than per test.</para>
///     <para>A copy carries the SCHEMA only, never rows: a test that needs an older version
///     stamps the version and drops what it needs, exactly as it did against a freshly
///     migrated file.</para></summary>
public static class TemplateDatabase
{
  private static readonly Lazy<string> TemplatePath =
      new(BuildTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

  /// <summary>A new temp database file, already migrated, that the caller owns and deletes.</summary>
  public static string Copy()
  {
    string path = TestTempRoot.FilePath($"ethang-test-{Guid.NewGuid():N}.db");
    File.Copy(TemplatePath.Value, path, overwrite: false);
    return path;
  }

  private static string BuildTemplate()
  {
    string directory = TestTempRoot.CreateDirectory("ethang-template").FullName;
    string path = Path.Combine(directory, "template.db");
    _ = new AppDatabase(path);
    return path;
  }
}
