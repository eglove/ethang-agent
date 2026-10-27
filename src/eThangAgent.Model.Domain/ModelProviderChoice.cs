using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>The model + provider pair a session commits to, and the codec for the value a
///     workspace preference stores (issue #296, D7).
///     <para>WHY THIS IS A DOMAIN TYPE AND NOT A VIEW-MODEL DETAIL: the pair is what the
///     resolver pins onto a request, what the status line names, and what a reopened workspace
///     restores. A second parse rule written at a second call site is exactly how the model
///     and the provider drift apart - the defect this whole issue exists to close.</para>
///     <para>THE PROVIDER IS OPTIONAL AND NULL MEANS UNSET, never 'no provider exists': a
///     session that committed a model without a provider keeps the provider's own routing,
///     which is what every pre-existing session does.</para></summary>
/// <param name="ModelId">The model that should serve.</param>
/// <param name="ProviderName">The provider that should serve it, or null for the provider's
///     own routing.</param>
public sealed record ModelProviderChoice(string ModelId, string? ProviderName = null)
{
  /// <summary>Renders the pair for the preference store: a JSON object with the two members.
  ///     <para>JSON RATHER THAN A DELIMITED STRING: a provider name may contain any character
  ///     the catalog chooses, and a delimiter that a name happens to contain would split the
  ///     pair into a model that does not exist and a provider nobody chose.</para></summary>
  public string Serialize() => JsonSerializer.Serialize(new PairDocument(ModelId, ProviderName));

  /// <summary>Reads a stored value back. The LEGACY form (a bare model id, no braces) restores
  ///     the model and leaves the provider unset - a provider the user never chose is never
  ///     invented for them, and refusing the value outright would silently drop a model the
  ///     user did choose.</summary>
  public static Result<ModelProviderChoice> Parse(string? stored)
  {
    if (string.IsNullOrWhiteSpace(stored))
    {
      return Result.Failure<ModelProviderChoice>(new DomainError("InvalidModelChoice",
          "no model choice is stored."));
    }

    string trimmed = stored.Trim();
    if (!trimmed.StartsWith('{'))
    {
      // The legacy shape: a bare model id.
      return Result.Success(new ModelProviderChoice(trimmed));
    }

    try
    {
      PairDocument? parsed = JsonSerializer.Deserialize<PairDocument>(trimmed);
      if (parsed is not { ModelId.Length: > 0 } document)
      {
        return Result.Failure<ModelProviderChoice>(new DomainError("InvalidModelChoice",
            "the stored model choice names no model."));
      }

      string? provider = string.IsNullOrWhiteSpace(document.ProviderName)
          ? null
          : document.ProviderName;
      return Result.Success(new ModelProviderChoice(document.ModelId, provider));
    }
#pragma warning disable CA1031 // A malformed preference is an EXPECTED condition: it becomes a named error.
    catch (JsonException ex)
#pragma warning restore CA1031
    {
      return Result.Failure<ModelProviderChoice>(new DomainError("InvalidModelChoice",
          "the stored model choice is not valid JSON: " + ex.Message));
    }
  }

  /// <summary>The wire shape. A private nested record keeps the two member names stable
  ///     without exposing a second public type for the same pair.</summary>
  private sealed record PairDocument(string ModelId, string? ProviderName);
}
