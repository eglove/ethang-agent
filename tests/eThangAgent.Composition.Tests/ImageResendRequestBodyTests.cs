using System.Net;
using System.Net.Sockets;
using System.Text;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>THE REQUEST BODY, NOT THE DOMAIN OBJECT. The claim this plan makes is about what leaves
///     the process, so the check reads the bytes the composed provider actually POSTed over HTTP to
///     a local capture server - the real composition, the real encoder, the real serializer.
///     <para>An image the current turn produced rides as a data: URL; an image from an earlier turn
///     does not, and its message carries the marker that says a picture was there. Both facts are
///     asserted against the captured body, so a change that withheld the image from the domain
///     object but not from the wire (or the reverse) fails here.</para></summary>
[Trait("Category", "Process")]
public class ImageResendRequestBodyTests
{
  private const string EarlierPayload = "b2xkZXI="; // 'older'
  private const string CurrentPayload = "bmV3ZXI="; // 'newer'

  /// <summary>A local server that captures the BODY of the first POST to /api/v1/responses and
  ///     answers a minimal valid Responses-API response.</summary>
  private sealed class BodyCapturingServer : IDisposable
  {
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public Uri BaseUrl { get; private set; } = null!;

    public string? Body { get; private set; }

    public void Start()
    {
      using TcpListener probe = new(IPAddress.Loopback, 0);
      probe.Start();
      int port = ((IPEndPoint)probe.LocalEndpoint).Port;
      probe.Stop();
      BaseUrl = new Uri($"http://127.0.0.1:{port}/");
      _listener.Prefixes.Add(BaseUrl.AbsoluteUri);
      _listener.Start();
      _ = Task.Run(LoopAsync, _cts.Token);
    }

    private async Task LoopAsync()
    {
      while (!_cts.IsCancellationRequested)
      {
        HttpListenerContext ctx;
        try
        {
          ctx = await _listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (HttpListenerException)
        {
          break; // the listener was stopped: the fixture is being disposed
        }
        catch (ObjectDisposedException)
        {
          break;
        }

        if (ctx.Request.Url!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
        {
          using (StreamReader reader = new(ctx.Request.InputStream, Encoding.UTF8))
          {
            Body = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
          }

          byte[] reply = Encoding.UTF8.GetBytes(/*lang=json,strict*/"""{"output":[{"type":"message","content":[{"type":"output_text","text":"hi"}]}],"status":"completed","usage":{"input_tokens":1,"output_tokens":1}}""");
          ctx.Response.StatusCode = 200;
          ctx.Response.ContentType = "application/json";
          ctx.Response.ContentLength64 = reply.Length;
          await ctx.Response.OutputStream.WriteAsync(reply, _cts.Token).ConfigureAwait(false);
          ctx.Response.Close();
          continue;
        }

        if (ctx.Request.Url.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
        {
          byte[] models = Encoding.UTF8.GetBytes(/*lang=json,strict*/"""{"data":[]}""");
          ctx.Response.StatusCode = 200;
          ctx.Response.ContentType = "application/json";
          ctx.Response.ContentLength64 = models.Length;
          await ctx.Response.OutputStream.WriteAsync(models, _cts.Token).ConfigureAwait(false);
          ctx.Response.Close();
          continue;
        }

        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
      }
    }

    public void Dispose()
    {
      _cts.Cancel();
      _cts.Dispose();
      _listener.Stop();
      _listener.Close();
    }
  }

  private static ModelConfig Model => ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!;

  private static ModelRequest Request() => new(
  [
    new Message(Role.User, "start", DateTimeOffset.UtcNow),
    new Message(Role.Assistant, "", DateTimeOffset.UtcNow, [new ToolCall("c1", "computer", "{}")]),
    new Message(Role.Tool, "computer: observe", DateTimeOffset.UtcNow, ToolCallId: "c1",
        Parts: [new MessagePart.ImagePart("image/png", EarlierPayload)]),
    new Message(Role.User, "here is another", DateTimeOffset.UtcNow,
        Parts: [new MessagePart.ImagePart("image/png", CurrentPayload)]),
  ]);

  /// <summary>The composed provider's POSTed body carries the current turn's image and NOT the
  ///     earlier turn's, and the earlier one is recorded as withheld rather than dropped.</summary>
  [Fact]
  public async Task ThePostedBody_CarriesOnlyTheCurrentTurnsImage()
  {
    using BodyCapturingServer server = new();
    server.Start();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", server.BaseUrl),
        new AgentRunOptions(null, 2));
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter, Model,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
    IModelProvider provider = services.GetRequiredService<IModelProvider>();

    Result<ModelResponse> result = await provider.SendAsync(Model, Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    string body = Assert.IsType<string>(server.Body);
    // The current turn's picture left the process as a data: URL; the earlier one did not.
    Assert.Contains("data:image/png;base64," + CurrentPayload, body, StringComparison.Ordinal);
    Assert.DoesNotContain(EarlierPayload, body, StringComparison.Ordinal);
    Assert.Contains("[image not re-sent: image/png, 5 bytes", body, StringComparison.Ordinal);
  }
}
