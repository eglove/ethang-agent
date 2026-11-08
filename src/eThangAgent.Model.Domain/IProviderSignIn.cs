using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Runs a provider's interactive sign-in and returns the API key it mints.
///     <para>THE RESULT IS AN ORDINARY KEY, NOT A TOKEN: the provider hands back a long-lived
///     credential that every model call presents, with no refresh path and no expiry, so a host
///     stores it exactly as it stores a key the user typed. What the flow removes is the
///     copy-and-paste, not the storage.</para></summary>
public interface IProviderSignIn
{
  /// <summary>Drives the whole flow - authorization in the user's browser, the loopback
  ///     callback, the code exchange - and returns the minted key. A flow the user never
  ///     completes times out with a typed error instead of waiting forever; cancelling
  ///     <paramref name="ct"/> abandons it.</summary>
  Task<Result<string>> SignInAsync(CancellationToken ct = default);
}
