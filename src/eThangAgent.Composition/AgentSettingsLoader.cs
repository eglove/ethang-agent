using System.Globalization;
using eThangAgent.AgentDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.Zai.ACL;

namespace eThangAgent.Composition;

/// <summary>App-preference keys for every non-secret host setting: absent means
///     default, a present value is validated strictly by the loader, clearing
///     persists as a delete. Secrets never appear here — API keys keep their own
///     preference keys and the DPAPI-protected path.</summary>
public static class AgentPreferenceKeys
{
  public const string MaxConcurrentAgents = "subagent_max_concurrent_agents";
  public const string DefaultModel = "subagent_default_model";
  public const string RemoteHost = "subagent_remote_host";
  public const string ComputerUseEnabled = "computer_use_enabled";
  public const string VerificationGateEnabled = "verification_gate_enabled";
  public const string SkillRegistryDefaultTarget = "skill_registry:default_target";
  public const string WatchdogTickInterval = "subagent_watchdog_tick_interval";
  public const string WatchdogIdleThreshold = "subagent_watchdog_idle_threshold";
  public const string WatchdogMaxWrapUpAttempts = "subagent_watchdog_max_wrap_up_attempts";
  public const string OpenRouterBaseUrl = "openrouter_base_url";
  public const string ZaiBaseUrl = "zai_base_url";
}

/// <summary>Shipped defaults for settings the UI pre-fills. MaxConcurrentAgents is
///     the value the retired appsettings.json shipped; an absent preference means
///     this — never an error, never a different silent value.</summary>
public static class AgentSettingsDefaults
{
  public const int MaxConcurrentAgents = 4;
}

/// <summary>Loads host settings from app preferences — the single configuration
///     source (no appsettings.json, no config environment variables). Strict where
///     the retired file binder was: a present-but-invalid stored value throws
///     InvalidOperationException naming the key, never coerced; absent keys bind
///     as absent, and MaxConcurrentAgents falls back to the shipped default. The
///     SubAgentConfiguration binders are reused verbatim so their error matrix
///     cannot drift from the binding rules.</summary>
public static class AgentSettingsLoader
{
  public static async Task<AgentSettings> LoadAsync(IAppPreferenceStore preferences)
  {
    ArgumentNullException.ThrowIfNull(preferences);

    string? defaultModel = await preferences.GetAsync(AgentPreferenceKeys.DefaultModel).ConfigureAwait(false);
    string? maxConcurrentStored = await preferences.GetAsync(AgentPreferenceKeys.MaxConcurrentAgents).ConfigureAwait(false);
    string maxConcurrent = maxConcurrentStored ??
        AgentSettingsDefaults.MaxConcurrentAgents.ToString(CultureInfo.InvariantCulture);
    string? remoteHostStored = await preferences.GetAsync(AgentPreferenceKeys.RemoteHost).ConfigureAwait(false);
    string? computerUseStored = await preferences.GetAsync(AgentPreferenceKeys.ComputerUseEnabled).ConfigureAwait(false);
    string? verificationGateStored = await preferences.GetAsync(AgentPreferenceKeys.VerificationGateEnabled).ConfigureAwait(false);
    string? registryTargetStored = await preferences.GetAsync(AgentPreferenceKeys.SkillRegistryDefaultTarget).ConfigureAwait(false);
    string? tick = await preferences.GetAsync(AgentPreferenceKeys.WatchdogTickInterval).ConfigureAwait(false);
    string? idle = await preferences.GetAsync(AgentPreferenceKeys.WatchdogIdleThreshold).ConfigureAwait(false);
    string? wrapUp = await preferences.GetAsync(AgentPreferenceKeys.WatchdogMaxWrapUpAttempts).ConfigureAwait(false);

    bool remoteHost;
    bool computerUse;
    SubAgentOptions subAgents;
    bool verificationGate;
    string? registryTarget;
    WatchdogSettings? watchdog;
    try
    {
      subAgents = SubAgentConfiguration.Bind(defaultModel, maxConcurrent, out remoteHost, remoteHostStored);
      watchdog = SubAgentConfiguration.BindWatchdog(tick, idle, wrapUp);
      computerUse = ParseComputerUseEnabled(computerUseStored);
      verificationGate = ParseVerificationGateEnabled(verificationGateStored);
      registryTarget = ParseRegistryDefaultTarget(registryTargetStored);
    }
    catch (InvalidOperationException ex)
    {
      // The binders own every binding rule and diagnostic (single validation
      // layer), but they predate preference keys and name the retired config
      // paths. Only the key naming is translated here; the rule text travels
      // verbatim from the binder.
      throw WithPreferenceKeyName(ex);
    }

    return new AgentSettings(
        new OpenRouterSettings(null, await ReadBaseUrlAsync(preferences,
#pragma warning disable S1075 // Anchored provider default; per-host preference overrides it.
            AgentPreferenceKeys.OpenRouterBaseUrl, "https://openrouter.ai").ConfigureAwait(false)),
#pragma warning restore S1075
        new ZaiSettings(null, await ReadBaseUrlAsync(preferences,
            AgentPreferenceKeys.ZaiBaseUrl, ZaiConfiguration.DefaultBaseUrl).ConfigureAwait(false)),
        subAgents,
        RemoteHost: remoteHost,
        ComputerUse: computerUse,
        VerificationGateEnabled: verificationGate,
        Watchdog: watchdog,
        SkillRegistryDefaultTarget: registryTarget);
  }

  /// <summary>Re-surfaces a binder error with this loader's preference-key names —
  ///     the one shared translation for every path that binds through the
  ///     SubAgentConfiguration binders (LoadAsync and the Desktop's store-less
  ///     rebind), so surfaced error naming cannot diverge between them. Only the
  ///     key naming is translated; the rule text travels verbatim and the original
  ///     error rides as the inner exception.</summary>
  public static InvalidOperationException WithPreferenceKeyName(InvalidOperationException binderError)
  {
    ArgumentNullException.ThrowIfNull(binderError);
    return new InvalidOperationException(NamePreferenceKey(binderError.Message), binderError);
  }

  /// <summary>Stored absolute URI or the anchored provider default. Invalid stored
  ///     text is a named error — the same contract the retired environment binding
  ///     enforced, now naming the preference key.</summary>
  // CA1054: the stored preference is deliberately raw text — parsing is the
  // loader's job (same raw-text contract as AgentSettings.LocalSettings.BaseUrlText).
#pragma warning disable CA1054
  public static Uri BindBaseUrl(string? stored, string key, string defaultUrl)
  {
    if (string.IsNullOrWhiteSpace(stored))
    {
      return new Uri(defaultUrl);
    }

    try
    {
      return new Uri(stored);
    }
    catch (UriFormatException)
    {
      throw new InvalidOperationException(
          $"{key} must be a valid absolute URI, got '{stored}'.");
    }
  }
#pragma warning restore CA1054

  /// <summary>computer_use_enabled - optional; only "true"/"false" (any case) bind;
  ///     anything else is a startup error naming the key. Absent is legal and means false
  ///     (the ParseRemoteHost pattern).</summary>
  private static bool ParseComputerUseEnabled(string? stored)
  {
    return stored switch
    {
      null => false,
      not null when bool.TryParse(stored, out bool parsed) => parsed,
      _ => throw new InvalidOperationException(
          $"{AgentPreferenceKeys.ComputerUseEnabled} must be 'true' or 'false', got '{stored}'."),
    };
  }

  /// <summary>verification_gate_enabled - optional; absent means ENABLED (the
  ///     gate's default-on decision); only "true"/"false" bind, anything else is
  ///     a startup error naming the key.</summary>
  private static bool ParseVerificationGateEnabled(string? stored)
  {
    return stored switch
    {
      null => true,
      "true" => true,
      "false" => false,
      _ => throw new InvalidOperationException(
          $"{AgentPreferenceKeys.VerificationGateEnabled} must be 'true' or 'false', got '{stored}'."),
    };
  }

  /// <summary>skill_registry:default_target - optional; absent means unset (installs
  ///     then demand an explicit target and error NoTarget); present must be exactly
  ///     'global' or 'workspace' (trimmed), anything else is a startup error naming
  ///     the key (the watchdog-knob pattern).</summary>
  private static string? ParseRegistryDefaultTarget(string? stored)
  {
    return stored switch
    {
      null => null,
      "global" => "global",
      "workspace" => "workspace",
      _ => throw new InvalidOperationException(
          $"{AgentPreferenceKeys.SkillRegistryDefaultTarget} must be 'global' or 'workspace', got '{stored}'."),
    };
  }
  /// <summary>Maps the binders' retired config-path key names onto this loader's
  ///     preference keys, so every surfaced error names the key that stores the
  ///     value. Unknown text passes through unchanged.</summary>
  private static string NamePreferenceKey(string message)
  {
    return message
        .Replace("SubAgent:Watchdog:TickInterval", AgentPreferenceKeys.WatchdogTickInterval, StringComparison.Ordinal)
        .Replace("SubAgent:Watchdog:IdleThreshold", AgentPreferenceKeys.WatchdogIdleThreshold, StringComparison.Ordinal)
        .Replace("SubAgent:Watchdog:MaxWrapUpAttempts", AgentPreferenceKeys.WatchdogMaxWrapUpAttempts, StringComparison.Ordinal)
        .Replace("SubAgent:MaxConcurrentAgents", AgentPreferenceKeys.MaxConcurrentAgents, StringComparison.Ordinal)
        .Replace("SubAgent:DefaultModel", AgentPreferenceKeys.DefaultModel, StringComparison.Ordinal)
        .Replace("SubAgent:RemoteHost", AgentPreferenceKeys.RemoteHost, StringComparison.Ordinal);
  }

  private static async Task<Uri> ReadBaseUrlAsync(
      IAppPreferenceStore preferences, string key, string defaultUrl)
  {
    string? stored = await preferences.GetAsync(key).ConfigureAwait(false);
    return BindBaseUrl(stored, key, defaultUrl);
  }
}
