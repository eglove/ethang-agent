using System.Text.Json;
using System.Text.Json.Serialization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>The ordered list of models a session prefers, each paired with the VENDOR that serves
///     it, plus the one routing variant and the one retention tier that cover the whole list, and
///     the codec a workspace preference round-trips through.
///     <para>THE HEAD IS THE MODEL THAT RUNS AND THE TAIL IS AN ORDERED FALLBACK: the harness
///     walks the list one entry at a time and falls through to the next when a model is unavailable.
///     Order is therefore part of the value, not a presentation detail — two selections carrying
///     the same entries in different orders are different selections.</para>
///     <para>THE VENDOR IS PART OF THE ENTRY, because the same model id served by two vendors is
///     two rows: they differ in price and in quota, so a list that carried only ids could not say
///     which one a turn should reach.</para>
///     <para>THE VARIANT IS STORED SEPARATELY FROM THE IDS even though the wire form combines
///     them: the catalog is keyed on the BASE model id, while the wire carries the intent, so
///     each id stays the catalog key and no consumer re-derives a variant by string surgery.
///     Only <see cref="WireModelIds"/> carries suffixes.</para></summary>
/// <param name="Entries">The ordered model+vendor pairs, most preferred first, each model id
///     never carrying a routing-variant suffix. Always holds at least one entry.</param>
/// <param name="Variant">The routing variant every entry is served under, or
///     <see cref="ModelVariant.Standard"/> for the provider's own routing.</param>
/// <param name="Retention">The retention tier the session routes under. ZDR unless the user
///     explicitly loosened it, so a stored value that never mentioned a tier restores as the
///     strict one.</param>
public sealed record ModelSelection(IReadOnlyList<ModelSelectionEntry> Entries,
    ModelVariant Variant = ModelVariant.Standard,
    RetentionPolicy Retention = RetentionPolicy.ZeroDataRetention)
{
  /// <summary>The most models a selection may name: the head plus two backups.
  ///     <para>THE PROVIDER ACCEPTS THREE. Its routing list is documented as taking at most three
  ///     entries, and a longer one is refused with a 400 before any model serves, so a fourth
  ///     entry is a list that can never run.</para></summary>
  public const int MaxModels = 3;

  private const string ModelsMember = "Models";
  private const string ModelIdMember = "ModelId";
  private const string ProviderMember = "Provider";
  private const string VariantMember = "Variant";
  private const string RetentionMember = "Retention";
  private const string ZeroDataRetentionMember = "ZeroDataRetention";
  private const string InvalidChoiceCode = "InvalidModelChoice";

  /// <summary>The base model ids the catalog is keyed on, in walk order. A projection of
  ///     <see cref="Entries"/>: the two can never disagree, because there is one stored value.
  ///     </summary>
  public IReadOnlyList<string> ModelIds => [.. Entries.Select(entry => entry.ModelId)];

  /// <summary>Value equality over the ordered (model id, vendor) pairs, the variant, and the
  ///     tier.
  ///     <para>THE DEFAULT RECORD EQUALITY COMPARES THE LIST BY REFERENCE, so two selections
  ///     carrying the same entries would compare unequal. Order is part of the value: the same
  ///     entries in a different order are different selections, and so are the same ids under
  ///     different vendors.</para></summary>
  /// <param name="other">The selection to compare with.</param>
  public bool Equals(ModelSelection? other)
      => other is not null
          && Variant == other.Variant
          && Retention == other.Retention
          && Entries.Count == other.Entries.Count
          && Entries.Zip(other.Entries).All(pair =>
              string.Equals(pair.First.ModelId, pair.Second.ModelId, StringComparison.Ordinal)
              && string.Equals(pair.First.Vendor, pair.Second.Vendor, StringComparison.Ordinal));

  /// <summary>The hash of the ordered entries, the variant, and the tier, matching
  ///     <see cref="Equals(ModelSelection)"/>.</summary>
  public override int GetHashCode()
  {
    HashCode hash = new();
    hash.Add(Variant);
    hash.Add(Retention);
    foreach (ModelSelectionEntry entry in Entries)
    {
      hash.Add(entry.ModelId, StringComparer.Ordinal);
      hash.Add(entry.Vendor, StringComparer.Ordinal);
    }

    return hash.ToHashCode();
  }

  /// <summary>The model that runs: the first entry of the list. Each turn starts here, and a
  ///     turn served by a backup leaves the list itself unchanged.</summary>
  public string Head => Entries[0].ModelId;

  /// <summary>The ids to send to a provider that accepts a list: every entry's base id in order
  ///     with the selection's variant applied.
  ///     <para>APPLYING IS IDEMPOTENT because <see cref="ModelVariants.ApplyTo"/> strips any
  ///     existing routing suffix before appending its own, so re-applying a variant cannot double
  ///     it.</para></summary>
  public IReadOnlyList<string> WireModelIds
      => [.. Entries.Select(entry => ModelVariants.ApplyTo(Variant, entry.ModelId))];

  /// <summary>Renders the selection for the preference store: a JSON object carrying the whole
  ///     ordered list with the variant and the tier.
  ///     <para>THE STRING-ARRAY SHAPE IS WRITTEN WHENEVER NO ENTRY NAMES A VENDOR, so a
  ///     selection that states nothing about vendors produces the document earlier builds wrote
  ///     and can still read. The object-array shape appears only when an entry names one.
  ///     </para>
  ///     <para>JSON RATHER THAN A DELIMITED STRING: a model id may contain any character the
  ///     catalog chooses, so a delimiter that an id happens to contain would split the selection
  ///     into models that do not exist and a variant nobody chose.</para></summary>
  public string Serialize()
      => Entries.Any(entry => entry.HasVendor)
          ? JsonSerializer.Serialize(new VendorSelectionDocument(
              [.. Entries.Select(entry => new VendorEntryDocument(entry.ModelId, entry.Vendor))],
              Variant.ToString(),
              Retention.ToString()))
          : JsonSerializer.Serialize(new SelectionDocument(
              [.. Entries.Select(entry => entry.ModelId)],
              Variant.ToString(),
              Retention.ToString()));

  /// <summary>Builds a selection from ids a caller is about to commit, with every entry's vendor
  ///     unset. The path a caller that names no vendor uses.</summary>
  /// <param name="modelIds">The ordered model ids.</param>
  /// <param name="variant">The routing variant covering the whole list.</param>
  /// <param name="retention">The retention tier covering the whole list.</param>
  public static Result<ModelSelection> Create(IEnumerable<string> modelIds,
      ModelVariant variant = ModelVariant.Standard,
      RetentionPolicy retention = RetentionPolicy.ZeroDataRetention)
      => modelIds is null
          ? Result.Failure<ModelSelection>(AtLeastOneModel())
          : Create(
              modelIds.Select(id => new ModelSelectionEntry(id ?? string.Empty)),
              variant, retention);

  /// <summary>Builds a selection from entries a caller is about to commit, refusing anything that
  ///     would put a request on the wire under a model the user did not name. This is the path
  ///     every validating caller uses; <see cref="Parse"/> is the read path.
  ///     <para>THE LIST IS TRIMMED AT THE EDGES ONLY. Surrounding whitespace is not part of an
  ///     id, so it is removed; an id that is empty after trimming is refused rather than
  ///     compacted away, because a blank row the user added is a mistake they can see and a
  ///     silently shortened list is not.</para>
  ///     <para>AN UNKNOWN VENDOR IS REFUSED BEFORE THE DUPLICATE AND CAP CHECKS, so the refusal
  ///     names the real fault rather than a consequence of it.</para>
  ///     <para>A REPEATED ENTRY IS REFUSED AND NAMED: the second occurrence can never serve,
  ///     because the first already covered it. A repeat is the same model id under the SAME
  ///     vendor — the same model under two vendors is two legal entries.</para></summary>
  /// <param name="entries">The ordered model+vendor pairs.</param>
  /// <param name="variant">The routing variant covering the whole list.</param>
  /// <param name="retention">The retention tier covering the whole list.</param>
  public static Result<ModelSelection> Create(IEnumerable<ModelSelectionEntry> entries,
      ModelVariant variant = ModelVariant.Standard,
      RetentionPolicy retention = RetentionPolicy.ZeroDataRetention)
  {
    List<ModelSelectionEntry> trimmed = entries is null ? [] : Trim(entries);
    DomainError? refusal = trimmed.Count == 0 ? AtLeastOneModel() : EntryRefusal(trimmed);
    return refusal is not null
        ? Result.Failure<ModelSelection>(refusal)
        : Result.Success(new ModelSelection(trimmed, variant, retention));
  }

  /// <summary>The entries with surrounding whitespace removed from each id and each vendor id.
  ///     </summary>
  /// <param name="entries">The entries to trim.</param>
  private static List<ModelSelectionEntry> Trim(IEnumerable<ModelSelectionEntry> entries)
      =>
      [
        .. entries.Select(entry => new ModelSelectionEntry(
            entry?.ModelId?.Trim() ?? string.Empty, entry?.Vendor?.Trim())),
      ];

  /// <summary>The first refusal a trimmed entry list earns, or null when it may be committed.
  ///     <para>A BLANK ID IS ASKED FIRST, then an unknown vendor, then a repeat, then the cap: the
  ///     order is the order the faults matter in, so the refusal names the real problem rather
  ///     than a consequence of it.</para></summary>
  /// <param name="entries">The trimmed entries.</param>
  private static DomainError? EntryRefusal(List<ModelSelectionEntry> entries)
      => BlankIdRefusal(entries)
          ?? UnknownVendorRefusal(entries)
          ?? DuplicateRefusal(entries)
          ?? TooManyRefusal(entries);

  /// <summary>The refusal for an entry that names no model, or null when every entry does.
  ///     </summary>
  /// <param name="entries">The trimmed entries.</param>
  private static DomainError? BlankIdRefusal(List<ModelSelectionEntry> entries)
  {
    int position = entries.FindIndex(entry => string.IsNullOrEmpty(entry.ModelId));
    return position < 0
        ? null
        : new DomainError(InvalidChoiceCode,
            $"the model list has no id at position {position + 1}; every entry must name a model.");
  }

  /// <summary>Reads a stored value back, accepting every shape this harness has ever written.
  ///     <para>THE LEGACY SHAPES RESTORE THE MODEL AND NOTHING ELSE. A bare model id (the oldest
  ///     form), a bare id already carrying a routing suffix, and the retired single-choice
  ///     document all restore as a ONE-ENTRY list under Standard with no vendor named: a provider
  ///     the user chose under a deleted surface must not be resurrected as a routing decision they
  ///     never made. Refusing those values outright would instead silently drop a model the user
  ///     did choose.</para>
  ///     <para>A LEGACY ID THAT CARRIES A VARIANT SUFFIX IS SPLIT, so the restored list is
  ///     normalized (base id + variant) and the picker preselects the right dropdown row instead
  ///     of showing a model whose id merely ends in ':nitro'.</para>
  ///     <para>AN UNKNOWN VARIANT OR TIER IS A FAILURE, NOT A SILENT DEFAULT: it may name a
  ///     member a later build added, and substituting one would route a turn the user did not ask
  ///     for. The caller falls back to its session default, which the user can see.</para>
  ///     <para>A DOCUMENT NAMING NO MODEL IS A FAILURE rather than an empty selection: a
  ///     selection with no head has nothing to serve and no id to report.</para>
  ///     <para>A MIXED ARRAY IS A FAILURE, NOT A SHAPE TO GUESS AT: this harness never wrote an
  ///     array holding both a bare string and an object, so such a value is unknown input.
  ///     </para></summary>
  /// <param name="stored">The stored preference value.</param>
  public static Result<ModelSelection> Parse(string? stored)
  {
    if (string.IsNullOrWhiteSpace(stored))
    {
      return Result.Failure<ModelSelection>(new DomainError(InvalidChoiceCode,
          "no model selection is stored."));
    }

    string trimmed = stored.Trim();
    if (!trimmed.StartsWith('{', StringComparison.Ordinal))
    {
      // A bare model id. It may carry the suffix the newer picker would have written.
      return Result.Success(new ModelSelection([new ModelSelectionEntry(
          ModelVariants.BaseOf(trimmed))], ModelVariants.FromModelId(trimmed)));
    }

    try
    {
      return ParseDocument(trimmed);
    }
    catch (JsonException ex)
    {
      return Result.Failure<ModelSelection>(new DomainError(InvalidChoiceCode,
          "the stored model selection is not valid JSON: " + ex.Message));
    }
  }

  /// <summary>The read path for a stored JSON document: its entries, then its variant and tier.
  ///     </summary>
  /// <param name="document">The stored document text.</param>
  private static Result<ModelSelection> ParseDocument(string document)
  {
    using JsonDocument parsed = JsonDocument.Parse(document);
    JsonElement root = parsed.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<ModelSelection>(NoModel());
    }

    Result<List<ModelSelectionEntry>> entries = ReadEntries(root);
    if (!entries.IsSuccess)
    {
      return Result.Failure<ModelSelection>(entries.Error);
    }

    Result<RetentionPolicy> tier = ResolveRetention(root);
    if (!tier.IsSuccess)
    {
      return Result.Failure<ModelSelection>(tier.Error);
    }

    Result<ModelVariant> variant = ResolveVariant(StringMember(root, VariantMember), entries.Value);
    if (!variant.IsSuccess)
    {
      return Result.Failure<ModelSelection>(variant.Error);
    }

    // A LIST LONGER THAN THE PROVIDER ACCEPTS RESTORES AS ITS FIRST ENTRIES: the surplus could
    // never serve, and refusing the whole value would drop a head the user did choose.
    return Result.Success(new ModelSelection(
        [.. entries.Value.Take(MaxModels)], variant.Value, tier.Value));
  }

  /// <summary>The ordered entries a stored document names, from its <c>Models</c> array when it
  ///     carries one and from its single-choice <c>ModelId</c> member otherwise.
  ///     <para>AN ARRAY IS READ AS ONE KIND OR REFUSED: every element a bare string, or every
  ///     element an object carrying a model id. A mixture is unknown input.</para></summary>
  /// <param name="root">The stored document.</param>
  private static Result<List<ModelSelectionEntry>> ReadEntries(JsonElement root)
  {
    if (!root.TryGetProperty(ModelsMember, out JsonElement models)
        || models.ValueKind != JsonValueKind.Array)
    {
      // A document written before the list existed names its model under 'ModelId'.
      string? single = StringMember(root, ModelIdMember);
      return string.IsNullOrWhiteSpace(single)
          ? Result.Failure<List<ModelSelectionEntry>>(NoModel())
          : Result.Success(new List<ModelSelectionEntry> { BaseEntry(single) });
    }

    List<JsonElement> elements = [.. models.EnumerateArray()];
    if (elements.Count == 0 || elements.Any(element => element.ValueKind == JsonValueKind.Null))
    {
      return Result.Failure<List<ModelSelectionEntry>>(NoModel());
    }

    bool anyString = elements.Any(element => element.ValueKind == JsonValueKind.String);
    bool anyObject = elements.Any(element => element.ValueKind == JsonValueKind.Object);
    return anyString && anyObject
        ? Result.Failure<List<ModelSelectionEntry>>(MixedShape())
        : ReadUniform(elements, anyString);
  }

  /// <summary>The entries a stored array of ONE kind names: bare ids when every element is a
  ///     string, model entries otherwise.</summary>
  /// <param name="elements">The array's elements.</param>
  /// <param name="anyString">Whether the elements are bare ids.</param>
  private static Result<List<ModelSelectionEntry>> ReadUniform(List<JsonElement> elements,
      bool anyString)
      => anyString ? ReadBareEntries(elements) : ReadNamedEntries(elements);

  /// <summary>The entries a stored array of bare ids names, refusing an entry that names no model
  ///     rather than compacting it away.</summary>
  /// <param name="elements">The array's elements, every one a string.</param>
  private static Result<List<ModelSelectionEntry>> ReadBareEntries(List<JsonElement> elements)
  {
    int blank = elements.FindIndex(element => string.IsNullOrWhiteSpace(element.GetString()));
    return blank >= 0
        ? Result.Failure<List<ModelSelectionEntry>>(new DomainError(InvalidChoiceCode,
            $"the stored model selection has no model at position {blank + 1}; every entry "
            + "must name a model."))
        : Result.Success(elements.Select(element => BaseEntry(element.GetString())).ToList());
  }

  /// <summary>The refusal for a stored array holding both a bare id and an object: this harness
  ///     never wrote that shape, so it is unknown input rather than a shape to guess at.
  ///     </summary>
  private static DomainError MixedShape()
      => new(InvalidChoiceCode,
          "the stored model selection mixes bare model ids and model entries in one list; "
          + "this harness never wrote that shape. Pick the models again in Model Settings.");

  /// <summary>The entries a stored array of objects names, each carrying the vendor it states.
  ///     </summary>
  /// <param name="elements">The array's elements, every one an object.</param>
  private static Result<List<ModelSelectionEntry>> ReadNamedEntries(List<JsonElement> elements)
  {
    List<ModelSelectionEntry> named = [];
    for (int position = 0; position < elements.Count; position++)
    {
      JsonElement element = elements[position];
      string? modelId = element.ValueKind == JsonValueKind.Object
          ? StringMember(element, ModelIdMember)
          : null;
      if (string.IsNullOrWhiteSpace(modelId))
      {
        return Result.Failure<List<ModelSelectionEntry>>(new DomainError(InvalidChoiceCode,
            $"the stored model selection has no model at position {position + 1}; every entry "
            + "must name a model."));
      }

      string? vendor = string.IsNullOrWhiteSpace(StringMember(element, ProviderMember))
          ? null
          : StringMember(element, ProviderMember)!.Trim();
      if (vendor is not null && !ModelVendors.IsKnown(vendor))
      {
        return Result.Failure<List<ModelSelectionEntry>>(new DomainError(InvalidChoiceCode,
            $"the stored model selection names the unknown provider '{vendor}' at position "
            + $"{position + 1}; known providers are {string.Join(", ", ModelVendors.All)}."));
      }

      named.Add(new ModelSelectionEntry(ModelVariants.BaseOf(modelId.Trim()), vendor));
    }

    return Result.Success(named);
  }

  /// <summary>One stored bare id normalized to the base id the catalog is keyed on, with no
  ///     vendor named.</summary>
  /// <param name="storedId">The stored model id, which may carry a routing suffix.</param>
  private static ModelSelectionEntry BaseEntry(string? storedId)
      => new(string.IsNullOrWhiteSpace(storedId) ? string.Empty : ModelVariants.BaseOf(storedId.Trim()));

  /// <summary>A string member of a stored document, or null when it is absent or not a string.
  ///     </summary>
  /// <param name="element">The element to read from.</param>
  /// <param name="name">The member name.</param>
  private static string? StringMember(JsonElement element, string name)
      => element.ValueKind == JsonValueKind.Object
          && element.TryGetProperty(name, out JsonElement value)
          && value.ValueKind == JsonValueKind.String
              ? value.GetString()
              : null;

  /// <summary>The head of a stored document: the first entry it names. Blank ids are skipped, so
  ///     a document naming only a blank entry has no head.</summary>
  /// <param name="entries">The stored entries.</param>
  private static string HeadOf(IReadOnlyList<ModelSelectionEntry> entries)
      => entries.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.ModelId))?.ModelId
          ?? string.Empty;

  /// <summary>The variant a stored selection states.
  ///     <para>A BLANK VARIANT IS UNSET, NOT UNKNOWN: a document written before the variant
  ///     member existed has none, and a hand-written blank means the same thing. The suffix the
  ///     stored head carries — the oldest spelling of the same decision — is then read back out.
  ///     </para>
  ///     <para>AN EXPLICIT VARIANT WINS over the suffix, because it is the pair the picker
  ///     wrote.</para></summary>
  /// <param name="stated">The variant the document states, or null.</param>
  /// <param name="entries">The stored entries.</param>
  private static Result<ModelVariant> ResolveVariant(string? stated,
      IReadOnlyList<ModelSelectionEntry> entries)
      => string.IsNullOrWhiteSpace(stated)
          ? Result.Success(FromStoredIds(HeadOf(entries)))
          : StatedVariant(stated);

  /// <summary>The router an explicitly stated variant names, requiring a DEFINED member:
  ///     Enum.TryParse alone would accept "5" and hand back an out-of-range value the domain
  ///     never defined.</summary>
  /// <param name="stated">The stated variant name.</param>
  private static Result<ModelVariant> StatedVariant(string stated)
      => Enum.TryParse(stated, ignoreCase: true, out ModelVariant variant) && Enum.IsDefined(variant)
          ? Result.Success(variant)
          : Result.Failure<ModelVariant>(new DomainError(InvalidChoiceCode,
              $"the stored model selection names an unknown routing variant '{stated}'; "
              + "pick the model again in Model Settings."));

  /// <summary>The routing variant the stored head carries, or
  ///     <see cref="ModelVariant.Standard"/> when it carries none.
  ///     <para>ONLY THE HEAD'S SUFFIX COUNTS: one variant covers the whole list, and the head is
  ///     the model that runs, so a suffix on a backup states nothing a single variant could
  ///     honour.</para></summary>
  /// <param name="head">The stored head id.</param>
  private static ModelVariant FromStoredIds(string head)
      => string.IsNullOrWhiteSpace(head) ? ModelVariant.Standard : ModelVariants.FromModelId(head);

  /// <summary>The tier a stored document states, resolving the legacy boolean when that is what
  ///     it carries.
  ///     <para>AN EXPLICIT TIER WINS, and an unknown one is a FAILURE rather than a silent ZDR:
  ///     it may name a tier a later build added, and substituting the strict tier would tighten a
  ///     session the user had loosened.</para>
  ///     <para>THE LEGACY BOOLEAN MAPS TO THE TIERS IT COULD EXPRESS: <c>false</c> meant 'no
  ///     restriction', which is <see cref="RetentionPolicy.RetentionAndTraining"/>; <c>true</c>
  ///     and an absent member both meant 'restricted', which is the strict tier. A DEFINED member
  ///     only, for the same reason as the variant.</para></summary>
  /// <param name="root">The stored document.</param>
  private static Result<RetentionPolicy> ResolveRetention(JsonElement root)
  {
    string? stated = StringMember(root, RetentionMember);
    if (!string.IsNullOrWhiteSpace(stated))
    {
      return StatedRetention(stated);
    }

    bool? legacy = root.TryGetProperty(ZeroDataRetentionMember, out JsonElement stored)
        && stored.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? stored.GetBoolean()
            : null;
    return Result.Success(legacy is false
        ? RetentionPolicy.RetentionAndTraining
        : RetentionPolicy.ZeroDataRetention);
  }

  /// <summary>The explicitly stated tier, requiring a DEFINED member for the same reason the
  ///     variant does.</summary>
  /// <param name="stated">The stated tier name.</param>
  private static Result<RetentionPolicy> StatedRetention(string stated)
      => Enum.TryParse(stated, ignoreCase: true, out RetentionPolicy policy)
          && Enum.IsDefined(policy)
              ? Result.Success(policy)
              : Result.Failure<RetentionPolicy>(new DomainError(InvalidChoiceCode,
                  $"the stored model selection names an unknown retention tier '{stated}'; "
                  + "pick the model again in Model Settings."));

  /// <summary>The refusal for a selection that would name no model at all: nothing could serve,
  ///     so there is no head to run and no id to report.</summary>
  private static DomainError AtLeastOneModel()
      => new(InvalidChoiceCode,
          "the model list is empty; add at least one model before saving.");

  /// <summary>The refusal for an entry naming a vendor id this build does not know, or null when
  ///     every entry names one it does. Naming no vendor is legal and never refused.
  ///     </summary>
  /// <param name="entries">The trimmed entries.</param>
  private static DomainError? UnknownVendorRefusal(List<ModelSelectionEntry> entries)
  {
    int position = entries.FindIndex(entry => entry.Vendor is { } vendor
        && !ModelVendors.IsKnown(vendor));
    return position < 0
        ? null
        : new DomainError(InvalidChoiceCode,
            $"the model at position {position + 1} names the unknown provider "
            + $"'{entries[position].Vendor}'; known providers are "
            + $"{string.Join(", ", ModelVendors.All)}.");
  }

  /// <summary>The refusal for a list naming the same model under the same vendor twice: the
  ///     second occurrence can never serve, because the first already covered it. The same model
  ///     under two different vendors is legal and never refused.</summary>
  /// <param name="entries">The trimmed entries.</param>
  private static DomainError? DuplicateRefusal(List<ModelSelectionEntry> entries)
  {
    IGrouping<(string ModelId, string? Vendor), ModelSelectionEntry>? repeated = entries
        .GroupBy(entry => (entry.ModelId, entry.Vendor))
        .FirstOrDefault(group => group.Count() > 1);
    if (repeated is null)
    {
      return null;
    }

    string vendor = repeated.Key.Vendor is { } named ? $" under '{named}'" : string.Empty;
    return new DomainError(InvalidChoiceCode,
        $"the model '{repeated.Key.ModelId}' appears more than once in the list{vendor}; "
        + "a repeated model can never serve, so remove the extra entry.");
  }

  /// <summary>The refusal for a list longer than the provider accepts, or null when it fits.
  ///     </summary>
  /// <param name="entries">The trimmed entries.</param>
  private static DomainError? TooManyRefusal(List<ModelSelectionEntry> entries)
      => entries.Count > MaxModels ? TooManyModels(entries.Count) : null;

  /// <summary>The refusal for a selection longer than the provider accepts, naming the count and
  ///     the limit so the surplus entries can be removed rather than guessed at.</summary>
  /// <param name="count">How many models the list names.</param>
  private static DomainError TooManyModels(int count)
      => new(InvalidChoiceCode,
          $"the model list names {count} models; at most {MaxModels} can be selected, because the "
          + "provider accepts no more than that on one request. Remove the extra entries.");

  private static DomainError NoModel()
      => new(InvalidChoiceCode,
          "the stored model selection names no model; add at least one model in Model Settings.");

  /// <summary>The wire shape a selection with no vendor named writes: <c>Models</c> is the
  ///     ordered list of bare ids.</summary>
  /// <param name="Models">The ordered model ids.</param>
  /// <param name="Variant">The routing variant name.</param>
  /// <param name="Retention">The retention tier name.</param>
  private sealed record SelectionDocument(IReadOnlyList<string> Models, string Variant,
      string Retention);

  /// <summary>The wire shape a selection that names a vendor writes: <c>Models</c> is the ordered
  ///     list of model entries.</summary>
  /// <param name="Models">The ordered entries.</param>
  /// <param name="Variant">The routing variant name.</param>
  /// <param name="Retention">The retention tier name.</param>
  private sealed record VendorSelectionDocument(IReadOnlyList<VendorEntryDocument> Models,
      string Variant, string Retention);

  /// <summary>One entry in the vendor-carrying wire shape. <c>Provider</c> is omitted when no
  ///     vendor was named, so an entry that names none reads as unset rather than as an empty id.
  ///     </summary>
  /// <param name="ModelId">The base model id.</param>
  /// <param name="Provider">The vendor id, or null.</param>
  private sealed record VendorEntryDocument(string ModelId,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider);
}
