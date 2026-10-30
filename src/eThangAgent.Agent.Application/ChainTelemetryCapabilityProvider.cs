using System.Globalization;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>The model-facing door over the chain telemetry report (issue #309): one action,
///     <c>telemetry</c>, which computes and renders what the persisted rows say about how chains
///     ran.
///     <para>WHY IT IS A SEPARATE PROVIDER FROM <c>chain</c>: the chain provider's action set is
///     the plan-to-execution transition (#313), and this is a READ over every chain in the
///     workspace — a different question with a different answer shape. Splitting keeps the
///     action surface honest about what each one does; a reader seeing <c>chain.start</c> and
///     <c>chain.telemetry</c> knows one writes and one measures.</para>
///     <para>IT REFUSES WITHOUT THRESHOLDS. The report cannot be computed until a thresholds
///     artifact exists, and the refusal NAMES the key to write — because the ordering D5 makes
///     checkable only holds if producing a report is impossible before the bounds are fixed.
///     </para>
///     <para>THE TITLE PARAMETER SELECTS WHICH THRESHOLDS GOVERN, since the artifact is keyed on
///     the chain's ledger slug. It is OPTIONAL and defaults to the workspace's only chain when
///     exactly one exists: a workspace running a single chain should not have to restate its
///     title to read its own telemetry, and a workspace with several is told to name one rather
///     than being silently measured against an arbitrary chain's bounds.</para></summary>
/// <param name="telemetry">The application service that gathers and computes.</param>
/// <param name="chains">The chain record, read only to resolve a default title.</param>
public sealed class ChainTelemetryCapabilityProvider(ChainTelemetryService telemetry,
    IChainStore chains) : ICapabilityProvider
{
  public const string ProviderId = "telemetry";

  /// <summary>The action ids this provider exposes, without constructing it — the grant surface
  ///     reads these at composition time, where resolving the provider itself would re-enter its
  ///     singleton.</summary>
  public static readonly string[] ActionNames = ["report"];

  private const string ChainParam = "chain";

  private readonly ChainTelemetryService _telemetry = telemetry
      ?? throw new ArgumentNullException(nameof(telemetry));
  private readonly IChainStore _chains = chains ?? throw new ArgumentNullException(nameof(chains));

  public string Id => ProviderId;

  public IReadOnlyList<ActionDescriptor> Actions { get; } =
  [
      new("report", "Report how chains actually ran, measured against thresholds fixed in advance.",
            """
            Computes the chain telemetry report from PERSISTED ROWS ONLY: the chain record's own status, attempts, review attempts and clocks, the usage ledger's per-call rows summed over the sessions a chain started, and the plan review record. Nothing is self-reported, so a session's own account of how it went is not an input.
            The report measures six things: steps completed per chain and how often a chain needed a human (intervention share); the park rate and its reason distribution; the retry rate per step and how often the retry cap was reached; the review deny rate and how often a deny produced a plan revision; cost per completed step against the subagent-era baseline; and the started-versus-settled ratio with wall clock against the same baseline.
            IT REFUSES WITHOUT THRESHOLDS, by design. The thresholds must be written to workspace state BEFORE the first report is computed, so that 'the bounds were fixed before the measurement' is checkable from the artifact's timestamp rather than asserted. The refusal names the key to write.
            A metric nothing in scope could measure reports NOT MEASURED, never 0%: an unmeasured value and a measured zero are different facts and the report keeps them apart. A baseline that cannot express a ratio reports INCOMPARABLE and gives the raw figure alone rather than asserting a comparison it could not compute.
            Output contract:
            [telemetry] <n> chain(s) in scope, <m> threshold(s) breached
              note: <a fact needed to read the numbers correctly>
              <metric-name>: <value> (threshold <bound>) [ok|BREACH|NOT MEASURED] - <the counts it came from>
            """,
            [new ActionParameter(ChainParam, ActionParameterTypes.StringType,
                "Optional. The chain whose thresholds artifact governs this report. Omit when the workspace holds exactly one chain.")],
            []),
  ];

  public async Task<CapabilityInvocationResult> InvokeAsync(string actionName, string jsonArguments,
      CancellationToken ct = default)
  {
    return actionName switch
    {
      "report" => await ReportAsync(jsonArguments, ct).ConfigureAwait(false),
      _ => CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."),
    };
  }

  /// <summary>Computes and renders the report for the resolved chain.</summary>
  private async Task<CapabilityInvocationResult> ReportAsync(string json, CancellationToken ct)
  {
    Result<string?> requested = OptChain(json);
    if (!requested.IsSuccess)
    {
      return CapabilityInvocationResult.Fail($"Error [{requested.Error.Code}]: {requested.Error.Message}");
    }

    Result<string> title = await ResolveTitleAsync(requested.Value, ct).ConfigureAwait(false);
    if (!title.IsSuccess)
    {
      return CapabilityInvocationResult.Fail($"Error [{title.Error.Code}]: {title.Error.Message}");
    }

    Result<ChainTelemetryReport> report = await _telemetry.BuildAsync(title.Value, ct)
        .ConfigureAwait(false);
    return report.IsSuccess
        ? CapabilityInvocationResult.Ok(report.Value.Render())
        : CapabilityInvocationResult.Fail($"Error [{report.Error.Code}]: {report.Error.Message}");
  }

  /// <summary>The chain whose thresholds govern, resolved from the argument or from the
  ///     workspace's only chain.
  ///     <para>MORE THAN ONE CHAIN WITH NO ARGUMENT IS A REFUSAL, not a guess: the thresholds are
  ///     per chain, and silently picking one would measure every chain against bounds the user
  ///     chose for a different one.</para></summary>
  private async Task<Result<string>> ResolveTitleAsync(string? requested, CancellationToken ct)
  {
    if (!string.IsNullOrWhiteSpace(requested))
    {
      return Result.Success(requested);
    }

    Result<ChainCatalog> catalog = await _chains.ListAsync(null, ct).ConfigureAwait(false);
    return catalog.IsSuccess ? SoleChain(catalog.Value) : Result.Failure<string>(catalog.Error);
  }

  /// <summary>The workspace's only chain's title, or the named refusal when there is none to
  ///     name or more than one to choose between.</summary>
  private static Result<string> SoleChain(ChainCatalog catalog) =>
      catalog.Chains.Count switch
      {
        0 => Result.Failure<string>(new DomainError("ChainNotFound",
            "this workspace holds no chain, so there is no thresholds artifact to read and nothing "
            + "to measure. Create one with chain.start, then write its thresholds key.")),
        1 => Result.Success(catalog.Chains[0].Title),
        _ => Result.Failure<string>(new DomainError("ChainAmbiguous",
            string.Create(CultureInfo.InvariantCulture, $"this workspace holds {catalog.Chains.Count} chains, each with its own thresholds artifact, so name one with 'chain'."))),
      };

  /// <summary>The optional <c>chain</c> argument, refused when present but not a non-empty
  ///     string — a caller that stated the member meant something by it.</summary>
  private static Result<string?> OptChain(string json)
  {
    JsonElement root;
    try
    {
      using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
      root = document.RootElement.Clone();
    }
    catch (JsonException ex)
    {
      return Result.Failure<string?>(new DomainError("InvalidActionInput",
          $"Arguments are not valid JSON: {ex.Message}"));
    }

    return root.ValueKind == JsonValueKind.Object
        ? ReadChain(root)
        : Result.Failure<string?>(new DomainError("InvalidActionInput",
            "Arguments must be a JSON object."));
  }

  /// <summary>Reads the one optional member, refusing an unknown parameter rather than ignoring
  ///     it: a caller that misspells a parameter should learn so, not read a report computed
  ///     from a different chain.</summary>
  private static Result<string?> ReadChain(JsonElement root)
  {
    string? value = null;
    foreach (JsonProperty property in root.EnumerateObject())
    {
      if (!string.Equals(property.Name, ChainParam, StringComparison.Ordinal))
      {
        return Result.Failure<string?>(new DomainError("InvalidActionInput",
            $"Unknown parameter '{property.Name}'."));
      }

      if (property.Value.ValueKind != JsonValueKind.String
          || string.IsNullOrWhiteSpace(property.Value.GetString()))
      {
        return Result.Failure<string?>(new DomainError("InvalidActionInput",
            $"'{ChainParam}' must be a non-empty string when present."));
      }

      value = property.Value.GetString()!.Trim();
    }

    return Result.Success(value);
  }
}
