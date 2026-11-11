using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.OpenRouter.ACL;

/// <summary>Exchanges an OAuth authorization code for a user-controlled API key
///     (POST /api/v1/auth/keys). The response body's 'key' field is the credential the
///     provider will accept on every model call; there is no token to refresh and no
///     expiry to track.</summary>
public sealed class OpenRouterCodeExchange(HttpClient http, OpenRouterConfiguration config)
{
  private const int MaxErrorBodyCharacters = 300;

  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly OpenRouterConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));

  /// <summary>Redeems <paramref name="code"/> and returns the minted key. A code is
  ///     single-use and short-lived, so every failure here is terminal for the attempt:
  ///     the caller reports it and the user starts a new sign-in.</summary>
  public async Task<Result<string>> ExchangeAsync(string code, string verifier, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(code);
    ArgumentException.ThrowIfNullOrWhiteSpace(verifier);

    using HttpRequestMessage request = new(HttpMethod.Post, _config.Endpoint("/api/v1/auth/keys"))
    {
      Content = JsonContent.Create(new
      {
        code,
        code_verifier = verifier,
        code_challenge_method = OpenRouterPkce.ChallengeMethod,
      }),
    };

    try
    {
      using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
      return response.IsSuccessStatusCode
          ? ReadKey(body)
          : Result.Failure<string>(new DomainError("SignInExchangeFailed",
              $"OpenRouter refused the authorization code ({(int)response.StatusCode}): {Cap(body)}"));
    }
    catch (HttpRequestException ex)
    {
      return Result.Failure<string>(new DomainError("SignInExchangeFailed",
          $"The OpenRouter sign-in could not reach the provider: {ex.Message}"));
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
      return Result.Failure<string>(new DomainError("SignInExchangeFailed",
          "The OpenRouter sign-in timed out while redeeming the authorization code."));
    }
  }

  /// <summary>Pulls 'key' out of the response body. A body that parses but carries no
  ///     usable key is a typed failure, never an empty credential handed to the host.</summary>
  private static Result<string> ReadKey(string body)
  {
    try
    {
      using JsonDocument document = JsonDocument.Parse(body);
      if (document.RootElement.ValueKind == JsonValueKind.Object
          && document.RootElement.TryGetProperty("key", out JsonElement key)
          && key.ValueKind == JsonValueKind.String
          && key.GetString() is { Length: > 0 } value)
      {
        return Result.Success(value);
      }
    }
    catch (JsonException)
    {
      return Result.Failure<string>(new DomainError("SignInExchangeFailed",
          $"OpenRouter returned an unreadable authorization response: {Cap(body)}"));
    }

    return Result.Failure<string>(new DomainError("SignInExchangeFailed",
        $"OpenRouter returned no API key in the authorization response: {Cap(body)}"));
  }

  private static string Cap(string body) =>
      body.Length <= MaxErrorBodyCharacters ? body : body[..MaxErrorBodyCharacters] + "...";
}
