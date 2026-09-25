using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>OpenRouter credentials. ApiKey may be null — the provider is only offered
///     when a key is configured, and a session that selects it without one fails
///     with a structured error. Hosts source the key themselves (the Desktop reads it
///     from app preferences via the Settings modal) and overlay it with
///     <see cref="AgentSettings.WithApiKeys"/>.</summary>
public sealed record OpenRouterSettings(string? ApiKey, Uri BaseUrl)
{
  /// <summary>App-preference key the Desktop stores the (protected) OpenRouter key under.</summary>
  public const string PreferenceKey = "openrouter_api_key";
}

/// <summary>Everything a host needs before building the core. ApiKeys may be null —
///     the host decides how to present a missing key (Desktop shows a dialog). There
///     is no configured model pin: the model is chosen per session (intelligent
///     selection on OpenRouter) or by the user through the host's model picker.</summary>
public sealed record AgentSettings(
    OpenRouterSettings OpenRouter,
    SubAgentOptions SubAgents,
    bool RemoteHost = false,
    bool ComputerUse = false,
    bool VerificationGateEnabled = true,
    WatchdogSettings? Watchdog = null,
    string? WorkspaceRoot = null,
    string? SessionFilesGlobal = null,
    string? SessionFilesWorkspace = null,
    string? SkillDirectoriesGlobal = null,
    string? SkillDirectoriesWorkspace = null,
    string? SkillRegistryDefaultTarget = null)
{
  // Watchdog (W1.2): null means no SubAgent:Watchdog configuration — the host watchdog
  // runs WatchdogOptions.Default. The value travels to the child host inside the
  // settings JSON the RemoteHostSupervisor writes.
  /// <summary>True when an OpenRouter API key (non-blank) is configured.</summary>
  public bool HasOpenRouter => !string.IsNullOrWhiteSpace(OpenRouter.ApiKey);

  /// <summary>Returns the same settings with the provider API key overlaid. Null
  ///     clears the key — the provider stops being offered. Hosts use this to lift
  ///     the key from their own credential source (app preferences) onto the loaded
  ///     settings.</summary>
  public AgentSettings WithApiKeys(string? openRouterApiKey) => this with
  {
    OpenRouter = OpenRouter with { ApiKey = openRouterApiKey },
  };

  /// <summary>Returns the same settings with the stored session-file lists overlaid
  ///     (E): the raw preference values travel to the host inside the settings JSON,
  ///     so remote children receive the SAME configured files as the app-side session.
  ///     Null arguments keep whatever the caller already set - never a clobber.</summary>
  public AgentSettings WithSessionFiles(string? globalStored, string? workspaceStored) => this with
  {
    SessionFilesGlobal = SessionFilesGlobal ?? globalStored,
    SessionFilesWorkspace = SessionFilesWorkspace ?? workspaceStored,
  };

  /// <summary>Returns the same settings with the stored skill-directory lists overlaid
  ///     (skill-routing Phase 1): the raw preference values travel to the host inside the
  ///     settings JSON, so remote children receive the SAME configured skill
  ///     directories as the app-side session. Null arguments keep whatever the caller
  ///     already set - never a clobber.</summary>
  public AgentSettings WithSkillDirectories(string? globalStored, string? workspaceStored) => this with
  {
    SkillDirectoriesGlobal = SkillDirectoriesGlobal ?? globalStored,
    SkillDirectoriesWorkspace = SkillDirectoriesWorkspace ?? workspaceStored,
  };

  /// <summary>Returns the same settings with the verification gate toggled. The
  ///     gate is on by default; hosts expose the toggle in their settings surface
  ///     (the Desktop's Settings window).</summary>
  public AgentSettings WithVerificationGate(bool enabled) => this with
  {
    VerificationGateEnabled = enabled,
  };

  public AgentSettings WithSkillRegistryDefaultTarget(string? target) => this with
  {
    SkillRegistryDefaultTarget = target,
  };

  public AgentSettings WithWorkspaceRoot(string? workspaceRoot) => this with
  {
    WorkspaceRoot = workspaceRoot,
  };
}
