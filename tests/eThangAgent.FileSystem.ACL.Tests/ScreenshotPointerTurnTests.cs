using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>ASSERTION 1 OF THE PLAN'S VERIFICATION: a NEW screenshot persists as a POINTER.
///     <para>WHY THIS FILE EXISTS AT ALL. Every other check of the pointer form starts at the
///     STORE: it hands a message to <see cref="SqliteAgentStore.AppendMessageAsync"/> and reads
///     the row back. That proves the store converts a payload, and it proves nothing about the
///     path a real screenshot takes - the computer tool builds a <see cref="ToolResultImage"/>,
///     the loop turns it into a conversation part, and the transcript sink persists it. A
///     regression anywhere on that path (a tool that stopped attaching an image, a loop that
///     stopped forwarding parts, a sink that dropped them) would leave every store-level test
///     green while the transcript went back to carrying bytes. So this drives the WHOLE turn: a
///     real <see cref="Agent"/> over a real <see cref="RootTranscriptSink"/> over a real
///     <see cref="SqliteAgentStore"/> over a real <see cref="SessionImageStore"/>, with the
///     computer seam scripted to return a screenshot.</para>
///     <para>THE DATABASE IS A FILE, NOT :memory:. The row is read back through a second
///     connection to the same file, which is what a poll after a restart does.</para></summary>
public sealed class ScreenshotPointerTurnTests : IDisposable
{
  /// <summary>The 8-byte PNG signature: what "the file is a real image" means here.</summary>
  private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

  private readonly string _dbPath = TemplateDatabase.Copy();
  private readonly string _base = TestTempRoot.CreateDirectory("ethang-shot-turn").FullName;
  private readonly string _session = "shot-turn-" + Guid.NewGuid().ToString("N");

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

  private SessionImageStore Images => new(_session, _base);

  private string SessionFolder => Path.Combine(SessionImageStore.ArtifactRootUnder(_base), _session);

  private static ModelConfig Config => ModelConfig.Create("test-model", 100, 0.5f, 8192).Value!;

  /// <summary>A PNG-shaped payload of exactly <paramref name="size"/> bytes. The store SNIFFS
  ///     the bytes, so a signature-only fixture is admitted exactly as a real capture is.</summary>
  private static byte[] Png(int size)
  {
    byte[] bytes = new byte[size];
    PngSignature.CopyTo(bytes, 0);
    return bytes;
  }

  /// <summary>Two scripted responses: the tool call, then a plain answer.</summary>
  private sealed class ScriptedProvider(Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public List<ModelRequest> RequestsSeen { get; } = [];

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
    {
      RequestsSeen.Add(request);
      return Task.FromResult(_responses.Count > 0
          ? _responses.Dequeue()
          : Result.Success(new ModelResponse("done", [])));
    }
  }

  /// <summary>The computer seam, scripted: an observe whose screenshot is the real thing.</summary>
  private sealed class ScreenshotSeam(ToolResultImage shot) : IComputerAccess
  {
    public Task<ComputerOutcome> ExecuteAsync(ComputerCommand command, CancellationToken ct = default) =>
        Task.FromResult<ComputerOutcome>(new ComputerOutcome.Observation(
            "# tree", "state-1", [], new ComputerFrameRef("frame-1", 800, 600), null, shot));
  }

  private sealed class Vision : IImageInputCapability
  {
    public bool AcceptsImages => true;
  }

  /// <summary>An observe that asks for a screenshot - the shape the model actually sends. Built
  ///     from typed values rather than a JSON literal, because a string that IS json trips the
  ///     analyzer that refuses probable JSON in source.
  ///     </summary>
  private static readonly string ObserveCall = JsonSerializer.Serialize(new
  {
    timeoutSeconds = 120,
    action = "observe",
    app_ref = new { name = "Notepad" },
    include_screenshot = true,
  });

  private async Task<AgentId> SavedAgentAsync()
  {
    AgentId id = AgentId.NewId();
    SqliteAgentStore store = new(new AppDatabase(_dbPath));
    _ = await store.SaveAsync(AgentRecord.Started(id, "provider/model", null, "task",
        new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    return id;
  }

  /// <summary>Runs one whole turn in which the computer tool captures a screenshot of
  ///     <paramref name="screenshot"/> and returns the row the transcript holds for it.</summary>
  private async Task<(string MetaJson, string Content)> TurnCapturingAsync(byte[] screenshot)
  {
    AgentId id = await SavedAgentAsync().ConfigureAwait(true);
    SqliteAgentStore store = new(new AppDatabase(_dbPath), imagesFor: _ => Images);
    Conversation conversation = new();
    RootTranscriptSink sink = new(store, conversation, () => id);
    ScriptedProvider provider = new([
      Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "computer", ObserveCall)],
          FinishReason.ToolCalls)),
      Result.Success(new ModelResponse("done", [])),
    ]);
    ComputerTool computer = new(
        new ScreenshotSeam(new ToolResultImage("image/png", Convert.ToBase64String(screenshot))),
        new Vision());
    Agent agent = new(provider, conversation, Config, new ToolRegistry([computer]),
        new AgentOptions { Id = id, TranscriptSink = sink });

    Result<string> turn = await agent.SendMessage("take a screenshot",
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(turn.IsSuccess, turn.Error?.Message);
    // The sink persists at the safe point the message is added; the flush is what makes the
    // row readable here, and it is the same call the container's shutdown makes.
    await sink.FlushAsync().ConfigureAwait(true);
    return ToolRow(id);
  }

  /// <summary>The tool message's row, read through a SECOND connection to the same file - the
  ///     read a poll performs, not the writer's own view.</summary>
  private (string MetaJson, string Content) ToolRow(AgentId id)
  {
    using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT meta_json, content FROM agent_messages "
        + "WHERE agent_id=@id AND role='Tool' ORDER BY seq DESC LIMIT 1;";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    using SqliteDataReader reader = command.ExecuteReader();
    Assert.True(reader.Read(), "the turn persisted no tool message at all");
    return (reader.GetString(0), reader.GetString(1));
  }

  // --- ASSERTION 1: a new screenshot persists as a pointer ---

  /// <summary>A turn that captures a 9 MB screenshot leaves a row with NO Base64Data, under
  ///     1,024 bytes, naming a file that exists and really is a PNG. The 9 MB is the size the
  ///     plan measured on real sessions (the heaviest carried 7.4 MB of embedded images), so the
  ///     fixture is the case that mattered rather than a toy.</summary>
  [Fact]
  public async Task ATurnThatCapturesAScreenshot_PersistsAPointer_UnderAKilobyte_AndTheFileIsARealPng()
  {
    byte[] screenshot = Png(9 * 1024 * 1024);

    (string metaJson, string content) = await TurnCapturingAsync(screenshot).ConfigureAwait(true);

    Assert.DoesNotContain("Base64Data", metaJson, StringComparison.Ordinal);
    Assert.True(metaJson.Length < 1024,
        $"a screenshot's row stays under 1,024 bytes; this one is {metaJson.Length}: {metaJson}");

    using JsonDocument document = JsonDocument.Parse(metaJson);
    JsonElement entry = document.RootElement.GetProperty("Parts")[0];
    Assert.Equal("image/png", entry.GetProperty("MediaType").GetString());
    string path = entry.GetProperty("Path").GetString()!;

    // The reference names a file that EXISTS, and the file is the picture.
    Assert.True(File.Exists(path), $"the row names '{path}', which no file backs");
    Assert.Equal(".png", Path.GetExtension(path));
    byte[] stored = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.Equal(screenshot.Length, stored.Length);
    Assert.True(stored.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature),
        $"the stored file does not begin with the PNG signature: {path}");
    Assert.Equal(screenshot, stored);

    // The message's own text names the file, so a later turn can pull the picture back with
    // read_image instead of being handed bytes it already paid for.
    Assert.Contains(path, content, StringComparison.Ordinal);
    // And the pointer is the ONLY thing in the session folder: one capture, one file.
    Assert.Equal(path, Assert.Single(Directory.GetFiles(SessionFolder)));
  }

  /// <summary>The awkward case, stated rather than hidden: the pointer rule is MARGINAL. A
  ///     screenshot under <see cref="ImageLimits.InlineThresholdBytes"/> keeps the inline form
  ///     and writes no file, so the change is to the size that lands in a row and not to whether
  ///     an image is stored at all.</summary>
  [Fact]
  public async Task AScreenshotUnderTheThreshold_StaysInline_AndWritesNoFile()
  {
    byte[] small = Png(ImageLimits.InlineThresholdBytes - 1);

    (string metaJson, _) = await TurnCapturingAsync(small).ConfigureAwait(true);

    Assert.Contains("Base64Data", metaJson, StringComparison.Ordinal);
    Assert.DoesNotContain("\"Path\"", metaJson, StringComparison.Ordinal);
    Assert.False(Directory.Exists(SessionFolder),
        "a payload under the threshold never becomes a file");
  }
};
