using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

/// <summary>A workspace IS its directory path, and nothing is written into the directory to
///     identify it. These pin the rule and the accepted cost: a directory that is moved or
///     renamed arrives as a new workspace, and the records scoped to the old path stay with the
///     old path.</summary>
public class WorkspaceIdentityTests
{
  [Fact]
  public void For_IsTheDirectoryPath()
      => Assert.Equal("C:\\work\\repo", WorkspaceIdentity.For("C:\\work\\repo"));

  /// <summary>A trailing separator is not a different directory, so it is not a different
  ///     workspace: the alternative is two record sets for one folder.</summary>
  [Fact]
  public void For_IgnoresATrailingSeparator()
      => Assert.Equal(WorkspaceIdentity.For("C:\\work\\repo"), WorkspaceIdentity.For("C:\\work\\repo\\"));

  /// <summary>The accepted cost, stated as a test: a RENAMED directory is a new workspace.</summary>
  [Fact]
  public void For_OfADifferentDirectory_IsADifferentIdentity()
      => Assert.NotEqual(WorkspaceIdentity.For("C:\\work\\repo"), WorkspaceIdentity.For("C:\\work\\repo2"));

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  public void For_OfABlankRoot_IsRefused(string? root)
      => _ = Assert.ThrowsAny<ArgumentException>(() => WorkspaceIdentity.For(root!));

  [Theory]
  [InlineData("C:\\work\\repo", "C:\\work\\repo")]
  [InlineData("C:\\work\\repo\\", "C:\\work\\repo")]
  [InlineData("C:\\WORK\\Repo", "c:\\work\\repo")]
  public void PathsEqual_IgnoresCaseAndTrailingSeparators(string left, string right)
      => Assert.True(WorkspaceIdentity.PathsEqual(left, right));

  [Fact]
  public void PathsEqual_DistinguishesSiblingsThatShareAPrefix()
      => Assert.False(WorkspaceIdentity.PathsEqual("C:\\work\\repo", "C:\\work\\repo2"));

  [Fact]
  public void Normalize_OfBlankInput_IsEmpty()
      => Assert.Equal(string.Empty, WorkspaceIdentity.Normalize("   "));

  /// <summary>A drive root keeps its separator: <c>C:\</c> is the root of a drive and
  ///     <c>C:</c> is a relative path to that drive's current directory, so trimming the last
  ///     separator would mint a different directory rather than a shorter spelling of one.
  ///     </summary>
  [Theory]
  [InlineData("C:\\", "C:\\")]
  [InlineData("C:\\\\", "C:\\")]
  [InlineData("C:\\work\\", "C:\\work")]
  [InlineData("C:\\work", "C:\\work")]
  public void Normalize_KeepsTheSeparatorOfADriveRoot(string path, string expected)
      => Assert.Equal(expected, WorkspaceIdentity.Normalize(path));

  /// <summary>The store boundary: an id that is already the normalized form is accepted, and one
  ///     that is not is refused rather than silently repaired - a store that repaired it would
  ///     leave the caller writing rows under the other spelling.</summary>
  [Fact]
  public void Require_AcceptsANormalizedId()
      => Assert.Equal("C:\\work\\repo", WorkspaceIdentity.Require("C:\\work\\repo"));

  [Theory]
  [InlineData("C:\\work\\repo\\")]
  [InlineData(" C:\\work\\repo")]
  public void Require_RefusesAnIdThatIsNotNormalized(string id)
  {
    ArgumentException refused = Assert.Throws<ArgumentException>(() => WorkspaceIdentity.Require(id));
    Assert.Contains(WorkspaceIdentity.Normalize(id), refused.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  public void Require_OfABlankId_IsRefused(string? id)
      => _ = Assert.ThrowsAny<ArgumentException>(() => WorkspaceIdentity.Require(id!));
}
