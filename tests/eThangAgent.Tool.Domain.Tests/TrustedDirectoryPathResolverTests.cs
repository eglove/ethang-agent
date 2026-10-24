using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The trusted-directory capability (issue #96) at the resolver seam: an
/// approved external root accepts absolute paths the workspace resolver refuses, a
/// read-only root refuses WRITES with a distinct code while still accepting reads, and
/// everything the wrapped resolver already accepted keeps its exact answer - so a
/// session that approves nothing is byte-identical to the build before this existed.
/// </summary>
public sealed class TrustedDirectoryPathResolverTests
{
  private const string Workspace = @"C:\ws";
  private const string Vault = @"C:\Users\me\vault";

  private static TrustedDirectoryPathResolver Resolver(params TrustedDirectory[] trusted) =>
      new(new WorkspacePathResolver(Workspace), trusted);

  [Fact]
  public void NoTrustedRoots_BehavesExactlyLikeTheWrappedResolver()
  {
    Result<string> trusted = Resolver().Resolve(@"C:\elsewhere\a.txt");
    Result<string> wrapped = new WorkspacePathResolver(Workspace).Resolve(@"C:\elsewhere\a.txt");

    Assert.False(trusted.IsSuccess);
    Assert.False(wrapped.IsSuccess);
    DomainError expected = wrapped.Error;
    DomainError actual = trusted.Error;
    // The error is the WRAPPED one, verbatim: nothing approved means nothing to add.
    Assert.Equal(expected.Code, actual.Code);
    Assert.Equal(expected.Message, actual.Message);
  }

  [Fact]
  public void RelativePath_StillResolvesAtTheWorkspace()
  {
    Result<string> resolved = Resolver(new TrustedDirectory(Vault, Editable: true)).Resolve(@"src\a.cs");

    Assert.True(resolved.IsSuccess);
    Assert.Equal(@"C:\ws\src\a.cs", resolved.Value);
  }

  [Fact]
  public void RelativeEscape_StillRefused()
  {
    // A relative path that escapes the workspace cannot be rescued by an approval:
    // relative paths belong to the workspace, and the trusted roots only answer for
    // ABSOLUTE paths the user explicitly approved.
    Result<string> resolved = Resolver(new TrustedDirectory(Vault, Editable: true)).Resolve(@"..\outside.txt");

    Assert.False(resolved.IsSuccess);
    Assert.Equal(@"PathOutsideWorkspace", resolved.Error.Code);
  }

  [Fact]
  public void AbsolutePathInsideTrustedRoot_Accepted()
  {
    Result<string> resolved = Resolver(new TrustedDirectory(Vault, Editable: true))
        .Resolve(@"C:\Users\me\vault\notes\a.md");

    Assert.True(resolved.IsSuccess);
    Assert.Equal(@"C:\Users\me\vault\notes\a.md", resolved.Value);
  }

  [Fact]
  public void AbsolutePathEqualToTrustedRoot_Accepted()
  {
    Result<string> resolved = Resolver(new TrustedDirectory(Vault, Editable: true)).Resolve(Vault);

    Assert.True(resolved.IsSuccess);
    Assert.Equal(Vault, resolved.Value);
  }

  [Fact]
  public void SiblingSharingAPrefix_IsNotInside()
  {
    // Segment-aware containment: a sibling whose name merely STARTS with the root's
    // name is not inside it.
    Result<string> resolved = Resolver(new TrustedDirectory(Vault, Editable: true))
        .Resolve(@"C:\Users\me\vault-other\a.md");

    Assert.False(resolved.IsSuccess);
    Assert.Equal(@"PathOutsideWorkspace", resolved.Error.Code);
  }

  [Fact]
  public void ReadOnlyRoot_RefusesWriteWithTheDistinctCode()
  {
    Result<string> resolved = Resolver(new TrustedDirectory(Vault)).Resolve(@"C:\Users\me\vault\a.md");

    Assert.False(resolved.IsSuccess);
    Assert.Equal(@"TrustedRootReadOnly", resolved.Error.Code);
    // The message names the root and the remedy, so the model can act on it.
    Assert.Contains(Vault, resolved.Error.Message, StringComparison.Ordinal);
    Assert.Contains(@"READING only", resolved.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void ReadOnlyRoot_AcceptsReadsThroughTheReadView()
  {
    Result<string> resolved = Resolver(new TrustedDirectory(Vault)).AsReadOnlyResolver()
        .Resolve(@"C:\Users\me\vault\a.md");

    Assert.True(resolved.IsSuccess);
    Assert.Equal(@"C:\Users\me\vault\a.md", resolved.Value);
  }

  [Fact]
  public void AsReadOnlyResolver_IsIdempotent()
  {
    TrustedDirectoryPathResolver readOnly = Resolver(new TrustedDirectory(Vault)).AsReadOnlyResolver();

    // A caller may ask unconditionally: the read view's read view is itself.
    Assert.Same(readOnly, readOnly.AsReadOnlyResolver());
  }

  [Fact]
  public void EditableRoot_AcceptsWrites()
  {
    Result<string> resolved = Resolver(new TrustedDirectory(Vault, Editable: true))
        .Resolve(@"C:\Users\me\vault\new.md");

    Assert.True(resolved.IsSuccess);
  }

  [Fact]
  public void PathInNoRoot_KeepsTheCodeAndNamesTheApprovedList()
  {
    Result<string> resolved = Resolver(new TrustedDirectory(Vault, Editable: true))
        .Resolve(@"C:\somewhere\else\a.md");

    Assert.False(resolved.IsSuccess);
    // The CODE stays the contract the model and the tests key on...
    Assert.Equal(@"PathOutsideWorkspace", resolved.Error.Code);
    // ...and the message now teaches the model what IS reachable.
    Assert.Contains(@"Trusted directories approved for this workspace", resolved.Error.Message, StringComparison.Ordinal);
    Assert.Contains(Vault, resolved.Error.Message, StringComparison.Ordinal);
    Assert.Contains(@"read/write", resolved.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void ReadOnlyRoot_IsLabeledReadOnlyInTheRefusal()
  {
    Result<string> resolved = Resolver(new TrustedDirectory(Vault)).Resolve(@"C:\somewhere\a.md");

    Assert.False(resolved.IsSuccess);
    Assert.Contains(@"read-only", resolved.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void BlankPath_StaysInvalidPath()
  {
    // A bad path is bad everywhere: the trusted roots never reinterpret malformed input.
    Result<string> resolved = Resolver(new TrustedDirectory(Vault, Editable: true)).Resolve(@"  ");

    Assert.False(resolved.IsSuccess);
    Assert.Equal(@"InvalidPath", resolved.Error.Code);
  }

  [Fact]
  public void WorkspacePath_IsUnchangedByTheDecorator()
  {
    Result<string> resolved = Resolver(new TrustedDirectory(Vault, Editable: true)).Resolve(@"C:\ws\a.md");

    Assert.True(resolved.IsSuccess);
    Assert.Equal(@"C:\ws\a.md", resolved.Value);
  }

  [Fact]
  public void TrustedRoots_AreNormalizedOnce()
  {
    TrustedDirectoryPathResolver resolver = Resolver(new TrustedDirectory(Vault + @"\", Editable: true));

    // A picked folder arrives with a trailing separator; it must not flip containment.
    Assert.Equal(Vault, Assert.Single(resolver.TrustedRoots).Root);
    Assert.True(resolver.Resolve(@"C:\Users\me\vault\a.md").IsSuccess);
  }
}
