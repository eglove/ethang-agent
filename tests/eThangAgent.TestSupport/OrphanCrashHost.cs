namespace eThangAgent.TestSupport;

/// <summary>The ONE command line the orphan sweep's crash tests spawn, spelled in one place.
///     <para>REAL PROCESSES, NOT SIMULATED ONES: the behaviour under test is what a killed
///     process leaves behind, and a fake store that throws in the middle of an append proves
///     nothing about what survives on disk. So the test starts this program, waits for it to
///     confirm the file is written, kills it, and then looks at what a restart would find.</para>
///     <para>IT IS A SEPARATE EXECUTABLE because the file write and the kill have to be two
///     processes. The arguments are: the verb, the session image folder's base directory, the
///     session id, and a PNG payload size in bytes.</para></summary>
public static class OrphanCrashHost
{
  /// <summary>Writes one image and exits 0 WITHOUT anything naming it - the state a process
  ///     killed between the write and the transcript flush leaves behind.</summary>
  public const string WriteOnly = "write-only";

  /// <summary>The project file, resolved from a test assembly's output directory the way every
  ///     other in-repo stub host is (three levels up to tests/, then into this directory).</summary>
  public static string Project(string testAssemblyDirectory) =>
      Path.GetFullPath(Path.Combine(testAssemblyDirectory, "..", "..", "..", "Host", "orphan-host.csproj"));

  /// <summary>The assembly the built project produces, under the standard Debug output path.</summary>
  public static string Executable(string projectPath) =>
      Path.Combine(Path.GetDirectoryName(projectPath)!, "bin", "Debug", "net11.0", "ethang-orphan-host.exe");
}
