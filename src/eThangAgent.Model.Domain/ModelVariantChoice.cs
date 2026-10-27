using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>The model + routing-variant choice a session commits to, and the codec for the
///     value a workspace preference stores.
///     <para>WHY THIS IS A DOMAIN TYPE AND NOT A VIEW-MODEL DETAIL: the choice is what the
///     resolver puts on the wire (as the variant-suffixed model id), what the status line
///     names, and what a reopened workspace restores. A second spelling written at a second
///     call site is exactly how the model and the routing intent drift apart.</para>
///     <para>THE VARIANT IS STORED SEPARATELY FROM THE MODEL even though the wire form
///     combines them: the catalog is keyed on the BASE model, while the wire carries the
///     intent, so keeping both halves explicit means no consumer has to re-derive one from
///     the other by string surgery.</para></summary>
/// <param name="ModelId">The BASE model id (the catalog key), never carrying a variant
///     suffix.</param>
/// <param name="Variant">The routing variant, or <see cref="ModelVariant.Standard"/> for the
///     provider's own routing.</param>
public sealed record ModelVariantChoice(string ModelId, ModelVariant Variant = ModelVariant.Standard)
{
  /// <summary>The id to send to the provider: the base model with the variant appended.
  ///     This is the whole routing decision.</summary>
  public string WireModelId => ModelVariants.ApplyTo(Variant, ModelId);

  /// <summary>The base model id the catalog is keyed on. Always the stored
  ///     <see cref="ModelId"/>, exposed by contract so a caller that receives a wire id can
  ///     normalize the same way.</summary>
  public string BaseModelId => ModelVariants.BaseOf(ModelId);

  /// <summary>Renders the choice for the preference store: a JSON object with both members.
  ///     <para>JSON RATHER THAN A DELIMITED STRING: a model id may contain any character the
  ///     catalog chooses, so a delimiter that an id happens to contain would split the choice
  ///     into a model that does not exist and a variant nobody chose.</para></summary>
  public string Serialize() => JsonSerializer.Serialize(new ChoiceDocument(ModelId, Variant.ToString()));

  /// <summary>Reads a stored value back, accepting every shape this harness has ever
  ///     written.
  ///     <para>THE LEGACY SHAPES RESTORE THE MODEL AND NOTHING ELSE. A bare model id (the
  ///     oldest form) and the retired provider pair both restore under Standard: a provider
  ///     the user chose under a deleted surface must not be resurrected as a routing decision
  ///     they never made. Refusing those values outright would instead silently drop a model
  ///     the user did choose.</para>
  ///     <para>A LEGACY ID THAT ALREADY CARRIES A VARIANT SUFFIX IS SPLIT, so the stored value
  ///     is normalized (base id + variant) and the picker preselects the right dropdown row
  ///     instead of showing a model whose id merely ends in ':nitro'.</para>
  ///     <para>AN UNKNOWN VARIANT IS A FAILURE, NOT A SILENT STANDARD: it may name a variant a
  ///     later build added, and substituting Standard would route a turn the user did not ask
  ///     for. The caller falls back to its session default, which the user can see.</para>
  ///     </summary>
  public static Result<ModelVariantChoice> Parse(string? stored)
  {
    if (string.IsNullOrWhiteSpace(stored))
    {
      return Result.Failure<ModelVariantChoice>(new DomainError("InvalidModelChoice",
          "no model choice is stored."));
    }

    string trimmed = stored.Trim();
    if (!trimmed.StartsWith('{'))
    {
      // A bare model id. It may carry the suffix the newer picker would have written.
      return Result.Success(Normalize(trimmed));
    }

    try
    {
      ChoiceDocument? parsed = JsonSerializer.Deserialize<ChoiceDocument>(trimmed);
      if (parsed is not { ModelId.Length: > 0 } document)
      {
        return Result.Failure<ModelVariantChoice>(new DomainError("InvalidModelChoice",
            "the stored model choice names no model."));
      }

      // A blank variant is UNSET, not unknown: the retired provider-pair payload has no
      // variant member at all, and a hand-written blank means the same thing.
      if (string.IsNullOrWhiteSpace(document.Variant))
      {
        return Result.Success(Normalize(document.ModelId));
      }

      // A DEFINED member only. Enum.TryParse alone would accept "5" and hand back an
      // out-of-range value the domain never defined.
      if (!Enum.TryParse(document.Variant, ignoreCase: true, out ModelVariant variant)
          || !Enum.IsDefined(variant))
      {
        return Result.Failure<ModelVariantChoice>(new DomainError("InvalidModelChoice",
            $"the stored model choice names an unknown routing variant '{document.Variant}'; "
            + "pick the model again in Model Settings."));
      }

      // The variant is stated; the model still must be normalized in case the id carries its
      // own suffix (a hand-written value can carry both).
      return Result.Success(new ModelVariantChoice(
          ModelVariants.BaseOf(document.ModelId), variant));
    }
#pragma warning disable CA1031 // A malformed preference is an EXPECTED condition: it becomes a named error.
    catch (JsonException ex)
#pragma warning restore CA1031
    {
      return Result.Failure<ModelVariantChoice>(new DomainError("InvalidModelChoice",
          "the stored model choice is not valid JSON: " + ex.Message));
    }
  }

  /// <summary>A parsed model id split into its base and the variant the id itself carries.
  ///     </summary>
  private static ModelVariantChoice Normalize(string modelId)
      => new(ModelVariants.BaseOf(modelId), ModelVariants.FromModelId(modelId));

  /// <summary>The wire shape. The retired <c>ProviderName</c> member is deliberately absent:
  ///     an unknown member deserializes away, which is exactly the handling a value written by
  ///     the deleted surface needs.</summary>
  private sealed record ChoiceDocument(string ModelId, string? Variant);
}
