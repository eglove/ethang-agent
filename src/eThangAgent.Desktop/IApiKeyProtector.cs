namespace eThangAgent.Desktop;

/// <summary>Protects provider API keys at rest: plaintext keys never touch durable
///     storage. The Settings modal and the desktop composition root are the only
///     consumers; tests substitute fakes.</summary>
internal interface IApiKeyProtector
{
  /// <summary>Returns the storage form of <paramref name="apiKey"/> (opaque, not
  ///     human-readable). The result is what <see cref="Unprotect"/> expects.</summary>
  string Protect(string apiKey);

  /// <summary>Recovers the plaintext key behind <paramref name="storedValue"/>, or
  ///     null when it cannot be decrypted (corrupted or foreign blob). A null result
  ///     means "treat the key as unconfigured", never a crash.</summary>
  string? Unprotect(string storedValue);
}
