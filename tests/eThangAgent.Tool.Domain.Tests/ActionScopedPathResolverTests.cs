using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Action-scoped path resolution (issue #96): a container-format tool resolves
/// ONE path before dispatching on an action, and the actions differ in what they do to
/// the file. A READ-ONLY action may reach a trusted root approved for reading only; every
/// other action - including an action nobody named, which no tool can prove read-only -
/// keeps the strict resolver. Deny by default is the whole point: the capability must
/// never widen silently.</summary>
public sealed class ActionScopedPathResolverTests
{
  private const string Workspace = @"C:\ws";
  private const string Vault = @"C:\Users\me\vault";

  private static readonly IReadOnlySet<string> ReadOnlyActions =
      new HashSet<string>(StringComparer.Ordinal) { @"read", @"render" };

  private static TrustedDirectoryPathResolver Resolver() =>
      new(new WorkspacePathResolver(Workspace), [new TrustedDirectory(Vault)]);

  /// <summary>The call's raw arguments for one action. (The two hand-written JSON shapes
  ///     below trip JSON002, which fires only in the format/IDE host; the pragma pair is the
  ///     repository's named pattern for deliberate test JSON.)</summary>
  private static string Args(string? action) =>
      action is null ? "{}" : JsonSerializer.Serialize(new { action });

  [Fact]
  public void ReadOnlyAction_ResolvesReadOnlyRootPath()
  {
    Result<string> resolved = ActionScopedPathResolver.Resolve(
        Resolver(), @"C:\Users\me\vault\a.docx", Args("read"), ReadOnlyActions);

    Assert.True(resolved.IsSuccess);
  }

  [Fact]
  public void MutatingAction_RefusesReadOnlyRootPath()
  {
    // 'edit' rewrites the document, so the read-only approval must refuse it -
    // otherwise approving a root for READING would silently grant writing.
    Result<string> resolved = ActionScopedPathResolver.Resolve(
        Resolver(), @"C:\Users\me\vault\a.docx", Args("edit"), ReadOnlyActions);

    Assert.False(resolved.IsSuccess);
    Assert.Equal(@"TrustedRootReadOnly", resolved.Error.Code);
  }

  [Fact]
  public void UnknownAction_TakesTheStrictPath()
  {
    // Deny by default: an action this tool does not know is treated as mutating.
    Result<string> resolved = ActionScopedPathResolver.Resolve(
        Resolver(), @"C:\Users\me\vault\a.docx", Args("deleteEverything"), ReadOnlyActions);

    Assert.False(resolved.IsSuccess);
    Assert.Equal(@"TrustedRootReadOnly", resolved.Error.Code);
  }

  [Fact]
  public void MissingAction_TakesTheStrictPath()
  {
    Result<string> resolved = ActionScopedPathResolver.Resolve(
        Resolver(), @"C:\Users\me\vault\a.docx", Args(null), ReadOnlyActions);

    Assert.False(resolved.IsSuccess);
    Assert.Equal(@"TrustedRootReadOnly", resolved.Error.Code);
  }

  [Fact]
  public void UnparseableArguments_TakeTheStrictPath()
  {
    // Reporting a malformed call is the TOOL's job with its own code; this layer must
    // not swallow it, and it certainly must not take it as evidence of a read.
    Result<string> resolved = ActionScopedPathResolver.Resolve(
        Resolver(), @"C:\Users\me\vault\a.docx", @"{not json", ReadOnlyActions);

    Assert.False(resolved.IsSuccess);
    Assert.Equal(@"TrustedRootReadOnly", resolved.Error.Code);
  }

  [Fact]
  public void NonStringAction_TakesTheStrictPath()
  {
    // A non-string action cannot name a known read-only verb, so it is refused the
    // widening rather than coerced into one.
    Result<string> resolved = ActionScopedPathResolver.Resolve(
#pragma warning disable JSON002 // Probable JSON string detected
        Resolver(), @"C:\Users\me\vault\a.docx", "{\"action\":7}", ReadOnlyActions);
#pragma warning restore JSON002

    Assert.False(resolved.IsSuccess);
    Assert.Equal(@"TrustedRootReadOnly", resolved.Error.Code);
  }

  [Fact]
  public void ReadOnlyAction_StillAcceptsWorkspacePath()
  {
    Result<string> resolved = ActionScopedPathResolver.Resolve(
        Resolver(), @"C:\ws\a.docx", Args("read"), ReadOnlyActions);

    Assert.True(resolved.IsSuccess);
    Assert.Equal(@"C:\ws\a.docx", resolved.Value);
  }

  [Fact]
  public void ReadOnlyAction_StillRefusesUnapprovedExternalPath()
  {
    // Approving ONE root does not open the disk: a read-only action reaches the vault
    // and nothing else.
    Result<string> resolved = ActionScopedPathResolver.Resolve(
        Resolver(), @"C:\somewhere\a.docx", Args("read"), ReadOnlyActions);

    Assert.False(resolved.IsSuccess);
    Assert.Equal(@"PathOutsideWorkspace", resolved.Error.Code);
  }

  [Fact]
  public void PlainResolver_IsUnrelaxedByTheReadView()
  {
    // A workspace-only resolver (no trusted roots, e.g. an anchored child) has no read
    // view to reach: the read action must not become a way around the anchor.
    Result<string> resolved = ActionScopedPathResolver.Resolve(
        new WorkspacePathResolver(Workspace), @"C:\elsewhere\a.docx", Args("read"), ReadOnlyActions);

    Assert.False(resolved.IsSuccess);
    Assert.Equal(@"PathOutsideWorkspace", resolved.Error.Code);
  }

  [Fact]
  public void CustomActionParameter_IsHonored()
  {
    // pdf's action parameter is @"action" too, but the seam must not hardcode it:
    // a tool with a differently named selector states it and it is read.
    Result<string> resolved = ActionScopedPathResolver.Resolve(
#pragma warning disable JSON002 // Probable JSON string detected
        Resolver(), @"C:\Users\me\vault\a.pdf", "{\"op\":\"read\"}", ReadOnlyActions, @"op");
#pragma warning restore JSON002

    Assert.True(resolved.IsSuccess);
  }
}
