using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.FileSystem.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>A session's images have ONE store and ONE reachable folder, asserted through the
///     REAL composed container: the container resolves the store, the store writes under the
///     OS temp directory in the session's own folder, and the resolver the container builds
///     for read_image - the registered base resolver widened by the registered roots - opens
///     the absolute path the store reported, with nothing approved by the user.
///     <para>This is the contract both producers depend on: an image a tool produced and an
///     image a screenshot captured are the same kind of thing to the model, which reaches
///     either with read_image on a later turn. The reach is READ-ONLY: the app writes the
///     image, the tools read it.</para></summary>
[Trait("Category", "Process")]
public sealed class SessionImageStoreCompositionTests
{
  // A real 3x2 PNG: the store SNIFFS the bytes, so a signature-only fixture would not do.
  private const string Png3x2Base64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAIAAAASFvFNAAAAFElEQVR4nGP4z8DAAMZMEOo/AwMAO+wF/S5FekgAAAAASUVORK5CYII=";

  [Fact]
  public async Task Container_ResolvesOneStore_AndTheReadResolverReachesWhatItPersisted()
  {
    string workspace = TestTempRoot.CreateDirectory("ethang-imgstore-ws").FullName;
    string? written = null;
    try
    {
      using ServiceProvider services = Build(workspace);

      // ONE store: a producer that resolves this seam cannot land anywhere else.
      SessionImageStore store = Assert.IsType<SessionImageStore>(
          services.GetRequiredService<IImageStore>());

      Result<StoredImage> stored = await store.PersistBase64Async("image/png", Png3x2Base64,
          TestContext.Current.CancellationToken);
      Assert.True(stored.IsSuccess, stored.Error?.Message);
      written = stored.Value.Path;

      // The write side is OUTSIDE the workspace - the harness adds nothing to a directory it
      // was merely pointed at - and the report is absolute, which is what read_image needs.
      Assert.True(Path.IsPathRooted(written), "read_image needs an absolute path");
      Assert.False(written.StartsWith(workspace, StringComparison.OrdinalIgnoreCase),
          "a session image never lands in the user's workspace");
      Assert.StartsWith(SessionImageStore.ArtifactRoot, written, StringComparison.OrdinalIgnoreCase);
      Assert.True(File.Exists(written));

      // The read side, assembled from the container's OWN registrations: the registered base
      // resolver widened by the registered roots, which is exactly what read_image receives.
      TrustedDirectoryPathResolver resolver = new TrustedDirectoryPathResolver(
          services.GetRequiredService<IPathResolver>(),
          services.GetRequiredService<ResolvedTrustedDirectories>().WithStoredImages().List,
          grants: services.GetRequiredService<SessionPathGrants>()).AsReadOnlyResolver();
      ReadImageTool tool = new(resolver, services.GetRequiredService<IFileSystemAccess>(),
          new Vision(true));
      ToolResult result = await tool.ExecuteAsync(
          new RawToolInput("read_image", JsonSerializer.Serialize(new { timeoutSeconds = 60, path = written })),
          ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.False(result.IsError, result.Content);
      ToolResultImage attached = Assert.Single(result.Images!);
      Assert.Equal("image/png", attached.MediaType);
      Assert.Equal(Png3x2Base64, attached.Base64Data);
    }
    finally
    {
      if (written is not null && File.Exists(written))
      {
        File.Delete(written);
      }

      Directory.Delete(workspace, recursive: true);
    }
  }

  /// <summary>The reach the container adds is the STORE's OWN root, read-only and added
  ///     once: a rule for where an image lives spelled anywhere else could drift from the
  ///     folder the writer creates.</summary>
  [Fact]
  public void Reach_IsTheStoresOwnRoot_ReadOnly_AndIdempotent()
  {
    ResolvedTrustedDirectories approved = new([new TrustedDirectory(@"C:\approved")]);

    ResolvedTrustedDirectories widened = approved.WithStoredImages();

    TrustedDirectory added = Assert.Single(widened.List, d =>
        string.Equals(d.Root, SessionImageStore.ArtifactRoot, StringComparison.OrdinalIgnoreCase));
    Assert.False(added.Editable, "the app writes the image; the tools read it");
    // The approvals are untouched and keep their order.
    Assert.Equal(@"C:\approved", widened.List[0].Root);
    // Idempotent: the second widening is the same list, not a duplicate entry.
    Assert.Same(widened, widened.WithStoredImages());
  }

  private static ServiceProvider Build(string workspace)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new AgentRunOptions(null, 2));
    using TestAppDatabase db = TestAppDatabase.Create();
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(FixedWorkspaceContext.ForPath(workspace),
                new WorkspacePathResolver(workspace)),
            db.Database)
        // Nothing approved: the only reach the read tools get beyond the workspace is the
        // harness's own image folder, which is exactly what this test is about.
        .AddSingleton(new ResolvedTrustedDirectories([]))
        .BuildServiceProvider();
  }

  /// <summary>The vision answer this test states: the round trip is the subject, not the
  ///     withhold contract a text-only model gets.</summary>
  private sealed class Vision(bool accepts) : IImageInputCapability
  {
    public bool AcceptsImages => accepts;
  }
}
