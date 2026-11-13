using System.Text.Json;
using System.Text.Json.Serialization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>The ordered list of models a session prefers, plus the one routing variant and the
///     one retention tier that cover the whole list, and the codec a workspace preference
///     round-trips through.
///     <para>THE HEAD IS THE MODEL THAT RUNS AND THE TAIL IS AN ORDERED FALLBACK: the provider is
///     handed the list as a preference and falls through to the next entry when a model is
///     unavailable. Order is therefore part of the value, not a presentation detail — two
///     selections carrying the same ids in different orders are different selections.</para>
///     <para>THE VARIANT IS STORED SEPARATELY FROM THE IDS even though the wire form combines
///     them: the catalog is keyed on the BASE model id, while the wire carries the intent, so
///     each id stays the catalog key and no consumer re-derives a variant by string surgery.
///     Only <see cref="WireModelIds"/> carries suffixes.</para></summary>
/// <param name="ModelIds">The base model ids the catalog is keyed on, most preferred first,
///     never carrying a routing-variant suffix. Always holds at least one entry.</param>
/// <param name="Variant">The routing variant every entry is served under, or
///     <see cref="ModelVariant.Standard"/> for the provider's own routing.</param>
/// <param name="Retention">The retention tier the session routes under. ZDR unless the user
///     explicitly loosened it, so a stored value that never mentioned a tier restores as the
///     strict one.</param>
public sealed record ModelSelection(IReadOnlyList<string> ModelIds,
    ModelVariant Variant = ModelVariant.Standard,
    RetentionPolicy Retention = RetentionPolicy.ZeroDataRetention)
{
  /// <summary>Value equality over the ordered ids, the variant, and the tier.
  ///     <para>THE DEFAULT RECORD EQUALITY COMPARES THE LIST BY REFERENCE, so two selections
  ///     carrying the same ids would compare unequal. Order is part of the value: the same ids in
  ///     a different order are different selections.</para></summary>
  public bool Equals(ModelSelection? other)
      => other is not null
          && Variant == other.Variant
          && Retention == other.Retention
          && ModelIds.SequenceEqual(other.ModelIds, StringComparer.Ordinal);

  /// <summary>The hash of the ordered ids, the variant, and the tier, matching
  ///     <see cref="Equals(ModelSelection)"/>.</summary>
  public override int GetHashCode()
  {
    HashCode hash = new();
    hash.Add(Variant);
    hash.Add(Retention);
    foreach (string modelId in ModelIds)
    {
      hash.Add(modelId, StringComparer.Ordinal);
    }

    return hash.ToHashCode();
  }

  /// <summary>The model that runs: the first entry of the list. Each turn starts here, and a
  ///     turn served by a backup leaves the list itself unchanged.</summary>
  public string Head => ModelIds[0];

  /// <summary>The ids to send to the provider: every entry in order with the selection's
  ///     variant applied, which is the whole routing decision.
  ///     <para>APPLYING IS IDEMPOTENT because <see cref="ModelVariants.ApplyTo"/> strips any
  ///     existing routing suffix before appending its own, so re-applying a variant cannot
  ///     double it.</para></summary>
  public IReadOnlyList<string> WireModelIds
      => [.. ModelIds.Select(id => ModelVariants.ApplyTo(Variant, id))];

  /// <summary>Renders the selection for the preference store: a JSON object carrying the whole
  ///     ordered list with the variant and the tier.
  ///     <para>JSON RATHER THAN A DELIMITED STRING: a model id may contain any character the
  ///     catalog chooses, so a delimiter that an id happens to contain would split the selection
  ///     into models that do not exist and a variant nobody chose.</para></summary>
  public string Serialize()
      => JsonSerializer.Serialize(new SelectionDocument(
          [.. ModelIds],
          Variant.ToString(),
          Retention.ToString()));

  /// <summary>Builds a selection from ids a caller is about to commit, refusing anything that
  ///     would put a request on the wire under a model the user did not name. This is the path
  ///     every validating caller uses; <see cref="Parse"/> is the read path.
  ///     <para>THE LIST IS TRIMMED AT THE EDGES ONLY. Surrounding whitespace is not part of an
  ///     id, so it is removed; an id that is empty after trimming is refused rather than
  ///     compacted away, because a blank row the user added is a mistake they can see and a
  ///     silently shortened list is not.</para>
  ///     <para>A REPEATED ID IS REFUSED AND NAMED: the second occurrence can never serve,
  ///     because the first already covered it, so it would be a row the picker shows and the
  ///     provider never reaches.</para></summary>
  public static Result<ModelSelection> Create(IEnumerable<string> modelIds,
      ModelVariant variant = ModelVariant.Standard,
      RetentionPolicy retention = RetentionPolicy.ZeroDataRetention)
  {
    if (modelIds is null)
    {
      return Result.Failure<ModelSelection>(AtLeastOneModel());
    }

    List<string> trimmed = [.. modelIds.Select(id => id?.Trim() ?? string.Empty)];
    if (trimmed.Count == 0)
    {
      return Result.Failure<ModelSelection>(AtLeastOneModel());
    }

    int blank = trimmed.FindIndex(string.IsNullOrEmpty);
    if (blank >= 0)
    {
      return Result.Failure<ModelSelection>(new DomainError("InvalidModelChoice",
          $"the model list has no id at position {blank + 1}; every entry must name a model."));
    }

    string? duplicate = trimmed.GroupBy(id => id, StringComparer.Ordinal)
        .FirstOrDefault(group => group.Count() > 1)?.Key;
    return duplicate is not null
        ? Result.Failure<ModelSelection>(new DomainError("InvalidModelChoice",
            $"the model '{duplicate}' appears more than once in the list; a repeated model "
            + "can never serve, so remove the extra entry."))
        : Result.Success(new ModelSelection(trimmed, variant, retention));
  }

  /// <summary>Reads a stored value back, accepting every shape this harness has ever written.
  ///     <para>THE LEGACY SHAPES RESTORE THE MODEL AND NOTHING ELSE. A bare model id (the oldest
  ///     form), a bare id already carrying a routing suffix, and the retired single-choice
  ///     document all restore as a ONE-ENTRY list under Standard: a provider the user chose under
  ///     a deleted surface must not be resurrected as a routing decision they never made.
  ///     Refusing those values outright would instead silently drop a model the user did
  ///     choose.</para>
  ///     <para>A LEGACY ID THAT CARRIES A VARIANT SUFFIX IS SPLIT, so the restored list is
  ///     normalized (base id + variant) and the picker preselects the right dropdown row instead
  ///     of showing a model whose id merely ends in ':nitro'.</para>
  ///     <para>AN UNKNOWN VARIANT OR TIER IS A FAILURE, NOT A SILENT DEFAULT: it may name a
  ///     member a later build added, and substituting one would route a turn the user did not ask
  ///     for. The caller falls back to its session default, which the user can see.</para>
  ///     <para>A DOCUMENT NAMING NO MODEL IS A FAILURE rather than an empty selection: a
  ///     selection with no head has nothing to serve and no id to report.</para></summary>
  public static Result<ModelSelection> Parse(string? stored)
  {
    if (string.IsNullOrWhiteSpace(stored))
    {
      return Result.Failure<ModelSelection>(new DomainError("InvalidModelChoice",
          "no model selection is stored."));
    }

    string trimmed = stored.Trim();
    if (!trimmed.StartsWith('{', StringComparison.Ordinal))
    {
      // A bare model id. It may carry the suffix the newer picker would have written.
      return Result.Success(Normalize([trimmed]));
    }

    try
    {
      SelectionDocument? parsed = JsonSerializer.Deserialize<SelectionDocument>(trimmed);
      if (parsed is null)
      {
        return Result.Failure<ModelSelection>(NoModel());
      }

      Result<RetentionPolicy> tier = ResolveRetention(parsed);
      if (!tier.IsSuccess)
      {
        return Result.Failure<ModelSelection>(tier.Error);
      }

      Result<ModelVariant> variant = ResolveVariant(parsed.Variant, parsed.Models, parsed.ModelId);
      if (!variant.IsSuccess)
      {
        return Result.Failure<ModelSelection>(variant.Error);
      }

      // A document written before the list existed names its model under 'ModelId'.
      IReadOnlyList<string> storedIds = [];
      if (parsed.Models is { } models)
      {
        storedIds = [.. models];
      }
      else if (!string.IsNullOrWhiteSpace(parsed.ModelId))
      {
        storedIds = [parsed.ModelId];
      }

      return storedIds.Count == 0 || storedIds.Any(string.IsNullOrWhiteSpace)
          ? Result.Failure<ModelSelection>(NoModel())
          : Result.Success(new ModelSelection(
              [.. storedIds.Select(BaseId)], variant.Value, tier.Value));
    }
#pragma warning disable CA1031 // A malformed preference is an EXPECTED condition: it becomes a named error.
    catch (JsonException ex)
#pragma warning restore CA1031
    {
      return Result.Failure<ModelSelection>(new DomainError("InvalidModelChoice",
          "the stored model selection is not valid JSON: " + ex.Message));
    }
  }

  /// <summary>A stored list normalized to base ids, keeping its order and the variant the head
  ///     carries. Used for the shapes that predate the variant member.</summary>
  private static ModelSelection Normalize(IReadOnlyList<string> storedIds)
      => new([.. storedIds.Select(BaseId)], FromStoredIds(storedIds[0].Trim()));

  /// <summary>The head of a stored document: the first entry it names, from the list when the
  ///     document carries one and from the single-choice member otherwise. Blank ids are
  ///     skipped, so a document naming only a blank entry has no head.</summary>
  private static string HeadOf(IReadOnlyList<string>? models, string? modelId)
  {
    string? head = models?.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
        ?? (string.IsNullOrWhiteSpace(modelId) ? null : modelId);
    return head?.Trim() ?? string.Empty;
  }

  /// <summary>The base id the catalog is keyed on: the stored id without any routing-variant
  ///     suffix.</summary>
  private static string BaseId(string storedId) => ModelVariants.BaseOf(storedId.Trim());

  /// <summary>The variant a stored selection states.
  ///     <para>A BLANK VARIANT IS UNSET, NOT UNKNOWN: a document written before the variant
  ///     member existed has none, and a hand-written blank means the same thing. The suffix the
  ///     stored head carries — the oldest spelling of the same decision — is then read back out.
  ///     </para>
  ///     <para>AN EXPLICIT VARIANT WINS over the suffix, because it is the pair the picker
  ///     wrote.</para></summary>
  private static Result<ModelVariant> ResolveVariant(string? stated,
      IReadOnlyList<string>? models, string? modelId)
      => string.IsNullOrWhiteSpace(stated)
          ? Result.Success(FromStoredIds(HeadOf(models, modelId)))
          : StatedVariant(stated);

  /// <summary>The router an explicitly stated variant names, requiring a DEFINED member:
  ///     Enum.TryParse alone would accept "5" and hand back an out-of-range value the
  ///     domain never defined.</summary>
  private static Result<ModelVariant> StatedVariant(string stated)
      => Enum.TryParse(stated, ignoreCase: true, out ModelVariant variant) && Enum.IsDefined(variant)
          ? Result.Success(variant)
          : Result.Failure<ModelVariant>(new DomainError("InvalidModelChoice",
              $"the stored model selection names an unknown routing variant '{stated}'; "
              + "pick the model again in Model Settings."));

  /// <summary>The routing variant the stored head carries, or
  ///     <see cref="ModelVariant.Standard"/> when it carries none.
  ///     <para>ONLY THE HEAD'S SUFFIX COUNTS: one variant covers the whole list, and the head is
  ///     the model that runs, so a suffix on a backup states nothing a single variant could
  ///     honour.</para></summary>
  private static ModelVariant FromStoredIds(string head)
      => string.IsNullOrWhiteSpace(head) ? ModelVariant.Standard : ModelVariants.FromModelId(head);

  /// <summary>The tier a stored document states, resolving the legacy boolean when that is what
  ///     it carries.
  ///     <para>AN EXPLICIT TIER WINS, and an unknown one is a FAILURE rather than a silent ZDR:
  ///     it may name a tier a later build added, and substituting the strict tier would tighten a
  ///     session the user had loosened.</para>
  ///     <para>THE LEGACY BOOLEAN MAPS TO THE TIERS IT COULD EXPRESS: <c>false</c> meant 'no
  ///     restriction', which is <see cref="RetentionPolicy.RetentionAndTraining"/>; <c>true</c>
  ///     and an absent member both meant 'restricted', which is the strict tier. A DEFINED
  ///     member only, for the same reason as the variant.</para></summary>
  private static Result<RetentionPolicy> ResolveRetention(SelectionDocument document)
      => string.IsNullOrWhiteSpace(document.Retention)
          ? Result.Success(LegacyRetention(document))
          : StatedRetention(document.Retention);

  /// <summary>The tier the retired boolean expressed: <c>false</c> meant 'no restriction',
  ///     while <c>true</c> and an absent member both meant 'restricted'.</summary>
  private static RetentionPolicy LegacyRetention(SelectionDocument document)
      => document.ZeroDataRetention is false
          ? RetentionPolicy.RetentionAndTraining
          : RetentionPolicy.ZeroDataRetention;

  /// <summary>The explicitly stated tier, requiring a DEFINED member for the same reason the
  ///     variant does.</summary>
  private static Result<RetentionPolicy> StatedRetention(string stated)
      => Enum.TryParse(stated, ignoreCase: true, out RetentionPolicy policy)
          && Enum.IsDefined(policy)
              ? Result.Success(policy)
              : Result.Failure<RetentionPolicy>(new DomainError("InvalidModelChoice",
                  $"the stored model selection names an unknown retention tier '{stated}'; "
                  + "pick the model again in Model Settings."));

  /// <summary>The refusal for a selection that would name no model at all: nothing could serve,
  ///     so there is no head to run and no id to report.</summary>
  private static DomainError AtLeastOneModel()
      => new("InvalidModelChoice",
          "the model list is empty; add at least one model before saving.");

  private static DomainError NoModel()
      => new("InvalidModelChoice",
          "the stored model selection names no model; add at least one model in Model Settings.");

  /// <summary>The wire shape. <c>Models</c> is the ordered list this build writes;
  ///     <c>ModelId</c> is the single choice an earlier build wrote, read but never written.
  ///     <para><c>Variant</c>, <c>Retention</c>, and <c>ZeroDataRetention</c> ARE NULLABLE ON THE
  ///     WIRE: a document written before those members existed carries none of them, and
  ///     <c>ZeroDataRetention</c> is the boolean the oldest of them stated instead.</para>
  ///     <para><c>ZeroDataRetention</c> IS READ-ONLY: <see cref="Serialize"/> never writes it,
  ///     because a build that wrote both could later read the stale one over the live one.</para>
  ///     </summary>
  private sealed record SelectionDocument(
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
      IReadOnlyList<string>? Models,
      string? Variant,
      string? Retention = null,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
      string? ModelId = null,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
      bool? ZeroDataRetention = null);
}
