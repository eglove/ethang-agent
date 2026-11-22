namespace eThangAgent.Cloudflare.ACL;

/// <summary>Holds the Cloudflare credential a container's catalog and provider read LIVE at
///     dispatch, so a credential saved after that container was built is in force without
///     rebuilding it. Seeded from settings at container build; the host pushes updates.
///     <para>THE CARRIER IS AUTHORITATIVE WHENEVER IT IS WIRED, INCLUDING WHEN IT HOLDS
///     NOTHING: a credential the user cleared must not fall back to the snapshot the container
///     was built with, which would resurrect the value the save just removed.</para>
///     <para>BOTH HALVES ARE SET TOGETHER, so no reader can see one account's id beside
///     another account's token.</para>
///     <para>THE GATEWAY ID TRAVELS WITH THEM. It is not a secret, but it is part of the same
///     configuration a save replaces, so it is set by the same call: a gateway configured after
///     the container was built must be in force on the next call, exactly as the credential
///     is.</para></summary>
public sealed class CloudflareCredentialCarrier
{
  /// <summary>The live account id, or null when the carrier holds none.</summary>
  public string? AccountId { get; private set; }

  /// <summary>The live API token, or null when the carrier holds none.</summary>
  public string? ApiToken { get; private set; }

  /// <summary>The live AI Gateway id, or null when calls go to Workers AI directly.</summary>
  public string? GatewayId { get; private set; }

  /// <summary>True when both halves are present, which is the least a call needs.</summary>
  public bool IsConfigured =>
      !string.IsNullOrWhiteSpace(AccountId) && !string.IsNullOrWhiteSpace(ApiToken);

  /// <summary>Replaces the whole configuration at once. A blank member is stored as null, so
  ///     'cleared' is one fact rather than several spellings of it.</summary>
  /// <param name="accountId">The account id, or null/blank for none.</param>
  /// <param name="apiToken">The API token, or null/blank for none.</param>
  /// <param name="gatewayId">The AI Gateway id, or null/blank for no gateway.</param>
  public void Set(string? accountId, string? apiToken, string? gatewayId = null)
  {
    AccountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
    ApiToken = string.IsNullOrWhiteSpace(apiToken) ? null : apiToken.Trim();
    GatewayId = string.IsNullOrWhiteSpace(gatewayId) ? null : gatewayId.Trim();
  }
}
