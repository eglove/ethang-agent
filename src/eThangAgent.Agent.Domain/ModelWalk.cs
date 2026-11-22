using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>One attempt a turn may make: the model, the vendor that serves it, and the
///     provider resolved for that vendor.</summary>
/// <param name="ModelId">The base catalog model id, without a routing-variant suffix.</param>
/// <param name="Vendor">The vendor id that serves it.</param>
/// <param name="Provider">The provider bound to the attempt's config.</param>
public sealed record ModelWalkAttempt(string ModelId, string? Vendor, IModelProvider Provider);

/// <summary>What one advance of a walk produced: an attempt to make, an entry that could not be
///     resolved (the walk may continue), or the spent state (every entry has been attempted).
///     <para>THE THREE OUTCOMES ARE SEPARATE BECAUSE THEY ARE HANDLED DIFFERENTLY. An entry that
///     cannot be resolved is recorded and walked past; the spent state ends the turn and its
///     message names every model attempted. Folding them together would either end a turn with
///     entries left unattempted or walk past a list that is already exhausted.</para></summary>
/// <param name="Attempt">The attempt to make, or null.</param>
/// <param name="Refusal">Why the entry could not be resolved, or the spent message.</param>
/// <param name="Spent">True when every entry has been attempted.</param>
public sealed record ModelWalkStep(ModelWalkAttempt? Attempt, DomainError? Refusal, bool Spent)
{
  /// <summary>The step that walks past an entry nobody can serve.</summary>
  public static ModelWalkStep Refused(DomainError refusal) => new(null, refusal, false);

  /// <summary>The step that ends the turn: the list is spent.</summary>
  public static ModelWalkStep Exhausted(DomainError spent) => new(null, spent, true);

  /// <summary>The step that makes an attempt.</summary>
  public static ModelWalkStep Entered(ModelWalkAttempt attempt) => new(attempt, null, false);
}

/// <summary>The ordered models ONE TURN walks: each entry is attempted at most once, in list
///     order, and the turn advances to the next entry only when the current one returns a
///     verdict that makes it unusable for the rest of the turn.
///     <para>THE HARNESS OWNS THE FALL-THROUGH. One attempt names exactly one model, so a
///     provider never receives a list; the walk is what turns a refusal into the next attempt.
///     </para>
///     <para>AN ENTRY IS SPENT BY A DROP VERDICT AND BY NOTHING ELSE. A transient fault is
///     retried on the SAME model by <see cref="TurnRetryPolicy"/>, and a context overflow runs
///     its own recovery on the same model, so neither may advance the walk — advancing would
///     spend an entry on a failure the turn had already recovered from.</para>
///     <para>A REFUSED CREDENTIAL DOES NOT ADVANCE EITHER. <c>ProviderUnauthorized</c> is a
///     configuration fault about the account, so the turn stops and reports the provider's own
///     text rather than walking past it.</para></summary>
public sealed class ModelWalk
{
  /// <summary>The error code an empty model list reports.</summary>
  public const string NoModelsCode = "NoModelsToWalk";

  /// <summary>The error code an entry that names no resolvable vendor reports.</summary>
  public const string NoVendorCode = "NoModelVendor";

  private readonly ModelSelectionEntry[] _entries;
  private readonly string? _sessionVendor;
  private readonly List<ModelWalkFailure> _failures = [];
  private int _index = -1;

  private ModelWalk(ModelSelectionEntry[] entries, string? sessionVendor)
  {
    _entries = entries;
    _sessionVendor = string.IsNullOrWhiteSpace(sessionVendor) ? null : sessionVendor;
  }

  /// <summary>Builds the turn's walk over <paramref name="entries"/>, in list order.
  ///     <para>AN ENTRY THAT NAMES NO VENDOR FALLS BACK TO THE SESSION'S OWN BINDING, which is
  ///     what every selection written before vendors existed means: the session recorded which
  ///     provider serves it, and an entry that says nothing inherits that.</para>
  ///     <para>AN EMPTY LIST IS A NAMED FAILURE, not an empty walk: a turn with no model cannot
  ///     be served, and reporting it here names the fault instead of failing every attempt.
  ///     </para></summary>
  /// <param name="entries">The ordered model+vendor pairs to walk.</param>
  /// <param name="sessionVendor">The provider id the session recorded, used by an entry that
  ///     names none.</param>
  public static Result<ModelWalk> Create(IReadOnlyList<ModelSelectionEntry>? entries,
      string? sessionVendor)
  {
    ModelSelectionEntry[] usable = [.. (entries ?? []).Where(entry => entry is not null)];
    return usable.Length == 0
        ? Result.Failure<ModelWalk>(new DomainError(NoModelsCode,
            "the session's model list is empty, so this turn has no model to attempt."))
        : Result.Success(new ModelWalk(usable, sessionVendor));
  }

  /// <summary>The model the turn is attempting now, or null before the first advance.</summary>
  public string? CurrentModelId =>
      _index >= 0 && _index < _entries.Length ? _entries[_index].ModelId : null;

  /// <summary>The vendor serving the model the turn is attempting now, or null when none was
  ///     named by the entry or the session.</summary>
  public string? CurrentVendor =>
      _index >= 0 && _index < _entries.Length ? _entries[_index].Vendor ?? _sessionVendor : null;

  /// <summary>The models this turn has attempted and dropped, in the order it attempted them.
  ///     </summary>
  public IReadOnlyList<string> Attempted => [.. _failures.Select(failure => failure.ModelId)];

  /// <summary>How many entries this turn has not attempted yet.</summary>
  public int Remaining => Math.Max(0, _entries.Length - _index - 1);

  /// <summary>Advances to the next entry and resolves the provider its vendor names.
  ///     <para>THE PROVIDER IS RESOLVED PER ATTEMPT, from the vendor the entry names, so a
  ///     vendor whose credential is missing yields a refusal for THAT entry instead of being
  ///     skipped silently or served by another vendor's transport.</para></summary>
  /// <param name="config">The turn's resolved config; the attempt's model id replaces its own.
  ///     </param>
  /// <param name="resolver">The vendor-keyed provider resolution.</param>
  public ModelWalkStep Next(ModelConfig config, IModelProviderResolver resolver)
  {
    ArgumentNullException.ThrowIfNull(config);
    ArgumentNullException.ThrowIfNull(resolver);
    _index++;
    if (_index >= _entries.Length)
    {
      return ModelWalkStep.Exhausted(SpentError());
    }

    ModelSelectionEntry entry = _entries[_index];
    string? vendor = entry.Vendor ?? _sessionVendor;
    if (vendor is null)
    {
      return ModelWalkStep.Refused(new DomainError(NoVendorCode,
          $"the model '{entry.ModelId}' names no provider and this session recorded none, so "
          + "no provider can serve it."));
    }

    Result<IModelProvider> provider = resolver.Resolve(vendor, ConfigFor(config, entry.ModelId));
    return provider.IsSuccess
        ? ModelWalkStep.Entered(new ModelWalkAttempt(entry.ModelId, vendor, provider.Value))
        : ModelWalkStep.Refused(provider.Error);
  }

  /// <summary>Records that the entry just attempted cannot serve this turn, with the reason it
  ///     gave. The recorded reasons are what the spent turn's message states.</summary>
  /// <param name="error">The failure the attempt returned.</param>
  public void Drop(DomainError error)
  {
    ArgumentNullException.ThrowIfNull(error);
    if (_index >= 0 && _index < _entries.Length)
    {
      _failures.Add(new ModelWalkFailure(_entries[_index].ModelId, CurrentVendor, error.Code,
          error.Message));
    }
  }

  /// <summary>The failure a spent walk reports: ONE message naming every model attempted and
  ///     the reason each gave, carrying the last attempt's own code so a reader that classifies
  ///     by code still sees what the final model said.
  ///     <para>IT IS NEVER PRODUCED WHILE AN ENTRY REMAINS UNATTEMPTED: the walk only reaches
  ///     this state after <see cref="Next"/> has consumed the last entry.</para></summary>
  public DomainError SpentError() => new(
      _failures.Count == 0 ? NoModelsCode : _failures[^1].Code, DescribeAttempts());

  /// <summary>Every model this turn attempted, each with the reason it gave: one clause per
  ///     attempt, naming the model, its vendor, and the provider's own words.</summary>
  public string DescribeAttempts()
  {
    if (_failures.Count == 0)
    {
      return "no model was attempted for this turn.";
    }

    string models = string.Join("; ", _failures.Select(failure =>
        $"{failure.ModelId} ({ModelVendors.DisplayName(failure.Vendor ?? "unknown")}) "
        + $"[{failure.Code}]: {failure.Message}"));
    return $"{_failures.Count} model(s) were attempted for this turn and none served: {models}";
  }

  /// <summary>The config one attempt sends: the turn's resolved config with this entry's model
  ///     id, so the request names exactly one model.
  ///     <para>THE ENTRY'S ID IS A CATALOG KEY, SO THE CONFIG'S VARIANT IS APPLIED TO IT. The
  ///     stored entry never carries a routing suffix - the catalog is keyed on the base id - while
  ///     the wire carries the intent, so the suffix is written here exactly as the overlay writes
  ///     it for a single-model turn. Replacing the id without it would silently drop the tradeoff
  ///     the user chose on every entry after the first.</para>
  ///     <para>THE CONFIG CARRIES NO LIST OF MODELS, so a provider cannot fall through inside a
  ///     single request behind the walk's back: every fall-through is a step this walk takes.
  ///     </para></summary>
  /// <param name="config">The turn's resolved config.</param>
  /// <param name="modelId">The entry's base catalog model id.</param>
  public static ModelConfig ConfigFor(ModelConfig config, string modelId)
  {
    ArgumentNullException.ThrowIfNull(config);
    return config with { ModelId = ModelVariants.ApplyTo(config.Variant, modelId) };
  }

  /// <summary>One attempted model and the reason it could not serve.</summary>
  private sealed record ModelWalkFailure(string ModelId, string? Vendor, string Code, string Message);
}

/// <summary>Where a turn's walk comes from: the session's live ordered model list, the vendor
///     the session recorded, and the vendor-keyed provider resolution.
///     <para>THE LIST IS READ AT TURN START, not at construction, so a model the user adds or
///     drops between turns is what the next turn walks.</para></summary>
/// <param name="Preferences">The session's live model preferences, whose entries are the walk
///     order.</param>
/// <param name="SessionVendor">The provider id the session recorded, used by an entry that
///     names no vendor.</param>
/// <param name="Resolver">The vendor-keyed provider resolution.</param>
public sealed record ModelWalkSource(SessionModelPreferences? Preferences, string? SessionVendor,
    IModelProviderResolver Resolver)
{
  /// <summary>The turn's walk over the session's current list, or a named failure when the list
  ///     is empty.</summary>
  public Result<ModelWalk> Begin() => ModelWalk.Create(Preferences?.Entries, SessionVendor);
}
