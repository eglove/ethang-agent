using System.Security.Cryptography;
using System.Text;

namespace eThangAgent.OpenRouter.ACL;

/// <summary>PKCE (RFC 7636) material for one authorization request: a high-entropy verifier,
///     its S256 challenge, and an opaque state value that ties the callback back to this
///     request. Single-use - a new instance is generated per sign-in attempt, and the
///     verifier never leaves the process that generated it.</summary>
public sealed record OpenRouterPkce(string Verifier, string Challenge, string State)
{
  /// <summary>The only challenge method this app requests.</summary>
  public const string ChallengeMethod = "S256";

  /// <summary>Generates a fresh verifier (32 random bytes) and state (16 random bytes).</summary>
  public static OpenRouterPkce Create() => Create(
      RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));

  internal static OpenRouterPkce Create(byte[] verifierBytes, byte[] stateBytes)
  {
    ArgumentNullException.ThrowIfNull(verifierBytes);
    ArgumentNullException.ThrowIfNull(stateBytes);
    string verifier = Base64Url(verifierBytes);
    return new OpenRouterPkce(verifier, ChallengeFor(verifier), Base64Url(stateBytes));
  }

  /// <summary>The S256 challenge for <paramref name="verifier"/>: the base64url encoding of
  ///     its SHA-256 digest, unpadded.</summary>
  public static string ChallengeFor(string verifier)
  {
    ArgumentNullException.ThrowIfNull(verifier);
    return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
  }

  /// <summary>base64url without padding: the encoding both PKCE parameters require.</summary>
  private static string Base64Url(byte[] bytes) =>
      Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
