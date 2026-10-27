using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Roslyn.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>THE IMAGE CHANNEL (issue #288), pinned end to end against the real pieces.
///     Collapsing to exec-only would have silently killed every image path - read_image,
///     docx extract, pdf render, computer screenshots, and issue #230's server-tool
///     rendering - unless a nested tool's ToolResult.Images can travel out of the run.
///     The chain these tests pin: a tool's ToolResult.Images -> AgentToolsProvider ->
///     CapabilityInvocationResult.Images -> ScriptGlobals collection -> ExecRunResult.Images
///     -> ExecResultFormatter -> ExecTool's ToolResult.Images -> Message.Parts.
///     <para>Nothing here is faked except the file BYTES and the vision answer: the exec
///     engine is the production Roslyn engine, the tools are the production tools, and the
///     final link is the exact part conversion the Agent loop performs (the shape
///     AgentImagePassThroughTests pins against the private helper).</para></summary>
public class ExecImageChannelTests
{
  // A real 1x1 PNG - survives ToolResultImage's base64 validation and the format sniff.
  private const string TinyPngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";

  private static readonly TimeSpan Budget = TimeSpan.FromSeconds(120);

  /// <summary>The production read_image tool over fixed bytes, with the vision answer the
  ///     test states: the file-read seam is covered by its own tests, and what THIS suite
  ///     is about is whether an image survives the trip out of exec.</summary>
  private sealed class FixedFiles(byte[] bytes) : IFileSystemAccess
  {
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default)
        => Task.FromResult(Result.Success(bytes));
  }

  private sealed class FixedVision(bool accepts) : IImageInputCapability
  {
    public bool AcceptsImages => accepts;
  }

  private static ReadImageTool ReadImage(bool vision) =>
      new(new UnrootedPathResolver(), new FixedFiles(Convert.FromBase64String(TinyPngBase64)),
          new FixedVision(vision));

  private static Task<ExecRunResult> RunAsync(ICapabilityProvider provider, string program)
  {
    CSharpScriptExecEngine engine = new(CapabilityRegistry.Create([provider]),
        workspaceRoot: () => AppContext.BaseDirectory);
    return engine.ExecuteAsync(new ExecProgram(program, Budget), TestContext.Current.CancellationToken);
  }

  /// <summary>The headline claim of the issue: a nested Tools.read_image inside exec
  ///     produces a ToolResult whose Images is non-empty, and the text NAMES it so the
  ///     model knows what it received.</summary>
  [Fact]
  public async Task Nested_ReadImage_Inside_Exec_Produces_Images_On_The_Tool_Result()
  {
    ExecRunResult run = await RunAsync(
        new AgentToolsProvider("agent", [new AgentToolBinding(ReadImage(vision: true), "Read one image file.")]),
        "return Tools.read_image(new { path = \"pic.png\" });").ConfigureAwait(true);

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Empty(run.ErrorLines);
    ToolResultImage image = Assert.Single(run.Images);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(TinyPngBase64, image.Base64Data);

    string content = ExecResultFormatter.Format(run).Content;
    Assert.Contains("[exec: 1 image(s) attached to this result as image parts", content, StringComparison.Ordinal);
    Assert.Contains("image/png", content, StringComparison.Ordinal);
  }

  /// <summary>And the attached image reaches Message.Parts as an ImagePart, through the
  ///     same conversion the production agent loop applies to every tool result.</summary>
  [Fact]
  public async Task Nested_Image_Reaches_Message_Parts_As_An_ImagePart()
  {
    ExecRunResult run = await RunAsync(
        new AgentToolsProvider("agent", [new AgentToolBinding(ReadImage(vision: true), "Read one image file.")]),
        "return Tools.read_image(new { path = \"pic.png\" });").ConfigureAwait(true);

    ToolResult result = ExecResultFormatter.Format(run);
    Conversation conversation = new();
    conversation.AddAssistantMessage("", [new ToolCall("c1", ExecTool.ToolName, "{}")]);
    conversation.AddToolResult("c1", result.Content, ToParts(result.Images));

    Message toolMessage = Assert.Single(conversation.Messages, m => m.Role == Role.Tool);
    MessagePart.ImagePart part = Assert.IsType<MessagePart.ImagePart>(Assert.Single(toolMessage.Parts!));
    Assert.Equal("image/png", part.MediaType);
    Assert.Equal(TinyPngBase64, part.Base64Data);
  }

  /// <summary>A text-only model keeps the withhold contract through the single door: the
  ///     annotation still comes back, no bytes are attached, and the line says why.</summary>
  [Fact]
  public async Task TextOnly_Model_Withholds_The_Image_And_Says_So()
  {
    ExecRunResult run = await RunAsync(
        new AgentToolsProvider("agent", [new AgentToolBinding(ReadImage(vision: false), "Read one image file.")]),
        "return Tools.read_image(new { path = \"pic.png\" });").ConfigureAwait(true);

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Empty(run.Images);
    Assert.Contains("image withheld", run.Output, StringComparison.Ordinal);
  }

  /// <summary>A run with no image-bearing dispatch carries none and says nothing about
  ///     images - the text-only result keeps its legacy shape.</summary>
  [Fact]
  public async Task Run_Without_Images_Carries_None_And_Says_Nothing()
  {
    ExecRunResult run = await RunAsync(
        new AgentToolsProvider("agent", [new AgentToolBinding(ReadImage(vision: true), "Read one image file.")]),
        "return 42;").ConfigureAwait(true);

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Empty(run.Images);
    Assert.Null(ExecResultFormatter.Format(run).Images);
    Assert.DoesNotContain("image", ExecResultFormatter.Format(run).Content, StringComparison.Ordinal);
  }

  /// <summary>The per-message bound is NAMED, never silent: a run that collects more images
  ///     than one message may carry serves the first four and says how many were dropped (a
  ///     silent loss would be the same lie as never carrying them).</summary>
  [Fact]
  public async Task Images_Past_The_Per_Message_Bound_Are_Dropped_And_Named()
  {
    int over = ImageLimits.MaxPerMessage + 2;
    ExecRunResult run = await RunAsync(
        new AgentToolsProvider("agent", [new AgentToolBinding(ReadImage(vision: true), "Read one image file.")]),
        "for (int i = 0; i < " + over.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        "; i++) { _ = Tools.read_image(new { path = \"pic.png\" }); } return \"done\";").ConfigureAwait(true);

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Equal(ImageLimits.MaxPerMessage, run.Images.Count);
    Assert.Equal(2, run.DroppedImageCount);

    string content = ExecResultFormatter.Format(run).Content;
    Assert.Contains("image(s) attached to this result as image parts", content, StringComparison.Ordinal);
    Assert.Contains("image(s) DROPPED", content, StringComparison.Ordinal);
  }

  /// <summary>The composition's own wiring: the container's read_image is reachable from
  ///     inside exec (it left the loop registry in issue #288), and a session with no
  ///     resolved model withholds the bytes rather than failing - the capability is wired,
  ///     the vision answer is conservative until a turn resolves a model.</summary>
  [Fact]
  public async Task Container_ReadImage_Is_Reachable_Inside_Exec()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();

    ExecTool tool = Assert.IsType<ExecTool>(services.GetRequiredService<ITool>());
    string json = "{\"timeoutSeconds\":120,\"title\":\"probe\",\"program\":" +
        System.Text.Json.JsonSerializer.Serialize("return Tools.Describe(\"read_image\");") + "}";

    ToolResult result = await tool.ExecuteAsync(new RawToolInput(ExecTool.ToolName, json),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Contains("read_image", result.Content, StringComparison.Ordinal);
    Assert.Contains("image", result.Content, StringComparison.Ordinal);
  }

  /// <summary>The Agent loop's own conversion (Agent.ToParts, private): a tool result's
  ///     images become conversation message parts, and null or empty stays null so a
  ///     text-only result keeps the legacy message shape.</summary>
  private static IReadOnlyList<MessagePart>? ToParts(IReadOnlyList<ToolResultImage>? images)
      => images is { Count: > 0 }
          ? [.. images.Select(i => new MessagePart.ImagePart(i.MediaType, i.Base64Data))]
          : null;
}
