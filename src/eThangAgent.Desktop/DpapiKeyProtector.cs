using System.Security.Cryptography;
using System.Text;

namespace eThangAgent.Desktop;

/// <summary><see cref="IApiKeyProtector"/> over Windows DPAPI with current-user scope:
///     only the Windows user who stored a key can decrypt it, and the ciphertext only
///     ever exists inside this machine's user profile. Values are stored base64-encoded
///     in the app-preferences store.</summary>
internal sealed class DpapiKeyProtector : IApiKeyProtector
{
  // App-specific entropy: blobs produced by other applications running under the
  // same Windows account are rejected here, and vice versa.
  private static readonly byte[] Entropy = "eThangAgent.ApiKeys.v1"u8.ToArray();

  public string Protect(string apiKey)
  {
    ArgumentNullException.ThrowIfNull(apiKey);
    byte[] encrypted = ProtectedData.Protect(
        Encoding.UTF8.GetBytes(apiKey), Entropy, DataProtectionScope.CurrentUser);
    return Convert.ToBase64String(encrypted);
  }

  public string? Unprotect(string storedValue)
  {
    ArgumentNullException.ThrowIfNull(storedValue);

    // Named decision (CA1031 scope kept tight): a value we cannot decrypt is a
    // corrupted or foreign blob — a structured "unconfigured", never a crash. The
    // caller logs and the key reads as absent.
    try
    {
      byte[] decrypted = ProtectedData.Unprotect(
          Convert.FromBase64String(storedValue), Entropy, DataProtectionScope.CurrentUser);
      return Encoding.UTF8.GetString(decrypted);
    }
    catch (FormatException)
    {
      return null;
    }
    catch (CryptographicException)
    {
      return null;
    }
  }
}
