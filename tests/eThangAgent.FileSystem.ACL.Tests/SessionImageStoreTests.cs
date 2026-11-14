using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>One store persists a session's images whichever producer made them - a
///     server-side tool's artifact or a computer screenshot - so there is exactly one answer
///     to where an image lives, what it is called, and what happens when it is not there.
///     <para>An image is SCRATCH: it lands under the operating system's temp directory in a
///     per-session folder, never in the user's workspace and never in the app's database. The
///     reported path is ABSOLUTE because that is what read_image accepts on a later turn.</para>
///     <para>Every behaviour below is one the producers depend on: distinct writes never
///     collide, a format is sniffed rather than trusted, and a reference whose file is gone
///     is a typed failure naming the path rather than an exception or empty bytes.</para>
///     </summary>
public sealed class SessionImageStoreTests : IDisposable
{
  // A real 3x2 PNG and a real 2x2 JPEG: the store SNIFFS the bytes, so a signature-only
  // fixture would not exercise the admission rule.
  private const string Png3x2Base64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAIAAAASFvFNAAAAFElEQVR4nGP4z8DAAMZMEOo/AwMAO+wF/S5FekgAAAAASUVORK5CYII=";

  private const string Jpeg2x2Base64 =
      "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD5soooroMD/9k=";

  private readonly string _session = "test-" + Guid.NewGuid().ToString("N");
  private readonly List<string> _extraSessions = [];

  /// <summary>The run root's copy of the store's root. The PRODUCTION root is the operating
  ///     system's temp directory; these tests state the run root instead, so their scratch state
  ///     lands where the run removes it and nowhere else.</summary>
  private static string TestArtifactRoot => SessionImageStore.ArtifactRootUnder(TestTempRoot.Root);

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    foreach (string session in _extraSessions.Append(_session))
    {
      try
      {
        string folder = Path.Combine(TestArtifactRoot, session);
        if (Directory.Exists(folder))
        {
          Directory.Delete(folder, recursive: true);
        }
      }
      catch { }
    }
#pragma warning restore CA1031, S108
  }

  private SessionImageStore Store => new(_session, TestTempRoot.Root);

  private static byte[] Png() => Convert.FromBase64String(Png3x2Base64);

  private static byte[] Jpeg() => Convert.FromBase64String(Jpeg2x2Base64);

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>Behaviour 1 - ONE SINK FOR BOTH PRODUCERS. The two producers differ only in
  ///     the payload they hand over; both land under the same root, in the same session
  ///     folder, reported the same way.</summary>
  [Fact]
  public async Task Persist_OfAToolArtifactAndOfAScreenshot_LandInTheSameSessionFolder()
  {
    StoredImage artifact = Must(await Store.PersistAsync("image/png", Png(),
        TestContext.Current.CancellationToken));
    StoredImage screenshot = Must(await Store.PersistAsync("image/jpeg", Jpeg(),
        TestContext.Current.CancellationToken));

    string sessionFolder = Path.Combine(TestArtifactRoot, _session);
    Assert.Equal(sessionFolder, Path.GetDirectoryName(artifact.Path));
    Assert.Equal(sessionFolder, Path.GetDirectoryName(screenshot.Path));
    Assert.Equal("image/png", artifact.MediaType);
    Assert.Equal("image/jpeg", screenshot.MediaType);
  }

  /// <summary>Behaviour 2 - A REFERENCE IS REACHABLE BY THE MODEL: the report is an absolute
  ///     path, and read_image - wired the way a session wires it, over a workspace-rooted
  ///     resolver widened by this folder - opens it and attaches the bytes.</summary>
  [Fact]
  public async Task PersistedImage_IsReadableFromItsAbsolutePath_OutsideTheWorkspace()
  {
    string workspace = TestTempRoot.CreateDirectory("ethang-ws").FullName;
    StoredImage stored = Must(await Store.PersistAsync("image/png", Png(),
        TestContext.Current.CancellationToken));

    Assert.True(Path.IsPathRooted(stored.Path), "read_image needs an absolute path");
    using DirectFileSystemAccess files = new(new EditorConfigTextConventionPolicy(workspace));
    TrustedDirectoryPathResolver resolver = new(
        new WorkspacePathResolver(workspace),
        [new TrustedDirectory(TestArtifactRoot)],
        enforceWritePolicy: false);
    ReadImageTool tool = new(resolver, files, new Vision(true));
    // Serialized, not pasted: a Windows path carries backslashes that break hand-built JSON.
    string arguments = JsonSerializer.Serialize(new { timeoutSeconds = 60, path = stored.Path });
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read_image", arguments),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
    ToolResultImage image = Assert.Single(result.Images!);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(Png3x2Base64, image.Base64Data);
  }

  /// <summary>Behaviour 3 - NO SILENT OVERWRITE. Two payloads, whatever their bytes, are two
  ///     files: writing twice never replaces the first image.</summary>
  [Fact]
  public async Task Persist_Twice_NeverOverwrites()
  {
    StoredImage first = Must(await Store.PersistAsync("image/png", Png(),
        TestContext.Current.CancellationToken));
    StoredImage second = Must(await Store.PersistAsync("image/png", Png(),
        TestContext.Current.CancellationToken));

    Assert.NotEqual(first.Path, second.Path);
    Assert.NotEqual(first.FileName, second.FileName);
    Assert.True(File.Exists(first.Path));
    Assert.True(File.Exists(second.Path));
  }

  /// <summary>The example the plan states outright: two DISTINCT images for one session id
  ///     resolve back to their own bytes, and neither file's contents equal the other's.
  ///     </summary>
  [Fact]
  public async Task Persist_TwoDistinctImages_ResolveToTheirOwnBytes()
  {
    byte[] png = Png();
    byte[] jpeg = Jpeg();
    StoredImage first = Must(await Store.PersistAsync("image/png", png,
        TestContext.Current.CancellationToken));
    StoredImage second = Must(await Store.PersistAsync("image/jpeg", jpeg,
        TestContext.Current.CancellationToken));

    Assert.NotEqual(first.Path, second.Path);
    Assert.True(File.Exists(first.Path));
    Assert.True(File.Exists(second.Path));

    StoredImageContent firstBytes = Must(await Store.ResolveAsync(first.Path,
        TestContext.Current.CancellationToken));
    StoredImageContent secondBytes = Must(await Store.ResolveAsync(second.Path,
        TestContext.Current.CancellationToken));
    Assert.Equal(png, firstBytes.Bytes.ToArray());
    Assert.Equal(jpeg, secondBytes.Bytes.ToArray());
    Assert.NotEqual(firstBytes.Bytes.ToArray(), secondBytes.Bytes.ToArray());
    Assert.Equal("image/png", firstBytes.MediaType);
    Assert.Equal("image/jpeg", secondBytes.MediaType);
  }

  /// <summary>Behaviour 4 - THE SESSION'S FOLDER IS THE SESSION'S. Two sessions never share a
  ///     folder, so two conversations never overwrite each other's images.</summary>
  [Fact]
  public async Task Persist_ForTwoSessionIds_UsesTwoFolders()
  {
    string other = "test-" + Guid.NewGuid().ToString("N");
    _extraSessions.Add(other);

    StoredImage mine = Must(await Store.PersistAsync("image/png", Png(),
        TestContext.Current.CancellationToken));
    StoredImage theirs = Must(await new SessionImageStore(other, TestTempRoot.Root).PersistAsync("image/png",
        Png(), TestContext.Current.CancellationToken));

    Assert.NotEqual(Path.GetDirectoryName(mine.Path), Path.GetDirectoryName(theirs.Path));
    Assert.Contains(_session, mine.Path, StringComparison.Ordinal);
    Assert.Contains(other, theirs.Path, StringComparison.Ordinal);
  }

  /// <summary>The awkward case: a blank session id still gets ONE folder, and two writes
  ///     there still do not collide.</summary>
  [Fact]
  public async Task Persist_ForABlankSessionId_SharesOneFolderWithoutColliding()
  {
    SessionImageStore store = new("  ", TestTempRoot.Root);

    StoredImage first = Must(await store.PersistAsync("image/png", Png(),
        TestContext.Current.CancellationToken));
    StoredImage second = Must(await store.PersistAsync("image/png", Png(),
        TestContext.Current.CancellationToken));

    Assert.Equal("shared", Path.GetFileName(Path.GetDirectoryName(first.Path)));
    Assert.Equal(Path.GetDirectoryName(first.Path), Path.GetDirectoryName(second.Path));
    Assert.NotEqual(first.Path, second.Path);
    Assert.True(File.Exists(first.Path));
    Assert.True(File.Exists(second.Path));
  }

  /// <summary>Behaviour 5 - A MISSING FILE IS A TYPED RESULT. The reference's file is gone: a
  ///     typed failure names the path, it never throws and never reports empty bytes as
  ///     success.</summary>
  [Fact]
  public async Task Resolve_OfAReferenceWhoseFileIsGone_IsATypedFailureNamingThePath()
  {
    StoredImage stored = Must(await Store.PersistAsync("image/png", Png(),
        TestContext.Current.CancellationToken));
    File.Delete(stored.Path);

    Result<StoredImageContent> resolved = await Store.ResolveAsync(stored.Path,
        TestContext.Current.CancellationToken);

    Assert.False(resolved.IsSuccess);
    Assert.Equal("ImageNotFound", resolved.Error.Code);
    Assert.Contains(stored.Path, resolved.Error.Message, StringComparison.Ordinal);
  }

  /// <summary>A resolve reports the format it SNIFFED, never a name's claim: a file holding
  ///     text is refused rather than handed back as an image.</summary>
  [Fact]
  public async Task Resolve_OfAFileThatIsNotAnImage_IsRefused()
  {
    string path = TestTempRoot.FilePath("not-an-image-" + Guid.NewGuid().ToString("N") + ".png");
    await File.WriteAllTextAsync(path, "not a png",
        TestContext.Current.CancellationToken);

    Result<StoredImageContent> resolved = await Store.ResolveAsync(path,
        TestContext.Current.CancellationToken);

    Assert.False(resolved.IsSuccess);
    Assert.Equal("UnsupportedImageFormat", resolved.Error.Code);
  }

  /// <summary>The extension follows the SNIFFED format, so a stored image's name never claims
  ///     a format its bytes deny - which is what lets a reference name it safely.</summary>
  [Fact]
  public async Task Persist_NamesTheFileByTheSniffedFormat_NotTheClaim()
  {
    // A JPEG payload whose producer claims image/png: the bytes decide, and the bytes are
    // refused because they contradict the claim.
    Result<StoredImage> refused = await Store.PersistAsync("image/png", Jpeg(),
        TestContext.Current.CancellationToken);
    Assert.False(refused.IsSuccess);
    Assert.Equal("ImageMediaTypeMismatch", refused.Error.Code);

    StoredImage stored = Must(await Store.PersistAsync("image/jpeg", Jpeg(),
        TestContext.Current.CancellationToken));
    Assert.EndsWith(".jpg", stored.FileName, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Persist_OfAnEmptyPayload_IsRefused()
  {
    Result<StoredImage> refused = await Store.PersistAsync("image/png", [],
        TestContext.Current.CancellationToken);

    Assert.False(refused.IsSuccess);
    Assert.Equal("InvalidImagePayload", refused.Error.Code);
  }

  [Fact]
  public async Task Persist_OfAPayloadOverTheImageBound_IsRefusedAndWritesNothing()
  {
    byte[] oversize = new byte[ImageLimits.MaxBytes + 1];
    Png().CopyTo(oversize, 0);

    Result<StoredImage> refused = await Store.PersistAsync("image/png", oversize,
        TestContext.Current.CancellationToken);

    Assert.False(refused.IsSuccess);
    Assert.Equal("ImageTooLarge", refused.Error.Code);
    Assert.False(Directory.Exists(Path.Combine(TestArtifactRoot, _session)),
        "a refused payload leaves no file, so no folder is created for it");
  }

  /// <summary>The base64 entry point carries the same admission rule: the form the wire and a
  ///     tool result both use is not a second, laxer door.</summary>
  [Fact]
  public async Task PersistBase64_OfTextThatIsNotBase64_IsATypedFailure()
  {
    Result<StoredImage> refused = await Store.PersistBase64Async("image/png", "not base64!",
        TestContext.Current.CancellationToken);

    Assert.False(refused.IsSuccess);
    Assert.Equal("InvalidImagePayload", refused.Error.Code);
  }

  [Fact]
  public async Task PersistBase64_OfAPng_ReportsTheAbsolutePath()
  {
    StoredImage stored = Must(await Store.PersistBase64Async("image/png", Png3x2Base64,
        TestContext.Current.CancellationToken));

    Assert.True(File.Exists(stored.Path));
    Assert.StartsWith(TestArtifactRoot, stored.Path, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(Png(), await File.ReadAllBytesAsync(stored.Path,
        TestContext.Current.CancellationToken));
  }

  /// <summary>The persisted name is one <see cref="ImagePayload.ExtensionAgrees"/> accepts,
  ///     which is what makes a reference naming it constructible at all.</summary>
  [Theory]
  [InlineData("image/png")]
  [InlineData("image/jpeg")]
  public async Task PersistedName_IsAnImageReferenceThePartRuleAccepts(string mediaType)
  {
    byte[] bytes = mediaType == "image/jpeg" ? Jpeg() : Png();
    StoredImage stored = Must(await Store.PersistAsync(mediaType, bytes,
        TestContext.Current.CancellationToken));

    Assert.True(ImagePayload.ExtensionAgrees(stored.Path, stored.MediaType));
    Assert.Equal(mediaType, stored.MediaType);
  }

  private sealed class Vision(bool accepts) : IImageInputCapability
  {
    public bool AcceptsImages => accepts;
  }
}
