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
    string? tick = await preferences.GetAsync(AgentPreferenceKeys.WatchdogTickInterval).ConfigureAwait(false);
    string? idle = await preferences.GetAsync(AgentPreferenceKeys.WatchdogIdleThreshold).ConfigureAwait(false);
    string? wrapUp = await preferences.GetAsync(AgentPreferenceKeys.WatchdogMaxWrapUpAttempts).ConfigureAwait(false);

    bool remoteHost;
    SubAgentOptions subAgents;
    WatchdogSettings? watchdog;
    try
    {
      subAgents = SubAgentConfiguration.Bind(defaultModel, maxConcurrent, out remoteHost, remoteHostStored);
      watchdog = SubAgentConfiguration.BindWatchdog(tick, idle, wrapUp);
    }
    catch (InvalidOperationException ex)
    {
      // The binders own every binding rule and diagnostic (single validation
      // layer), but they predate preference keys and name the retired config
      // paths. Only the key naming is translated here; the rule text travels
      // verbatim from the binder.
      throw new InvalidOperationException(NamePreferenceKey(ex.Message), ex);
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
        Watchdog: watchdog);
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
