using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The second source of reach: a path the user QUOTED in a message is granted for the
///     session, read-only, so the tools open the file the user named without the approval
///     dialog a whole directory would need. Everything else keeps its exact answer - a session
///     that quotes nothing resolves exactly as it did before grants existed.</summary>
public sealed class SessionPathGrantsTests
{
  private const string Workspace = @"C:\ws";
  private const string Downloads = @"C:\Users\me\Downloads";

  private static TrustedDirectoryPathResolver Resolver(SessionPathGrants grants, bool enforceWritePolicy = false) =>
      new(new WorkspacePathResolver(Workspace), [], enforceWritePolicy, grants);

  [Fact]
  public void AQuotedFileIsReachableAfterTheGrant()
  {
    SessionPathGrants grants = new();
    string file = Path.Combine(Downloads, "graph.png");

    Assert.False(Resolver(grants).Resolve(file).IsSuccess);
    grants.Grant(file);

    Result<string> resolved = Resolver(grants).Resolve(file);
    Assert.True(resolved.IsSuccess);
    Assert.Equal(file, resolved.Value);
  }

  [Fact]
  public void AGrantIsReadOnlyForAMutatingTool()
  {
    // Quoting a path says which file to look at, never that it may be written.
    SessionPathGrants grants = new();
    string file = Path.Combine(Downloads, "graph.png");
    grants.Grant(file);

    Result<string> resolved = Resolver(grants, enforceWritePolicy: true).Resolve(file);

    Assert.False(resolved.IsSuccess);
    Assert.Equal("TrustedRootReadOnly", resolved.Error.Code);
  }

  [Fact]
  public void AGrantReachesTheQuotedFileAndNothingBesideIt()
  {
    SessionPathGrants grants = new();
    grants.Grant(Path.Combine(Downloads, "notes.txt"));

    Assert.True(Resolver(grants).Resolve(Path.Combine(Downloads, "notes.txt")).IsSuccess);
    Assert.False(Resolver(grants).Resolve(Path.Combine(Downloads, "other.txt")).IsSuccess);
  }

  [Fact]
  public void AQuotedDirectoryGrantsItsContents()
  {
    SessionPathGrants grants = new();
    grants.Grant(Downloads);

    Assert.True(Resolver(grants).Resolve(Path.Combine(Downloads, "deep", "a.txt")).IsSuccess);
  }

  [Fact]
  public void ARepeatedGrantIsStoredOnce()
  {
    SessionPathGrants grants = new();
    string file = Path.Combine(Downloads, "graph.png");

    grants.Grant(file);
    grants.Grant(file.ToUpperInvariant());

    _ = Assert.Single(grants.List);
  }

  [Fact]
  public void ARelativePathGrantsNothing()
  {
    // A relative path names no location until a workspace is chosen for it, so it cannot
    // grant one.
    SessionPathGrants grants = new();

    grants.Grant(@"notes.txt");

    Assert.Empty(grants.List);
  }

  [Fact]
  public void WithNoGrantTheResolverIsUnchanged()
  {
    Result<string> resolved = Resolver(new SessionPathGrants()).Resolve(Path.Combine(Downloads, "a.txt"));

    Assert.False(resolved.IsSuccess);
    Assert.Equal("PathOutsideWorkspace", resolved.Error.Code);
  }

  [Fact]
  public void TheReadViewKeepsTheGrants()
  {
    SessionPathGrants grants = new();
    string file = Path.Combine(Downloads, "graph.png");
    grants.Grant(file);

    Assert.True(Resolver(grants, enforceWritePolicy: true).AsReadOnlyResolver().Resolve(file).IsSuccess);
  }
}
