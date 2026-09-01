using eThangAgent.AgentDomain;
using eThangAgent.AgentDomain.Specifications;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Start command of the spawn CQRS split: validates the request, persists a Running child,
///     and hands it to the runtime as an independent actor. Owns the validation/depth/model rules
///     that previously lived in SubAgentSpawner's synchronous path. When no explicit model is
///     provided and no session model preference is set, an available IModelSelector runs
///     intelligent model selection; the chain falls back to the host-injected
///     <see cref="SpawnOptions.FallbackModelId"/> on any selection failure.</summary>
public sealed class StartSpawnHandler(IAgentStore store, IAgentRuntime runtime, SubAgentOptions options,
    SpawnOptions spawn, IModelSelector? modelSelector = null, IContextWindowSource? windowSource = null)
    : IAgentSpawnCommand
{
  private readonly SpawnOptions _spawn = spawn ?? throw new ArgumentNullException(nameof(spawn));
  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly IAgentRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
  private readonly SubAgentOptions _options = options ?? throw new ArgumentNullException(nameof(options));
  private readonly IModelSelector? _modelSelector = modelSelector;
  private readonly IContextWindowSource? _windowSource = windowSource;
  private readonly string _fallbackModelId = spawn?.FallbackModelId
      ?? throw new ArgumentNullException(nameof(spawn));

  private readonly NonEmptyTaskPromptSpecification _promptSpec = new();
  private readonly ValidModelReferenceSpecification _modelSpec = new();

  public async Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(parent);
    ArgumentNullException.ThrowIfNull(request);
    Violation? violation = _promptSpec.ViolationFor(request) ?? _modelSpec.ViolationFor(request);
    if (violation is not null)
    {
      return Result.Failure<AgentId>(new DomainError("InvalidSpawnRequest", violation.Message));
    }

    // Grant validation (D9/A5): privilege cannot grow down the tree. A spawn whose
    // allow-list names tools outside the parent's effective set fails strictly.
    if (request.Contract?.CapabilityGrants is { } grants)
    {
      ToolGrantPolicy grantPolicy = new(grants);
      if (grantPolicy.HasGrants)
      {
        IReadOnlySet<string> parentEffective = _spawn.ChildToolSurface ?? new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string> widening = grantPolicy.WideningViolations(parentEffective);
        if (widening.Count > 0)
        {
          return Result.Failure<AgentId>(new DomainError("InvalidSpawnRequest",
              "capability grants widen beyond the parent's effective set: " + string.Join(", ", widening)));
        }
      }
    }

    if (parent.Depth >= _options.MaxDepth)
    {
      return Result.Failure<AgentId>(new DomainError("DepthExceeded",
          $"agent depth {parent.Depth} is at the limit ({_options.MaxDepth}); children cannot spawn further"));
    }

    Result<ModelConfig> resolved = await ResolveModelAsync(request, ct).ConfigureAwait(false);
    if (!resolved.IsSuccess)
    {
      return Result.Failure<AgentId>(resolved.Error);
    }

    ModelConfig modelConfig = resolved.Value;

    AgentRecord record = AgentRecord.Spawned(AgentId.NewId(), parent.Id, parent.Depth + 1, modelConfig.ModelId,
        request.Label, request.TaskPrompt, DateTimeOffset.UtcNow);

    Result<string> saved = await _store.SaveAsync(record, ct).ConfigureAwait(false);
    if (!saved.IsSuccess)
    {
      return Result.Failure<AgentId>(saved.Error);
    }

    Result<AgentId> started = await _runtime.Start(record, ct).ConfigureAwait(false);
    return started.IsSuccess
        ? Result.Success(record.Id)
        : Result.Failure<AgentId>(started.Error);
  }

  private async Task<Result<ModelConfig>> ResolveModelAsync(SpawnRequest request, CancellationToken ct)
  {
    // 1. Explicit per-spawn model always wins. Unknown window → failed spawn: the
    //    request named this model explicitly, so silently serving another would lie.
    if (!string.IsNullOrWhiteSpace(request.Model))
    {
      return await CreateAsync(request.Model, null, ct).ConfigureAwait(false);
    }

    // 2. The session's live model choice (the host's model picker) is session-wide:
    //    children follow it too, ahead of the static configured default.
    if (!string.IsNullOrWhiteSpace(_spawn.Preferences?.ModelId))
    {
      return await CreateAsync(_spawn.Preferences.ModelId, null, ct).ConfigureAwait(false);
    }

    // 3. Configured default model wins.
    if (!string.IsNullOrWhiteSpace(_options.DefaultModel))
    {
      return await CreateAsync(_options.DefaultModel, null, ct).ConfigureAwait(false);
    }

    // 4. Intelligent selection when a selector is available; a selection whose model
    //    has no window falls through to the fallback (same chain as selection failure).
    if (_modelSelector is not null)
    {
      Result<ModelSelectionResult> selection = await _modelSelector.SelectAsync(request.TaskPrompt, excludedKeys: null, ct).ConfigureAwait(false);
      if (selection.IsSuccess)
      {
        int? selectedWindow = _windowSource is null
            ? null
            : await _windowSource.WindowForAsync(selection.Value.ModelId, selection.Value.ProviderName, ct).ConfigureAwait(false);
        if (selectedWindow is { } selected)
        {
          return ModelConfig.Create(selection.Value.ModelId, selection.Value.ProviderName,
              _spawn.MaxTokens, _spawn.Temperature, selected);
        }
      }
    }

    // 5. Fallback to the host-injected fallback model.
    return await CreateAsync(_fallbackModelId, null, ct).ConfigureAwait(false);
  }

  /// <summary>Creates the config for an explicitly chosen model id; a model with no
  /// catalog window fails the spawn (strict correctness — never guess a window).</summary>
  private async Task<Result<ModelConfig>> CreateAsync(string modelId, string? providerName, CancellationToken ct)
  {
    int? window = _windowSource is null ? null : await _windowSource.WindowForAsync(modelId, providerName, ct).ConfigureAwait(false);
    return window is { } resolved
        ? ModelConfig.Create(modelId, providerName, _spawn.MaxTokens, _spawn.Temperature, resolved)
        : Result.Failure<ModelConfig>(new DomainError("UnknownModelWindow",
            $"Model '{modelId}' has no catalog context window; the spawn cannot proceed."));
  }
}
