
namespace eThangAgent.ToolDomain.Tests;

/// <summary>FileVersion: the content-true freshness pair (Length, LastWriteTimeUtc) the
///     ACL captures at the read moment, plus the stable short token the read tool's
///     annotation shows. Token is deterministic — same pair, same token, every run.</summary>
public class FileVersionTests
{
  private static readonly DateTime Mtime = new(638000000000000000, DateTimeKind.Utc);

  [Fact]
  public void Token_DeterministicForSamePair()
  {
    FileVersion a = new(1234, Mtime);
    FileVersion b = new(1234, Mtime);
    Assert.Equal(a.Token, b.Token);
    Assert.StartsWith("v", a.Token, StringComparison.Ordinal);
  }

  [Fact]
  public void Token_DiffersWhenLengthChanges()
  {
    FileVersion a = new(1234, Mtime);
    FileVersion b = new(1235, Mtime);
    Assert.NotEqual(a.Token, b.Token);
  }

  [Fact]
  public void Token_DiffersWhenMtimeChanges()
  {
    // In-place same-length write: Length identical, mtime differs — the exact
    // failure mode the pair exists to catch.
    FileVersion a = new(1234, Mtime);
    FileVersion b = new(1234, Mtime.AddTicks(1));
    Assert.NotEqual(a.Token, b.Token);
  }

  [Fact]
  public void Token_IsShortHex()
  {
    FileVersion v = new(1234, Mtime);
    string body = v.Token[1..];
    Assert.Equal(8, body.Length);
    Assert.True(body.All(Uri.IsHexDigit), $"token body '{body}' must be hex");
  }

  [Fact]
  public void Equality_IsPairWise()
  {
    Assert.Equal(new FileVersion(10, Mtime), new FileVersion(10, Mtime));
    Assert.NotEqual(new FileVersion(10, Mtime), new FileVersion(11, Mtime));
    Assert.NotEqual(new FileVersion(10, Mtime), new FileVersion(10, Mtime.AddSeconds(1)));
  }

  // ---- FileRead carries the version ----

  [Fact]
  public void FileRead_DefaultVersionIsNull_LegacyShape()
  {
    FileRead legacy = new(["a"], 1, 1);
    Assert.Null(legacy.Version);
  }

  [Fact]
  public void FileRead_CarriesVersion()
  {
    FileVersion v = new(10, Mtime);
    FileRead read = new(["a"], 1, 1, v);
    Assert.Equal(v, read.Version);
  }
}
