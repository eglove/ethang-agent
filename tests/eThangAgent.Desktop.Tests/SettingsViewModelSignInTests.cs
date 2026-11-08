using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>The settings modal's sign-in: the minted key lands in the field the save
///     persists, a failure is shown without touching the field, and a host that wired no
///     sign-in offers no button.</summary>
public class SettingsViewModelSignInTests
{
  [Fact]
  public void SignIn_IsOffered_OnlyWhenTheHostWiredIt()
  {
    SettingsViewModel without = new(new SettingsValues());
    SettingsViewModel with = new(new SettingsValues
    {
      ProviderSignInFactory = _ => new StubSignIn(Result.Success("k")),
    });

    Assert.Null(without.ProviderSignIn);
    Assert.NotNull(with.ProviderSignIn);
  }

  [Fact]
  public async Task SignIn_PutsTheMintedKeyInTheField_AndTheSaveCarriesIt()
  {
    SettingsViewModel vm = new(new SettingsValues
    {
      ProviderSignInFactory = _ => new StubSignIn(Result.Success("sk-or-v1-minted")),
    });
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;

    await vm.SignInCommand.ExecuteAsync(null);

    Assert.Equal("sk-or-v1-minted", vm.OpenRouterKey);
    Assert.Null(vm.SignInError);
    vm.SaveCommand.Execute(null);
    Assert.NotNull(saved);
    Assert.Equal("sk-or-v1-minted", saved.OpenRouterApiKey);
  }

  [Fact]
  public async Task SignIn_ShowsTheFailure_AndLeavesTheFieldAlone()
  {
    SettingsViewModel vm = new(new SettingsValues
    {
      OpenRouterKey = "sk-or-v1-existing",
      ProviderSignInFactory = _ => new StubSignIn(
          Result.Failure<string>(new DomainError("SignInTimeout", "timed out"))),
    });

    await vm.SignInCommand.ExecuteAsync(null);

    Assert.Equal("sk-or-v1-existing", vm.OpenRouterKey);
    Assert.Equal("timed out", vm.SignInError);
    Assert.True(vm.HasSignInError);
  }

  [Fact]
  public async Task SignIn_ClearsThePreviousError_WhenItSucceeds()
  {
    StubSignIn signIn = new(Result.Failure<string>(new DomainError("SignInTimeout", "timed out")));
    SettingsViewModel vm = new(new SettingsValues { ProviderSignInFactory = _ => signIn });
    await vm.SignInCommand.ExecuteAsync(null);
    Assert.NotNull(vm.SignInError);

    signIn.Outcome = Result.Success("sk-or-v1-minted");
    await vm.SignInCommand.ExecuteAsync(null);

    Assert.Null(vm.SignInError);
    Assert.Equal("sk-or-v1-minted", vm.OpenRouterKey);
  }

  [Fact]
  public async Task SignIn_AuthorizesAgainstTheBaseUrlInTheForm()
  {
    Uri? seen = null;
    SettingsViewModel vm = new(new SettingsValues
    {
      OpenRouterBaseUrlText = "https://proxy.example",
      ProviderSignInFactory = baseUrl =>
      {
        seen = baseUrl;
        return new StubSignIn(Result.Success("sk-or-v1-minted"));
      },
    });

    await vm.SignInCommand.ExecuteAsync(null);

    Assert.NotNull(seen);
    Assert.Equal("proxy.example", seen.Host);
  }

  [Fact]
  public async Task SignIn_FallsBackToTheProviderDefault_WhenTheBaseUrlIsMalformed()
  {
    Uri? seen = null;
    SettingsViewModel vm = new(new SettingsValues
    {
      OpenRouterBaseUrlText = "not a url",
      ProviderSignInFactory = baseUrl =>
      {
        seen = baseUrl;
        return new StubSignIn(Result.Success("sk-or-v1-minted"));
      },
    });

    await vm.SignInCommand.ExecuteAsync(null);

    Assert.NotNull(seen);
    Assert.Equal("openrouter.ai", seen.Host);
  }

  [Fact]
  public void SignInLabel_StatesTheWait_WhileAnAttemptRuns()
  {
    SettingsViewModel vm = new(new SettingsValues());
    Assert.Equal("Sign in with OpenRouter", vm.SignInLabel);

    vm.IsSigningIn = true;

    Assert.Equal("Waiting for browser...", vm.SignInLabel);
  }

  /// <summary>A sign-in that returns a scripted outcome without a browser or a port.</summary>
  private sealed class StubSignIn(Result<string> outcome) : IProviderSignIn
  {
    public Result<string> Outcome { get; set; } = outcome;

    public Task<Result<string>> SignInAsync(CancellationToken ct = default) =>
        Task.FromResult(Outcome);
  }
}
