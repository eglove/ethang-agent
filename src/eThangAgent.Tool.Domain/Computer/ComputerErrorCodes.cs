namespace eThangAgent.ToolDomain;

/// <summary>The Computer Use error-code surface (spec 2.2): the only codes an
///     <see cref="IComputerAccess"/> failure may carry, plus the retry-hint
///     partition the tool renders on every error block — the model reads the
///     hint verbatim to decide its next move.</summary>
public static class ComputerErrorCodes
{
  public const string AppNotFound = "APP_NOT_FOUND";
  public const string AmbiguousApp = "AMBIGUOUS_APP";
  public const string ElementUnavailable = "ELEMENT_UNAVAILABLE";
  public const string StaleState = "STALE_STATE";
  public const string NotSettable = "NOT_SETTABLE";
  public const string NotSelectable = "NOT_SELECTABLE";
  public const string ActionUnavailable = "ACTION_UNAVAILABLE";
  public const string ForegroundRequired = "FOREGROUND_REQUIRED";
  public const string ControllerBusy = "CONTROLLER_BUSY";
  public const string ControlStopped = "CONTROL_STOPPED";
  public const string HelperUnavailable = "HELPER_UNAVAILABLE";
  public const string VersionMismatch = "VERSION_MISMATCH";
  public const string Timeout = "TIMEOUT";
  public const string InvalidApp = "INVALID_APP";
  public const string LaunchFailed = "LAUNCH_FAILED";
  public const string Internal = "INTERNAL";

  /// <summary>The retry hint for one surface code: 'reobserve' when the desktop
  ///     has moved under the model (fresh observe first), 'never' when repeating
  ///     cannot help, 'retry' when the same call may succeed later. Unknown codes
  ///     default to 'retry' — the conservative, optimistic hint.</summary>
  public static string RetryHint(string code) => code switch
  {
    ElementUnavailable => "reobserve",
    StaleState => "reobserve",
    ControllerBusy => "never",
    ControlStopped => "never",
    NotSettable => "never",
    NotSelectable => "never",
    ActionUnavailable => "never",
    VersionMismatch => "never",
    _ => "retry",
  };
}
