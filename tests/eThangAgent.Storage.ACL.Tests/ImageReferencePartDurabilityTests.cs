using System.Reflection;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The durable form of an image is a FILE PLUS A POINTER, never bytes in a row. A
///     persisted image part names the stored file (path and media type, no payload), the row
///     stays far under a kilobyte, and a row written by an older build - the inline Base64Data
///     shape - still reads back as an image part carrying its payload. The file itself is a
///     real PNG or JPEG: the format is SNIFFED from the bytes, never taken from the claim.</summary>
public sealed class ImageReferencePartDurabilityTests : IDisposable
{
  // A real 3x2 PNG and a real 2x2 JPEG: the admission rule sniffs bytes, so a
  // signature-only fixture would not exercise it.
  private const string Png3x2Base64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAIAAAASFvFNAAAAFElEQVR4nGP4z8DAAMZMEOo/AwMAO+wF/S5FekgAAAAASUVORK5CYII=";

  private const string Jpeg2x2Base64 =
      "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD5soooroMD/9k=";

  private readonly string _dbPath = TemplateDatabase.Copy();
  private readonly string _imageRoot = TestTempRoot.CreateDirectory("ethang-imgref").FullName;
  private readonly SqliteAgentStore _store;

  public ImageReferencePartDurabilityTests()
      => _store = new SqliteAgentStore(new AppDatabase(_dbPath));

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  /// <summary>Writes <paramref name="bytes"/> to a fresh file under the run root and returns
  ///     its absolute path. The file lives under the run's temp root, never in the workspace:
  ///     the harness adds nothing to a directory it was merely pointed at.</summary>
  private string StoreImage(byte[] bytes, string extension)
  {
    string path = Path.Combine(_imageRoot, $"image-{Guid.NewGuid():N}.{extension}");
    File.WriteAllBytes(path, bytes);
    return path;
  }

  private async Task<AgentId> SavedAgentAsync()
  {
    AgentId id = AgentId.NewId();
    _ = await _store.SaveAsync(AgentRecord.Started(id, "provider/model", null, "task",
        new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    return id;
  }

  private static DateTimeOffset At(int seconds) => new(2026, 8, 21, 10, 0, seconds, TimeSpan.Zero);

  /// <summary>Appends one Tool message carrying <paramref name="part"/> and returns the
  ///     stored row's meta_json, read straight from the file.</summary>
  private async Task<string> AppendAndReadMetaAsync(AgentId id, MessagePart part)
  {
    Message tool = new(Role.Tool, "captured the screen", At(2), ToolCallId: "call_1", Parts: [part]);
    Result<string> appended = await _store.AppendMessageAsync(id, tool,
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(appended.IsSuccess, appended.Error?.Message);
    return ReadMetaJson(id);
  }

  /// <summary>Reads the only message row's meta_json through a connection this test owns,
  ///     so the assertion is about what the database actually holds.</summary>
  private string ReadMetaJson(AgentId id)
  {
    using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT meta_json FROM agent_messages WHERE agent_id=@id ORDER BY seq DESC LIMIT 1;";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    return (string)command.ExecuteScalar()!;
  }

  // --- Behaviour 1: the row carries a reference, not bytes ---

  [Fact]
  public async Task ReferencePart_PersistsAPointerUnderAKilobyte_AndReadsBackNamingTheFile()
  {
    AgentId id = await SavedAgentAsync();
    byte[] png = Convert.FromBase64String(Png3x2Base64);
    string path = StoreImage(png, "png");
    MessagePart.ImageReferencePart part = Must(MessagePart.ImageReferencePart.Create("image/png", png, path));

    string metaJson = await AppendAndReadMetaAsync(id, part);

    Assert.DoesNotContain("Base64Data", metaJson, StringComparison.Ordinal);
    Assert.True(metaJson.Length < 1024,
        $"a reference row stays under 1,024 bytes; this one is {metaJson.Length}: {metaJson}");

    // Read through a SECOND AppDatabase instance over the same file: nothing in memory
    // is doing the work.
    Result<IReadOnlyList<Message>> transcript = await new SqliteAgentStore(new AppDatabase(_dbPath))
        .GetTranscriptAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(transcript.IsSuccess, transcript.Error?.Message);
    Message loaded = Assert.Single(transcript.Value);
    MessagePart.ImageReferencePart loadedPart = Assert.IsType<MessagePart.ImageReferencePart>(
        Assert.Single(loaded.Parts!));
    Assert.Equal(path, loadedPart.Path);
    Assert.Equal("image/png", loadedPart.MediaType);
  }

  // --- Behaviour 2: the part carries only what identifies the image ---

  /// <summary>A screenshot-sized payload: the row must not scale with the image. The
  ///     fixture is the PNG signature followed by filler, which is the shape this
  ///     repository's sniff rule admits (ImageFormats reads the signature, and
  ///     ReadImageToolTests builds its oversize case the same way). The inline form of
  ///     this payload would put over half a megabyte of base64 in the row.</summary>
  [Fact]
  public async Task ReferencePart_RowDoesNotScaleWithTheImage()
  {
    AgentId id = await SavedAgentAsync();
    byte[] screenshot = new byte[400 * 1024];
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(screenshot, 0);
    string path = StoreImage(screenshot, "png");
    MessagePart.ImageReferencePart part = Must(MessagePart.ImageReferencePart.Create("image/png", screenshot, path));

    string metaJson = await AppendAndReadMetaAsync(id, part);

    Assert.DoesNotContain("Base64Data", metaJson, StringComparison.Ordinal);
    Assert.True(metaJson.Length < 1024,
        $"a {screenshot.Length}-byte image leaves a row under 1,024 bytes; this one is {metaJson.Length}");
    Assert.True(Convert.ToBase64String(screenshot).Length > 500_000,
        "the inline form of this payload would be over half a megabyte of base64 in the row");
  }

  [Fact]
  public void ReferencePart_CarriesPathAndMediaType_AndNoBytes()
  {
    byte[] png = Convert.FromBase64String(Png3x2Base64);
    string path = StoreImage(png, "png");
    MessagePart.ImageReferencePart part = new(path, "image/png");

    // The whole part state, read off the type itself: a payload field added later fails here.
    string[] properties = [.. part.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(property => property.Name)
        .OrderBy(name => name, StringComparer.Ordinal)];

    Assert.Equal(["MediaType", "Path"], properties);
    Assert.Equal(path, part.Path);
    Assert.Equal("image/png", part.MediaType);
    Assert.DoesNotContain("base64", JsonSerializer.Serialize(part), StringComparison.OrdinalIgnoreCase);
  }

  // --- Behaviour 3: the file is a real image ---

  [Fact]
  public void ReferencedFile_IsARealImage_WhoseExtensionAgreesWithItsSniffedFormat()
  {
    byte[] png = Convert.FromBase64String(Png3x2Base64);
    byte[] jpeg = Convert.FromBase64String(Jpeg2x2Base64);

    string pngPath = StoreImage(png, "png");
    string jpegPath = StoreImage(jpeg, "jpg");
    _ = Must(MessagePart.ImageReferencePart.Create("image/png", png, pngPath));
    _ = Must(MessagePart.ImageReferencePart.Create("image/jpeg", jpeg, jpegPath));

    Assert.Equal("image/png", ImageFormats.SniffMediaType(File.ReadAllBytes(pngPath)));
    Assert.Equal("image/jpeg", ImageFormats.SniffMediaType(File.ReadAllBytes(jpegPath)));
    Assert.Equal(".png", Path.GetExtension(pngPath));
    Assert.Equal(".jpg", Path.GetExtension(jpegPath));
  }

  [Fact]
  public void ReferencePart_ExtensionThatContradictsTheMediaType_IsRejected()
  {
    byte[] png = Convert.FromBase64String(Png3x2Base64);
    string path = StoreImage(png, "png");

    ArgumentException thrown = Assert.Throws<ArgumentException>(
        () => new MessagePart.ImageReferencePart(path, "image/jpeg"));

    Assert.Equal("mediaType", thrown.ParamName);
  }

  // --- Behaviour 4: the format is sniffed, never trusted ---

  [Fact]
  public void Create_ClaimedPngOverBytesThatAreNotAPng_FailsTypedNamingTheMediaType()
  {
    byte[] notAPng = "not a png"u8.ToArray();

    Result<MessagePart.ImageReferencePart> refused = MessagePart.ImageReferencePart.Create(
        "image/png", notAPng, Path.Combine(_imageRoot, "not-a-png.png"));

    Assert.False(refused.IsSuccess);
    Assert.Equal("UnsupportedImageFormat", refused.Error.Code);
    Assert.Contains("image/png", refused.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_ClaimThatContradictsTheSniffedBytes_FailsTypedNamingBoth()
  {
    byte[] png = Convert.FromBase64String(Png3x2Base64);

    Result<MessagePart.ImageReferencePart> refused = MessagePart.ImageReferencePart.Create(
        "image/jpeg", png, Path.Combine(_imageRoot, "mislabelled.jpg"));

    Assert.False(refused.IsSuccess);
    Assert.Equal("ImageMediaTypeMismatch", refused.Error.Code);
    Assert.Contains("image/jpeg", refused.Error.Message, StringComparison.Ordinal);
    Assert.Contains("image/png", refused.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_ZeroBytePayload_FailsTyped()
  {
    Result<MessagePart.ImageReferencePart> refused = MessagePart.ImageReferencePart.Create(
        "image/png", [], Path.Combine(_imageRoot, "empty.png"));

    Assert.False(refused.IsSuccess);
    Assert.Equal("InvalidImagePayload", refused.Error.Code);
  }

  [Fact]
  public void Create_PrefixOnlyPayload_FailsTyped()
  {
    byte[] magicOnly = [0x89, 0x50, 0x4E, 0x47];

    Result<MessagePart.ImageReferencePart> accepted = MessagePart.ImageReferencePart.Create(
        "image/png", magicOnly, Path.Combine(_imageRoot, "magic-only.png"));

    // The full 8-byte signature is the rule: a truncated prefix is not a PNG.
    Assert.False(accepted.IsSuccess);
    Assert.Equal("UnsupportedImageFormat", accepted.Error.Code);
  }

  // --- Behaviour 5: an oversize image is refused the same way ---

  [Fact]
  public void Create_PayloadOverTheSharedLimit_FailsTypedAndWritesNothing()
  {
    byte[] oversize = new byte[ImageLimits.MaxBytes + 1];
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(oversize, 0);
    string path = Path.Combine(_imageRoot, "oversize.png");

    Result<MessagePart.ImageReferencePart> refused = MessagePart.ImageReferencePart.Create(
        "image/png", oversize, path);

    Assert.False(refused.IsSuccess);
    Assert.Equal("ImageTooLarge", refused.Error.Code);
    Assert.Contains(ImageLimits.MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        refused.Error.Message, StringComparison.Ordinal);
    Assert.False(File.Exists(path), "a refused image is not written");
  }

  // --- Behaviour 6: the legacy form and the reference form coexist ---

  [Fact]
  public async Task LegacyInlineRow_StillReadsBackAsAnImagePartCarryingItsPayload()
  {
    AgentId id = await SavedAgentAsync();
    _ = await _store.AppendMessageAsync(id,
        new Message(Role.Tool, "older screenshot", At(1), ToolCallId: "call_1",
            Parts: [new MessagePart.ImagePart("image/png", "aGVsbG8=")]),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<IReadOnlyList<Message>> transcript = await new SqliteAgentStore(new AppDatabase(_dbPath))
        .GetTranscriptAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(transcript.IsSuccess, transcript.Error?.Message);
    MessagePart.ImagePart legacy = Assert.IsType<MessagePart.ImagePart>(
        Assert.Single(Assert.Single(transcript.Value).Parts!));
    Assert.Equal("image/png", legacy.MediaType);
    Assert.Equal("aGVsbG8=", legacy.Base64Data);
  }

  [Fact]
  public async Task LegacyInlineRowAndReferenceRow_LiveSideBySideInOneTranscript()
  {
    AgentId id = await SavedAgentAsync();
    byte[] png = Convert.FromBase64String(Png3x2Base64);
    string path = StoreImage(png, "png");
    MessagePart.ImageReferencePart reference = Must(MessagePart.ImageReferencePart.Create("image/png", png, path));

    _ = await _store.AppendMessageAsync(id,
        new Message(Role.Tool, "older screenshot", At(1), ToolCallId: "call_1",
            Parts: [new MessagePart.ImagePart("image/jpeg", "aGVsbG8=")]),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await _store.AppendMessageAsync(id,
        new Message(Role.Tool, "newer screenshot", At(2), ToolCallId: "call_2", Parts: [reference]),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<IReadOnlyList<Message>> transcript = await _store.GetTranscriptAsync(id,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(transcript.IsSuccess, transcript.Error?.Message);
    Assert.Equal(2, transcript.Value.Count);
    MessagePart.ImagePart legacy = Assert.IsType<MessagePart.ImagePart>(
        Assert.Single(transcript.Value[0].Parts!));
    Assert.Equal("aGVsbG8=", legacy.Base64Data);
    MessagePart.ImageReferencePart loaded = Assert.IsType<MessagePart.ImageReferencePart>(
        Assert.Single(transcript.Value[1].Parts!));
    Assert.Equal(path, loaded.Path);
  }

  [Fact]
  public async Task LegacyMetaJson_WithTheOldBase64DataShape_ReadsBackAsAnInlinePart()
  {
    AgentId id = await SavedAgentAsync();
    _ = await _store.AppendMessageAsync(id,
        new Message(Role.Tool, "older screenshot", At(1), ToolCallId: "call_1"),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The exact meta_json an older build wrote: a parts entry with MediaType and
    // Base64Data and NO path field at all.
    string legacyMetaJson = JsonSerializer.Serialize(new
    {
      Timestamp = At(1),
      ToolCalls = (object?)null,
      ToolCallId = "call_1",
      Parts = new[] { new { MediaType = "image/png", Base64Data = "aGVsbG8=" } },
    });
    Assert.DoesNotContain("Path", legacyMetaJson, StringComparison.Ordinal);
    RewriteMeta(id, legacyMetaJson);

    Result<IReadOnlyList<Message>> transcript = await _store.GetTranscriptAsync(id,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(transcript.IsSuccess, transcript.Error?.Message);
    MessagePart.ImagePart legacy = Assert.IsType<MessagePart.ImagePart>(
        Assert.Single(Assert.Single(transcript.Value).Parts!));
    Assert.Equal("aGVsbG8=", legacy.Base64Data);
  }

  /// <summary>Overwrites every message row's meta_json with <paramref name="metaJson"/>.</summary>
  private void RewriteMeta(AgentId id, string metaJson)
  {
    using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "UPDATE agent_messages SET meta_json=@m WHERE agent_id=@id;";
    _ = command.Parameters.AddWithValue("@m", metaJson);
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    _ = command.ExecuteNonQuery();
  }

  // --- Behaviour 7: unrepresentable parts stay a programmer error ---

  [Fact]
  public async Task UnrepresentablePart_StillThrows_RatherThanDroppingSilently()
  {
    AgentId id = await SavedAgentAsync();
    Message tool = new(Role.Tool, "captured", At(2), ToolCallId: "call_1",
        Parts: [new UnrepresentablePart()]);

    // ToMetaPart's default arm is reachable only from a part kind outside the vocabulary.
    _ = await Assert.ThrowsAsync<InvalidOperationException>(
        () => _store.AppendMessageAsync(id, tool, TestContext.Current.CancellationToken)).ConfigureAwait(true);

    Assert.Equal(0, CountMessages(id));
  }

  /// <summary>A part kind the meta_json vocabulary does not represent.</summary>
  private sealed record UnrepresentablePart : MessagePart;

  private int CountMessages(AgentId id)
  {
    using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM agent_messages WHERE agent_id=@id;";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }
}
