using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

public interface IModelProvider
{
    Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default);

    /// <summary>
    /// Streaming variant of <see cref="SendAsync"/>: invokes <paramref name="onContentDelta"/>
    /// with each content fragment as it arrives and returns the fully assembled final
    /// response — the same value SendAsync produces for the same request. The default
    /// implementation delegates to SendAsync and emits no deltas, so providers and test
    /// fakes without streaming support keep working unchanged. Deltas are observational:
    /// failures still flow exclusively through the returned Result.
    /// </summary>
    Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
}
