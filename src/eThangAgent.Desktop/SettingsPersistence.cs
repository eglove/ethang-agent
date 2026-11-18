using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Desktop;

/// <summary>Writes a confirmed <see cref="SettingsUpdate"/> to the app preference store.
///     <para>ONE WRITER, TWO CALLERS. The main window's shell and the launcher's Settings entry
///     are the same dialog over the same store, so a save from either lands through this one
///     implementation - a second copy would be a second answer waiting to disagree about which
///     key holds what.</para>
///     <para>Every write is best effort: a store failure is logged to stderr and never fails the
///     save, so the worst case is a setting that did not land rather than a lost window.</para>
///     <para>Plaintext keys never reach durable storage: without a protector a key save is
///     skipped with a note instead of stored raw.</para></summary>
/// <param name="preferences">The app preference store, or null for a host that remembers nothing.</param>
/// <param name="keyProtector">Protects keys before they reach durable storage.</param>
internal sealed class SettingsPersistence(IAppPreferenceStore? preferences, IApiKeyProtector? keyProtector)
{
  private readonly IAppPreferenceStore? _preferences = preferences;
  private readonly IApiKeyProtector? _keyProtector = keyProtector;

  /// <summary>Persists every field of the update: the two keys (protected) and every plaintext
  ///     knob, the global file lists included. Returns the normalized keys the caller applies to
  ///     its live settings, so one normalization serves both the write and the rebind.</summary>
  /// <param name="update">The confirmed update.</param>
  /// <param name="preferredProviderId">The provider the scoped compaction choice belongs to.</param>
  public async Task<(string? OpenRouterKey, string? ManagementKey)> ApplyAsync(
      SettingsUpdate update, string preferredProviderId)
  {
    ArgumentNullException.ThrowIfNull(update);
    ArgumentException.ThrowIfNullOrWhiteSpace(preferredProviderId);
    string? openRouterKey = Normalize(update.OpenRouterApiKey);
    string? managementKey = Normalize(update.OpenRouterManagementKey);

    await PersistTextAsync(update, openRouterKey, managementKey).ConfigureAwait(false);
    PersistScoped(update, preferredProviderId);
    return (openRouterKey, managementKey);
  }

  /// <summary>Trims a field and reads a blank as 'cleared'.</summary>
  /// <param name="value">The field's text.</param>
  public static string? Normalize(string? value)
  {
    string trimmed = value?.Trim() ?? string.Empty;
    return trimmed.Length == 0 ? null : trimmed;
  }

  /// <summary>Writes one file list under its preference key: an empty list deletes the key, so
  ///     an unconfigured scope leaves nothing behind for the session factory to read.</summary>
  /// <param name="preferences">The store, or null when nothing is remembered.</param>
  /// <param name="preferenceKey">The key the list lives under.</param>
  /// <param name="entries">The rows to write.</param>
  /// <param name="serialize">The list's own serialization.</param>
  public static async Task PersistFileListAsync(IAppPreferenceStore? preferences,
      string preferenceKey, IReadOnlyList<SessionFileEntry> entries,
      Func<IReadOnlyList<SessionFileEntry>, string> serialize)
  {
    ArgumentNullException.ThrowIfNull(serialize);
    ArgumentNullException.ThrowIfNull(entries);
    if (preferences is null)
    {
      return; // test seam: nothing is remembered
    }

    // Named decision (CA1031): preference persistence must not take the window down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      bool landed = entries.Count == 0
          ? await preferences.DeleteAsync(preferenceKey).ConfigureAwait(false)
          : await preferences.SetAsync(preferenceKey, serialize(entries)).ConfigureAwait(false);
      if (!landed)
      {
        await Console.Error.WriteLineAsync($"preference write failed for '{preferenceKey}'").ConfigureAwait(false);
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync(
          $"preference write failed for '{preferenceKey}': {ex.Message}").ConfigureAwait(false);
    }
#pragma warning restore CA1031
  }

  /// <summary>Persists every non-scoped field of the update.</summary>
  private async Task PersistTextAsync(SettingsUpdate update, string? openRouterKey,
      string? managementKey)
  {
    await PersistApiKeyAsync(OpenRouterSettings.PreferenceKey, openRouterKey).ConfigureAwait(false);
    await PersistApiKeyAsync(OpenRouterSettings.ManagementKeyPreferenceKey, managementKey)
        .ConfigureAwait(false);
    await PersistPreferenceAsync(AppPreferenceCommitStyleProvider.PreferenceKey,
        update.CommitStyle.ToString()).ConfigureAwait(false);
    await PersistPreferenceAsync(AgentPreferenceKeys.MaxConcurrentAgents,
        Normalize(update.MaxConcurrentAgentsText)).ConfigureAwait(false);
    await PersistPreferenceAsync(AgentPreferenceKeys.DefaultModel,
        Normalize(update.DefaultModelText)).ConfigureAwait(false);
    await PersistPreferenceAsync(AgentPreferenceKeys.WatchdogTickInterval,
        Normalize(update.WatchdogTickText)).ConfigureAwait(false);
    await PersistPreferenceAsync(AgentPreferenceKeys.WatchdogIdleThreshold,
        Normalize(update.WatchdogIdleText)).ConfigureAwait(false);
    await PersistPreferenceAsync(AgentPreferenceKeys.WatchdogMaxWrapUpAttempts,
        Normalize(update.WatchdogWrapUpText)).ConfigureAwait(false);
    await PersistPreferenceAsync(AgentPreferenceKeys.RootTurnIdle,
        Normalize(update.RootTurnIdleText)).ConfigureAwait(false);
    await PersistPreferenceAsync(AgentPreferenceKeys.OpenRouterBaseUrl,
        Normalize(update.OpenRouterBaseUrlText)).ConfigureAwait(false);
    await PersistPreferenceAsync(AgentPreferenceKeys.ComputerUseEnabled,
        update.ComputerUse ? "true" : "false").ConfigureAwait(false);
    // The compaction handoff is a LIVE preference (issue #234): the composition reads it at every
    // compaction, so a save applies from the next compaction without a container rebuild. Absent
    // means off, so 'false' is written as a delete rather than a value.
    await PersistPreferenceAsync(SessionHandoffPreferences.Key,
        update.CompactionHandoff ? "true" : null).ConfigureAwait(false);
    // Retention window (issue #246): '0' is stored as the zero-hour window, which the sweep reads
    // as 'delete nothing'; blank deletes the key so the thirty-day default applies.
    await PersistPreferenceAsync(RetentionSweep.WindowHoursKey,
        Normalize(update.RetentionWindowText)).ConfigureAwait(false);
    // The ticked tools (plan #209) are one global preference like the rest: null is an empty
    // selection, which the store expresses as a delete rather than as a written blank.
    await PersistPreferenceAsync(PathToolPreferences.Key,
        Normalize(update.PathToolsText)).ConfigureAwait(false);
    // The two lists that apply to EVERY workspace: an empty list deletes its key.
    await PersistFileListAsync(_preferences, SessionFilePreferences.GlobalKey,
        update.GlobalSessionFiles ?? [], SessionFilePreferences.Serialize).ConfigureAwait(false);
    await PersistFileListAsync(_preferences, SkillDirectoryPreferences.GlobalKey,
        update.GlobalSkillDirectories ?? [], SkillDirectoryPreferences.Serialize).ConfigureAwait(false);
  }

  /// <summary>Persists the two scoped settings of an update: the compaction summarizer (per
  ///     provider and workspace) and the skill-registry default target. Absent values delete
  ///     their key.</summary>
  private void PersistScoped(SettingsUpdate update, string preferredProviderId)
  {
    // Compaction summarizer is per (provider, workspace): unset means Automatic - the cheapest
    // capable catalog entry resolves at compaction time.
    if (update.CompactionWorkspaceKey is { } workspaceKey)
    {
      string compactionKey = CompactionModelResolver.PreferenceKey(preferredProviderId, workspaceKey);
      _ = update.CompactionModelId is null
          ? _preferences?.DeleteAsync(compactionKey)
          : _preferences?.SetAsync(compactionKey, update.CompactionModelId);
    }

    // Skill-registry default target (plan #29 T12): absent text deletes the key (unset),
    // 'global'/'workspace' store verbatim; the loader strict-parses on read.
    if (update.SkillRegistryDefaultTarget is { } registryTarget)
    {
      _ = registryTarget.Length == 0
          ? _preferences?.DeleteAsync(AgentPreferenceKeys.SkillRegistryDefaultTarget)
          : _preferences?.SetAsync(AgentPreferenceKeys.SkillRegistryDefaultTarget, registryTarget);
    }
  }

  /// <summary>Writes one key preference: set (protected) when configured, delete when cleared.
  ///     A configured key with no protector is skipped with a note rather than stored raw.</summary>
  private async Task PersistApiKeyAsync(string preferenceKey, string? apiKey)
  {
    if (_preferences is null)
    {
      return; // test seam: nothing is remembered
    }

    if (apiKey is not null && _keyProtector is null)
    {
      await Console.Error.WriteLineAsync(
          $"no API key protector configured; '{preferenceKey}' not persisted").ConfigureAwait(false);
      return;
    }

    // Named decision (CA1031): preference persistence must not take the window down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      bool landed = apiKey is null
          ? await _preferences.DeleteAsync(preferenceKey).ConfigureAwait(false)
          : await _preferences.SetAsync(preferenceKey, _keyProtector!.Protect(apiKey)).ConfigureAwait(false);
      if (!landed)
      {
        await Console.Error.WriteLineAsync(
            $"api key preference write failed for '{preferenceKey}'").ConfigureAwait(false);
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync(
          $"api key preference write failed for '{preferenceKey}': {ex.Message}").ConfigureAwait(false);
    }
#pragma warning restore CA1031
  }

  /// <summary>Writes one plaintext preference (never a secret). A null value deletes the
  ///     preference, which is how a blank field clears it.</summary>
  private async Task PersistPreferenceAsync(string preferenceKey, string? value)
  {
    if (_preferences is null)
    {
      return; // test seam: nothing is remembered
    }

    // Named decision (CA1031): preference persistence must not take the window down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      bool landed = value is null
          ? await _preferences.DeleteAsync(preferenceKey).ConfigureAwait(false)
          : await _preferences.SetAsync(preferenceKey, value).ConfigureAwait(false);
      if (!landed)
      {
        await Console.Error.WriteLineAsync(
            $"preference write failed for '{preferenceKey}'").ConfigureAwait(false);
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync(
          $"preference write failed for '{preferenceKey}': {ex.Message}").ConfigureAwait(false);
    }
#pragma warning restore CA1031
  }
}
