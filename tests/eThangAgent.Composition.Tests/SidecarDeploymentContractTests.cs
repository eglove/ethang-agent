using System.Xml.Linq;

namespace eThangAgent.Composition.Tests;

/// <summary>The sidecar apps (the conversation host, the computer-use broker) are APPS of
///     their own: each resolves its native assets through its OWN dependency manifest
///     (eThangAgent.ChildHost.deps.json -> e_sqlite3.dll). A publishing app copies a
///     referenced project's ASSEMBLIES but not that manifest, so a published host could
///     not load SQLite and every root turn faulted with "The type initializer for
///     'Microsoft.Data.Sqlite.SqliteConnection' threw an exception" - a fact that read
///     as a product bug until the deployment gap was found (2026-10-22).
///     <para>The declaration in the sidecar project IS the contract, so this test pins
///     it: a sidecar that stops publishing its manifest fails here, not in a user's
///     published app.</para></summary>
public class SidecarDeploymentContractTests
{
  /// <summary>The sidecar apps that ship next to the desktop app.</summary>
  private static readonly string[] SidecarProjects =
  [
      "eThangAgent.ChildHost",
      "eThangAgent.ComputerUse.Host",
  ];

  [Fact]
  public void EverySidecarApp_Publishes_Its_Dependency_Manifest()
  {
    string? repo = ChildHostExeLocator.FindRepoRoot(typeof(AgentSessionFactory).Assembly.Location);
    Assert.True(repo is not null, "the sidecar projects are only reachable from a checkout.");

    foreach (string project in SidecarProjects)
    {
      string path = Path.Combine(repo, "src", project, project + ".csproj");
      Assert.True(File.Exists(path), path + " should exist.");

      XDocument document = XDocument.Load(path);
      bool declared = document.Descendants("Content")
          .Any(content =>
              content.Attribute("Include")?.Value.Contains("ProjectDepsFileName", StringComparison.Ordinal) == true
              && content.Attribute("CopyToPublishDirectory")?.Value.Contains("PreserveNewest", StringComparison.Ordinal) == true);

      Assert.True(declared,
          project + " must publish its dependency manifest (" + project + ".deps.json): a sidecar app "
          + "without it cannot resolve its native assets, and every root turn in a published app "
          + "faulted on the missing SQLite library (2026-10-22).");
    }
  }
}
