using eThangAgent.AgentDomain;
using eThangAgent.AgentDomain.Specifications;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Start command of the spawn CQRS split: validates the request, persists a Running child,
///     and hands it to the runtime as an independent actor. Owns the validation/depth/model rules
///     that previously lived in SubAgentSpawner's synchronous path, the workspace-anchor
///     rule (validated against the parent's effective root, resolved path persisted on the contract),
///     and spawn-time worktree isolation (an IsolateInWorktree request is provisioned a fresh worktree
///     and anchored at it). When no explicit model is
///     provided and no session model preference or configured default is set, the
///     host-injected <see cref="SpawnOptions.FallbackModelId"/> serves.</summary>
public sealed class StartSpawnHandler(IAgentStore store, IAgentRuntime runtime, SubAgentOptions options,
    SpawnOptions spawn, IContextWindowSource? windowSource = null,
    IWorktreeProvisioner? worktrees = null, IModelCatalog? catalog = null)
    : IAgentSpawnCommand
{
  /// <summary>Outcome of one contract-resolution stage: the (possibly updated) contract,
  ///     or the failure that aborts the spawn. A null contract is a legitimate outcome —
  ///     an unanchored, grant-free request persists no contract at all.</summary>
  private sealed record ContractStage(SpawnContract? Contract, DomainError? Error)
  {
    public static ContractStage Keep(SpawnContract? contract) => new(contract, null);

    public static ContractStage Fail(DomainError error) => new(null, error);
  }

  private readonly SpawnOptions _spawn = spawn ?? throw new ArgumentNullException(nameof(spawn));
  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly IAgentRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
  private readonly SubAgentOptions _options = options ?? throw new ArgumentNullException(nameof(options));
  private readonly IContextWindowSource? _windowSource = windowSource;
  private readonly IWorktreeProvisioner? _worktrees = worktrees;
  private readonly IModelCatalog? _catalog = catalog;
  private readonly string _fallbackModelId = spawn?.FallbackModelId
      ?? throw new ArgumentNullException(nameof(spawn));

  private readonly NonEmptyTaskPromptSpecification _promptSpec = new();
  private readonly ValidModelReferenceSpecification _modelSpec = new();

  public async Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(parent);
    ArgumentNullException.ThrowIfNull(request);
    if (ValidateRequest(request) is { } violation)
    {
      return Result.Failure<AgentId>(new DomainError("InvalidSpawnRequest", violation.Message));
    }

    ContractStage grants = ResolveContractGrants(parent, request.Contract);
    if (grants.Error is { } grantsError)
    {
      return Result.Failure<AgentId>(grantsError);
    }

    // Spawn-time worktree isolation: a flagged request is provisioned a fresh worktree
    // inside the parent's effective root and anchored at the provisioned path — the
    // child's entire re-rooted surface (files, exec, git) lands in the worktree and its
    // commits land on the worktree's branch, never the controller's checkout. The flag
    // and an explicit anchor are contradictory: refused before anything happens.
    ContractStage isolation = request.IsolateInWorktree
        ? await ProvisionWorktreeAsync(parent, request, grants.Contract, ct).ConfigureAwait(false)
        : ContractStage.Keep(grants.Contract);
    if (isolation.Error is { } isolationError)
    {
      return Result.Failure<AgentId>(isolationError);
    }

    // Workspace anchor (worktree ladder, T5): an anchor on the request is validated
    // against the parent's effective root — the parent's OWN persisted anchor when its
    // contract carries one (grandchild chains measure against it), else the session
    // workspace — and the FULL resolved path is persisted on the contract so the record
    // stays self-describing for later enforcement. A null/whitespace anchor keeps the
    // byte-identical legacy path: no spec run, no persistence change. An isolated
    // request skips both: it was anchored at its freshly provisioned worktree above —
    // the worktree IS the confinement, and the provisioned path must stand.
    ContractStage anchored = request.IsolateInWorktree
        ? ContractStage.Keep(isolation.Contract)
        : ApplyAnchor(parent, request, isolation.Contract);
    if (anchored.Error is { } anchoredError)
    {
      return Result.Failure<AgentId>(anchoredError);
    }

    if (parent.Depth >= _options.MaxDepth)
    {
      return Result.Failure<AgentId>(new DomainError("DepthExceeded",
          $"agent depth {parent.Depth} is at the limit ({_options.MaxDepth}); children cannot spawn further"));
    }

    Result<ModelConfig> resolved = await ResolveModelAsync(request, ct).ConfigureAwait(false);
    return resolved.IsSuccess
      ? await PersistAndStartAsync(parent, request, anchored.Contract, resolved.Value, ct).ConfigureAwait(false)
      : Result.Failure<AgentId>(resolved.Error);
  }

  private Violation? ValidateRequest(SpawnRequest request)
      => _promptSpec.ViolationFor(request) ?? _modelSpec.ViolationFor(request);

  /// <summary>Grant validation + resolution (D9/A5, R1): privilege cannot grow down the
  ///     tree. The parent's effective set is its OWN persisted resolution when it carries
  ///     one (grandchild narrowing chains through it, R1.5); otherwise it is the session's
  ///     child tool surface. The child's effective set is resolved once, validated, and
  ///     PERSISTED on the contract — the record becomes self-describing, and the remote
  ///     ChildHost path (which rebuilds the spawner from settings, without SpawnOptions)
  ///     enforces identically.</summary>
  private ContractStage ResolveContractGrants(AgentRecord parent, SpawnContract? contract)
  {
    if (contract?.CapabilityGrants is not { } grants)
    {
      return ContractStage.Keep(contract);
    }

    ToolGrantPolicy grantPolicy = new(grants);
    if (!grantPolicy.HasGrants)
    {
      return ContractStage.Keep(contract.WithEffectiveTools(EffectiveSource(parent)));
    }

    IReadOnlySet<string> parentEffective = EffectiveSource(parent);
    IReadOnlyList<string> widening = grantPolicy.WideningViolations(parentEffective);
    return widening.Count > 0
      ? ContractStage.Fail(new DomainError("InvalidSpawnRequest",
          "capability grants widen beyond the parent's effective set: " + string.Join(", ", widening)))
      : ContractStage.Keep(contract.WithEffectiveTools(grantPolicy.EffectiveTools(parentEffective)));
  }

  /// <summary>Provisions the isolation worktree inside the parent's effective root and
  ///     anchors the contract at the provisioned path.</summary>
  private async Task<ContractStage> ProvisionWorktreeAsync(
      AgentRecord parent, SpawnRequest request, SpawnContract? contract, CancellationToken ct)
  {
    if (!string.IsNullOrWhiteSpace(request.WorkspaceRoot))
    {
      return ContractStage.Fail(new DomainError("InvalidSpawnRequest",
          "isolateInWorktree and workspaceRoot are mutually exclusive: pick one."));
    }

    if (_worktrees is null)
    {
      return ContractStage.Fail(new DomainError("InvalidSpawnRequest",
          "isolateInWorktree requires a worktree provisioner; none is wired."));
    }

    string? isolationRoot = parent.Contract is { } isoParentJson
        && SpawnContract.Decode(isoParentJson).WorkspaceRoot is { } isoParentAnchor
            ? isoParentAnchor
            : _spawn.WorkspaceRoot;
    if (string.IsNullOrWhiteSpace(isolationRoot))
    {
      return ContractStage.Fail(new DomainError("AnchorInvalid",
          WorkspaceAnchorSpecification.InvalidPrefix + " isolation requested but no session workspace and no parent anchor establish a root to provision inside."));
    }

    string worktreeName = DeriveWorktreeName(request.Label, request.TaskPrompt);
    Result<WorktreeProvision> provisioned = await _worktrees.CreateAsync(isolationRoot, worktreeName, ct).ConfigureAwait(false);
    return provisioned.IsSuccess
      ? ContractStage.Keep((contract ?? new SpawnContract()) with { WorkspaceRoot = provisioned.Value.Path })
      : ContractStage.Fail(provisioned.Error);
  }

  /// <summary>Applies the workspace-anchor rules to a non-isolated request: an explicit
  ///     anchor is validated against the parent's effective root; without one, an anchored
  ///     parent anchors the child by default (grandchild chains, spec decision 2) — the
  ///     inherited anchor is the parent's own canonical value; it was validated when the
  ///     parent spawned and is never re-validated here.</summary>
  private ContractStage ApplyAnchor(AgentRecord parent, SpawnRequest request, SpawnContract? contract)
      => !string.IsNullOrWhiteSpace(request.WorkspaceRoot)
          ? ValidateAnchor(parent, request, contract)
          : InheritAnchor(parent, contract);

  private ContractStage ValidateAnchor(AgentRecord parent, SpawnRequest request, SpawnContract? contract)
  {
    string? effectiveRoot = parent.Contract is { } parentContractJson
        && SpawnContract.Decode(parentContractJson).WorkspaceRoot is { } parentAnchor
            ? parentAnchor
            : _spawn.WorkspaceRoot;
    if (string.IsNullOrWhiteSpace(effectiveRoot))
    {
      // Named refusal, never silent inheritance: an anchor cannot be validated
      // without a root to measure containment against.
      return ContractStage.Fail(new DomainError("AnchorInvalid",
          WorkspaceAnchorSpecification.InvalidPrefix + " the anchor '" + request.WorkspaceRoot
              + "' cannot be validated: no session workspace and no parent anchor establish a root."));
    }

    WorkspaceAnchorSpecification anchorSpec = new(effectiveRoot);
    string anchor = request.WorkspaceRoot!;
    if (anchorSpec.ViolationFor(anchor) is { } anchorViolation)
    {
      string message = anchorViolation.Message;
      string code = message.StartsWith(WorkspaceAnchorSpecification.MissingPrefix, StringComparison.Ordinal)
          ? "AnchorMissing"
          : "AnchorInvalid";
      return ContractStage.Fail(new DomainError(code, message));
    }

    return ContractStage.Keep((contract ?? new SpawnContract()) with { WorkspaceRoot = ResolveAnchor(anchor) });
  }

  private static ContractStage InheritAnchor(AgentRecord parent, SpawnContract? contract)
      => parent.Contract is { } inheritedJson
          && SpawnContract.Decode(inheritedJson).WorkspaceRoot is { } inheritedAnchor
              ? ContractStage.Keep((contract ?? new SpawnContract()) with { WorkspaceRoot = inheritedAnchor })
              : ContractStage.Keep(contract);

  /// <summary>Persists the Running child record and hands it to the runtime.</summary>
  private async Task<Result<AgentId>> PersistAndStartAsync(AgentRecord parent, SpawnRequest request,
      SpawnContract? contract, ModelConfig modelConfig, CancellationToken ct)
  {
    // Persisted QUEUED, not Running (issue #238): the runtime is the only party that
    // knows the run actually began, and it owns the Queued -> Running transition. A
    // spawn parked at the concurrency boundary therefore never claims to be running, and
    // a reader can tell a parked child from a live one.
    AgentRecord record = AgentRecord.Spawned(AgentId.NewId(), parent.Id, parent.Depth + 1, modelConfig.ModelId,
        request.Label, request.TaskPrompt, DateTimeOffset.UtcNow, contract,
        provider: _spawn.ProviderName) with
    { Status = AgentStatus.Queued };

    Result<string> saved = await _store.SaveAsync(record, ct).ConfigureAwait(false);
    if (!saved.IsSuccess)
    {
      return Result.Failure<AgentId>(saved.Error);
    }

    Result<AgentId> started = await _runtime.Start(record, ct).ConfigureAwait(false);
    if (started.IsSuccess)
    {
      return Result.Success(record.Id);
    }

    // A REFUSED start must not leave the child parked forever (issue #238): the caller
    // gets the runtime's error verbatim, and the persisted row is terminalized so
    // agent.status / agent.result report a retrievable failure instead of a Queued row
    // no run will ever claim.
    _ = await _store.UpdateAsync(record with
    {
      Status = AgentStatus.Failed,
      FailureReason = AgentFailureReason.ProviderError,
      CompletedAt = DateTimeOffset.UtcNow,
      FinalReport = "Error [" + started.Error.Code + "]: the child run was never started: "
          + started.Error.Message,
    }, CancellationToken.None).ConfigureAwait(false);
    return Result.Failure<AgentId>(started.Error);
  }

  /// <summary>Canonical form of a resolved anchor: fully qualified with any trailing
  ///     separators removed — the same discipline the tool domain's workspace root uses,
  ///     so a persisted anchor never differs from a later candidate comparison by a
  ///     separator. A drive root ("C:\") would trim to "C:" which no longer refers to
  ///     the drive root; restore the separator so it stays meaningful.</summary>
  private static string ResolveAnchor(string anchor)
  {
    string full = Path.GetFullPath(anchor).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return full.Length == 2 && full[1] == ':' ? full + Path.DirectorySeparatorChar : full;
  }

  /// <summary>Derives a WorktreeName-conforming name for an isolated child: a label- or
  ///     prompt-derived slug (lowercase letters, digits, hyphens) truncated to leave
  ///     room for a uniqueness fragment, so concurrent isolated spawns of the same task
  ///     never collide (a collision is a WorktreeExists refusal, not a surprise).
  ///     Falls back to "spawn" when nothing survives shaping.</summary>
  private static string DeriveWorktreeName(string? label, string taskPrompt)
  {
    string source = string.IsNullOrWhiteSpace(label) ? taskPrompt : label;
    System.Text.StringBuilder sb = new();
    bool lastWasHyphen = false;
    foreach (char c in source)
    {
      char lower = char.ToLowerInvariant(c);
      bool ok = char.IsAsciiLetterOrDigit(lower);
      if (ok)
      {
        _ = sb.Append(lower);
        lastWasHyphen = false;
      }
      else if (!lastWasHyphen && sb.Length > 0)
      {
        _ = sb.Append('-');
        lastWasHyphen = true;
      }
    }

    string slug = sb.ToString().TrimEnd('-');
    if (slug.Length == 0)
    {
      slug = "spawn";
    }

    string fragment = Guid.NewGuid().ToString("N")[..8];
    int maxSlug = 64 - fragment.Length - 1;
    return slug.Length > maxSlug ? slug[..maxSlug].TrimEnd('-') + "-" + fragment : slug + "-" + fragment;
  }

  /// <summary>The parent's effective tool set, as R1 defines it: the parent's OWN
  ///     persisted resolution when its contract carries one (grandchild chains narrow
  ///     from it, R1.5); otherwise the session's child tool surface; otherwise empty
  ///     (any grant then fails widening — strict, never clamped).</summary>
  private IReadOnlySet<string> EffectiveSource(AgentRecord parent)
      => parent.Contract is { } parentContractJson
          && SpawnContract.Decode(parentContractJson).DecodedEffectiveTools is { } parentResolved
              ? parentResolved
              : _spawn.ChildToolSurface ?? new HashSet<string>(StringComparer.Ordinal);

  /// <summary>Resolves the child's model. The session's routing VARIANT is not consulted here:
  ///     it does not change which model a child runs, only how that model's endpoints are
  ///     ordered, so it reaches the child through the overlay in
  ///     <c>SubAgentSpawner.ChildConfigAsync</c> - applied to the child's OWN model.
  ///     <para>A child that silently landed on a different model than its parent chose would
  ///     make the session unreadable for the same reason a provider change would: the ledger's
  ///     prices and cache ratio belong to a model and a workload nobody chose.</para></summary>
  private async Task<Result<ModelConfig>> ResolveModelAsync(SpawnRequest request, CancellationToken ct)
  {
    // 1. Explicit per-spawn model always wins. Unknown window → failed spawn: the request
    //    named this model explicitly, so silently serving another would lie.
    if (!string.IsNullOrWhiteSpace(request.Model))
    {
      return await CreateAsync(request.Model, ct).ConfigureAwait(false);
    }

    // 2. The session's live model choice (the host's model picker) is session-wide:
    //    children follow it too, ahead of the static configured default.
    if (!string.IsNullOrWhiteSpace(_spawn.Preferences?.ModelId))
    {
      return await CreateAsync(_spawn.Preferences.ModelId, ct).ConfigureAwait(false);
    }

    // 3. Configured default model wins.
    if (!string.IsNullOrWhiteSpace(_options.DefaultModel))
    {
      return await CreateAsync(_options.DefaultModel, ct).ConfigureAwait(false);
    }

    // 4. Fallback to the host-injected fallback model.
    return await CreateAsync(_fallbackModelId, ct).ConfigureAwait(false);
  }

  /// <summary>The model's vision capability from the session catalog: any entry matching the
  ///     BASE model that accepts image input. A missing or failed catalog resolves false -
  ///     capability is never guessed, and one capable endpoint of a router is enough.</summary>
  private async Task<bool> AcceptsImagesAsync(string baseModel, CancellationToken ct)
  {
    if (_catalog is null)
    {
      return false;
    }

    Result<IReadOnlyList<ModelProviderEntry>> entries = await _catalog.GetAsync(ct).ConfigureAwait(false);
    return entries.IsSuccess && entries.Value.Any(e => e.ModelId == baseModel && e.SupportsVision);
  }

  /// <summary>Creates the config for an explicitly chosen model id; a model with no catalog
  ///     window fails the spawn (strict correctness - never guess a window).
  ///     <para>THE ID IS NORMALIZED TO THE BASE MODEL: a per-spawn model may carry a variant
  ///     suffix, and the catalog is keyed on the bare id - looking up the suffixed id would
  ///     find nothing and fail a spawn whose model exists.</para></summary>
  private async Task<Result<ModelConfig>> CreateAsync(string modelId, CancellationToken ct)
  {
    string baseModel = ModelVariants.BaseOf(modelId);
    int? window = _windowSource is null ? null : await _windowSource.WindowForAsync(baseModel, ct).ConfigureAwait(false);
    return window is { } resolved
        ? ModelConfig.Create(baseModel, _spawn.MaxTokens, _spawn.Temperature, resolved,
            acceptsImageInput: await AcceptsImagesAsync(baseModel, ct).ConfigureAwait(false))
        : Result.Failure<ModelConfig>(new DomainError("UnknownModelWindow",
            $"Model '{baseModel}' has no catalog context window; the spawn cannot proceed."));
  }
}
