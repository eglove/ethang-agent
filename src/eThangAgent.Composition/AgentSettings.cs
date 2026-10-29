using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>OpenRouter credentials. ApiKey may be null — the provider is only offered
///     when a key is configured, and a session that selects it without one fails
///     with a structured error. Hosts source the key themselves (the Desktop reads it
///     from app preferences via the Settings modal) and overlay it with
///     <see cref="AgentSettings.WithApiKeys"/>. ManagementKey is the OPTIONAL
///     management-API key (the separate sk-or-... credentialed for /api/v1/keys);
///     null means the 'openrouter_management' tool refuses with a typed error —
///     it never gates the provider or the model loop.</summary>
public sealed record OpenRouterSettings(string? ApiKey, Uri BaseUrl, string? ManagementKey = null)
{
  /// <summary>App-preference key the Desktop stores the (protected) OpenRouter key under.</summary>
  public const string PreferenceKey = "openrouter_api_key";

  /// <summary>App-preference key the Desktop stores the (protected) OpenRouter
  ///     management key under.</summary>
  public const string ManagementKeyPreferenceKey = "openrouter_management_key";
}

/// <summary>Everything a host needs before building the core. ApiKeys may be null —
///     the host decides how to present a missing key (Desktop shows a dialog). There
///     is no configured model pin: the model is chosen per session (intelligent
///     selection on OpenRouter) or by the user through the host's model picker.</summary>
public sealed record AgentSettings(
    OpenRouterSettings OpenRouter,
    SubAgentOptions SubAgents,
    bool ComputerUse = false,
    bool VerificationGateEnabled = true,
    WatchdogSettings? Watchdog = null,
    string? WorkspaceRoot = null,
    string? SessionFilesGlobal = null,
    string? SessionFilesWorkspace = null,
    string? SkillDirectoriesGlobal = null,
    string? SkillDirectoriesWorkspace = null,
    string? SkillRegistryDefaultTarget = null,
    string? SessionId = null,
    string? TrustedDirectories = null,
    string? WorkspaceId = null)
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

  /// <summary>Returns the same settings with the stored trusted-directory list
  ///     overlaid (issue #96): the raw preference value travels to the host inside the
  ///     settings JSON, so a remote child - and the host-run ROOT turn, which is every
  ///     production turn - resolves the SAME approved external roots the app-side
  ///     session does. Null keeps whatever the caller already set (never a clobber).</summary>
  public AgentSettings WithTrustedDirectories(string? stored) => this with
  {
    TrustedDirectories = TrustedDirectories ?? stored,
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

  /// <summary>Returns the same settings bound to the workspace's STABLE IDENTITY (issue #317 D5).
  ///     <para>The root alone is not enough for the host: since #317 the id is what every durable
  ///     row keys on and the root is merely where that id currently lives, so a host that received
  ///     only the path would scope its plans, chains, and state to the path - which is precisely
  ///     the binding #317 removed, and it would silently fork a moved workspace's records.</para>
  ///     <para>Stamped by the same supervisor that stamps <see cref="SessionId"/>; null means no
  ///     identity was resolved, and the host then falls back to the path (the pre-#317 shape).</para></summary>
  public AgentSettings WithWorkspaceId(string? workspaceId) => this with
  {
    WorkspaceId = workspaceId,
  };

  /// <summary>Returns the same settings bound to the root session the host serves (spec D6):
  ///     the conversation's own agent runs host-side, so the host must know WHICH session it
  ///     is. Null leaves the host with no root session, and a root turn then fails with a
  ///     typed settle instead of running against a guessed session. The supervisor stamps
  ///     this on every launch — it is the ONLY writer, and without it every root turn
  ///     answers RootTurnUnavailable.</summary>
  public AgentSettings WithSessionId(string? sessionId) => this with
  {
    SessionId = sessionId,
  };

  /// <summary>Returns the same settings with the OpenRouter management key overlaid.
  ///     Null clears it — the 'openrouter_management' tool then refuses with a typed
  ///     error. Hosts use this to lift the key from their own credential source onto
  ///     the loaded settings; a missing management key never gates anything else.</summary>
  public AgentSettings WithManagementKey(string? openRouterManagementKey) => this with
  {
    OpenRouter = OpenRouter with { ManagementKey = openRouterManagementKey },
  };
}
