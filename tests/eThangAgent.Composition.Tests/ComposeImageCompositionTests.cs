using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>compose_image draws a picture inside this process and leaves a real PNG on disk, over
///     the REAL composed container and through exec - the surface the model actually calls.
///     <para>IT ASSERTS A FILE AND ITS SIZE, not a status string: the whole point of the tool is
///     that an image a session needed exists afterwards as bytes it can hand to a build, without
///     a model call, a provider, a key, or a network. A text-only model is the case pinned here
///     because it is the one that cannot fall back to a provider's image tool at all.</para></summary>
[Trait("Category", "Process")]
public class ComposeImageCompositionTests
{
  private static ServiceProvider Build()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new AgentRunOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  [Fact]
  public async Task ComposeImage_ThroughExec_WritesARealPngOfTheStatedSize()
  {
    using ServiceProvider services = Build();
    IExecEngine engine = services.GetRequiredService<IExecEngine>();

    string scene = JsonSerializer.Serialize(new
    {
      timeoutSeconds = 60,
      width = 400,
      height = 200,
      background = "#0C0E14",
      shapes = new object[]
      {
        new { kind = "rectangle", x = 20, y = 20, width = 160, height = 100, fill = "#E65A3C" },
        new { kind = "ellipse", x = 220, y = 40, width = 120, height = 120, fill = "#3E5C86" },
        new { kind = "line", from = new { x = 20, y = 150 }, to = new { x = 380, y = 150 },
          stroke_width = 3, stroke_color = "#FFFFFF" },
        new { kind = "text", text = "composed locally", x = 20, y = 168, font_size = 18,
          color = "#FFFFFF" },
      },
    });

    ExecRunResult run = await engine.ExecuteAsync(
        new ExecProgram(Invocation(scene), TimeSpan.FromSeconds(120)),
        ct: TestContext.Current.CancellationToken);

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Empty(run.ErrorLines);
    // The annotation names the path, the media type, and the size it actually rendered.
    Assert.Contains("[compose_image ", run.Output, StringComparison.Ordinal);
    Assert.Contains("image/png, 400x200", run.Output, StringComparison.Ordinal);

    string path = PathOf(run.Output);
    Assert.True(Path.IsPathRooted(path), $"the annotation must name an absolute path, got '{path}'");
    Assert.True(File.Exists(path), $"the tool named '{path}', which does not exist");

    byte[] bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
  }

  [Fact]
  public async Task ComposeImage_AShapeThatCannotBeDrawn_IsRefused()
  {
    // A rectangle with neither fill nor stroke paints nothing. The boundary says so, naming the
    // shape index, instead of writing an empty canvas and calling it success.
    using ServiceProvider services = Build();
    IExecEngine engine = services.GetRequiredService<IExecEngine>();
    string scene = JsonSerializer.Serialize(new
    {
      timeoutSeconds = 60,
      width = 100,
      height = 100,
      shapes = new object[] { new { kind = "rectangle", x = 0, y = 0, width = 50, height = 50 } },
    });

    ExecRunResult run = await engine.ExecuteAsync(
        new ExecProgram(Invocation(scene), TimeSpan.FromSeconds(120)),
        ct: TestContext.Current.CancellationToken);

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Contains("Error [InvalidParameterValue]", run.Output, StringComparison.Ordinal);
    // The refusal names WHICH shape and WHY, which is the whole point of validating at the boundary.
    Assert.Contains("shape 0", run.Output, StringComparison.Ordinal);
    Assert.Contains("fill", run.Output, StringComparison.Ordinal);
  }

  /// <summary>An exec program that dispatches compose_image with <paramref name="scene"/> as
  ///     its arguments. The document is passed as a JSON STRING, which the bridge accepts
  ///     verbatim - C# object literals cannot express it, and a script that is written by
  ///     pasting JSON would be a different test than the one a model writes.</summary>
  private static string Invocation(string scene) =>
      "return Tools.Invoke(\"compose_image\", @\"" + scene.Replace("\"", "\"\"", StringComparison.Ordinal)
      + "\");";

  /// <summary>The absolute path the annotation named.</summary>
  private static string PathOf(string output)
  {
    string trimmed = output.Trim();
    int start = trimmed.IndexOf("[compose_image ", StringComparison.Ordinal)
        + "[compose_image ".Length;
    int end = trimmed.IndexOf(' ', start, StringComparison.Ordinal);
    return trimmed[start..end];
  }
}
