namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>What a language-server test that leaves the fast tier actually does, named as data
///     in one place.
///     <para>IT LIVES HERE, in a file that holds no test, so the check that uses it can be
///     written without matching itself: a marker list spelled inside a test file is text that
///     test's own scan finds.</para></summary>
internal static class ProcessTierMarkers
{
  /// <summary>The ways a language-server test reaches a process, a pipe, or a download.
  ///     <para><c>LspServerSet.StartAsync</c> is deliberately absent: its fake-starter overload
  ///     starts no process, so naming it would put the routing tests in the process tier.
  ///     Constructing an install strategy is absent for the same reason: only reaching the
  ///     network does, and that is <c>RealServerWorkspace.InstallAsync</c>.</para>
  ///     </summary>
  public static IReadOnlyList<string> Spawns { get; } =
  [
      "Process.Start",
      "ProcessStartInfo",
      "LspProcessServerStarter",
      "StubLspServer",
      "LspServerDownloader",
      "RealServerWorkspace.Install",
      "NamedPipeServerStream",
      "NamedPipeClientStream",
      "LspPipeClient",
  ];

  /// <summary>The attribute that puts a test in the process tier.</summary>
  public const string Trait = "[Trait(\"Category\", \"Process\")]";
}
