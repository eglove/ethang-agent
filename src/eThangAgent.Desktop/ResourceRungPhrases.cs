using eThangAgent.AgentDomain;

namespace eThangAgent.Desktop;

/// <summary>The ladder rungs in the words a reader can act on. ONE table, two
///     readers: the chrome renders it, and the enactment seam names its refusals with
///     it - so a refusal recorded in the audit trail reads the way the decision that
///     caused it does. The domain's enum names are internal vocabulary and never
///     reach the screen.
///     <para>"KillProgram" is a MISNOMER in production: the app stops the whole
///     conversation HOST process, not one program inside it, so the phrase says what
///     actually happens. The domain enum keeps its spec name (spec D3 owns the
///     ladder).</para></summary>
internal static class ResourceRungPhrases
{
  /// <summary>The phrase for a rung, from the domain's own action.</summary>
  public static string Of(ResourceAction action) => action switch
  {
    ResourceAction.Steer => "steer the conversation",
    ResourceAction.KillProgram => "stop the conversation's host process",
    ResourceAction.ReclaimMemory => "free the conversation's memory",
    ResourceAction.RestartConversation => "restart the conversation",
    ResourceAction.RestartApp => "restart the app",
    ResourceAction.None => "do nothing",
    _ => "act on the conversation",
  };

  /// <summary>The phrase for a rung named by the AUDIT TRAIL's raw text. The trail
  ///     writes the domain's action name, so the name is resolved back to the action
  ///     and then to its phrase.</summary>
  public static string Of(string rawRungName)
  {
    ArgumentNullException.ThrowIfNull(rawRungName);
    return rawRungName switch
    {
      nameof(ResourceAction.Steer) => Of(ResourceAction.Steer),
      nameof(ResourceAction.KillProgram) => Of(ResourceAction.KillProgram),
      nameof(ResourceAction.ReclaimMemory) => Of(ResourceAction.ReclaimMemory),
      nameof(ResourceAction.RestartConversation) => Of(ResourceAction.RestartConversation),
      nameof(ResourceAction.RestartApp) => Of(ResourceAction.RestartApp),
      nameof(ResourceAction.None) => Of(ResourceAction.None),
      _ => Of((ResourceAction)(-1)),
    };
  }
}
