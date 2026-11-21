namespace eThangAgent.ModelDomain;

/// <summary>One entry of a session's ordered model list: the model, and the VENDOR that serves
///     it.
///     <para><see cref="ProviderId"/> IS NULLABLE, AND NULL IS A FACT: it means NO VENDOR WAS
///     NAMED, which is what every selection written before vendors existed says. It is a
///     different statement from naming a vendor, and a consumer that resolves a provider must
///     fall back to its session's binding rather than invent one.</para>
///     <para>A BLANK OR WHITESPACE ID IS UNSET, NOT A VENDOR NAMED AS EMPTY STRING: the two
///     spellings mean the same thing to every consumer, so they are normalized to one.
///     </para>
///     <para>THE SAME MODEL ID UNDER TWO VENDORS IS TWO LEGAL ENTRIES. They differ in price and
///     in quota, so the list must be able to hold both; a duplicate is the same model under the
///     SAME vendor.</para></summary>
/// <param name="ModelId">The base catalog model id, never carrying a routing-variant suffix.
///     </param>
/// <param name="ProviderId">The vendor id that serves this entry, or null when none was named.
///     </param>
public sealed record ModelSelectionEntry(string ModelId, string? ProviderId = null)
{
  /// <summary>The vendor this entry names, with a blank or whitespace id normalized to null.
  ///     </summary>
  public string? Vendor => string.IsNullOrWhiteSpace(ProviderId) ? null : ProviderId;

  /// <summary>True when this entry names no vendor, so a consumer falls back to its session's
  ///     binding.</summary>
  public bool HasVendor => Vendor is not null;
}
