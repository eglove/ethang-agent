namespace eThangAgent.ModelDomain;

/// <summary>Provider-neutral output-verbosity knob (low, medium, high, xhigh, max).
///     Null (unset) means the provider's own default applies; provider ACLs translate it
///     onto each provider's wire format.</summary>
public enum VerbosityLevel
{
  Low,
  Medium,
  High,
  XHigh,
  Max,
}
