namespace eThangAgent.ModelDomain;

/// <summary>What a session allows the provider to keep of its prompts, in the terms the
///     provider publishes its own data policy in.
///     <para>THREE TIERS, NOT A TOGGLE. The old boolean asked one question - may this session
///     reach a provider that retains anything - and answered it by refusing every endpoint the
///     provider did not list as zero-retention. The provider's framework has a middle tier: an
///     endpoint may keep prompts solely to detect and prevent abuse and never train on them,
///     which is a real choice a user can make and which the boolean could not express.</para>
///     <para>THE FIRST TWO ARE ENFORCED BY THE HARNESS, the third is not: a model with no
///     endpoint eligible under the stated tier fails the turn rather than silently routing to
///     a provider that keeps the prompts. <see cref="RetentionPolicies.IsEnforced"/> is the
///     single place that distinction is stated.</para></summary>
public enum RetentionPolicy
{
  /// <summary>Nothing is kept after processing. The strictest tier, and the default.</summary>
  ZeroDataRetention,

  /// <summary>Prompts may be kept only to detect and prevent abuse, never for training.</summary>
  SafetyRetention,

  /// <summary>No restriction: the provider's own policy applies and may include training.</summary>
  RetentionAndTraining,
}

/// <summary>The retention tiers' order and presentation, kept as PURE functions beside the
///     enum so the dropdown, the status line, and the enforcement gate cannot each invent
///     their own spelling of the same tier.</summary>
public static class RetentionPolicies
{
  /// <summary>The tiers in the order the picker offers them: the default first, then the
  ///     two a user loosens to.</summary>
  public static IReadOnlyList<RetentionPolicy> All { get; } =
      [RetentionPolicy.ZeroDataRetention, RetentionPolicy.SafetyRetention,
       RetentionPolicy.RetentionAndTraining];

  /// <summary>The dropdown label: the tier's short name.</summary>
  public static string DisplayName(RetentionPolicy policy) => policy switch
  {
    RetentionPolicy.ZeroDataRetention => "ZDR",
    RetentionPolicy.SafetyRetention => "Safety Retention",
    RetentionPolicy.RetentionAndTraining => "Retention and Training",
    _ => throw Unhandled(policy),
  };

  /// <summary>The status line's name for the tier in force. Every tier is named, so a
  ///     session that has loosened its policy says so instead of showing silence.</summary>
  public static string StatusBadge(RetentionPolicy policy) => policy switch
  {
    RetentionPolicy.ZeroDataRetention => "no data retention",
    RetentionPolicy.SafetyRetention => "safety retention only",
    RetentionPolicy.RetentionAndTraining => "retention and training allowed",
    _ => throw Unhandled(policy),
  };

  /// <summary>True when the harness enforces this tier against the provider's eligible
  ///     endpoints, so an unservable model fails the turn. False only for
  ///     <see cref="RetentionPolicy.RetentionAndTraining"/>, which states no restriction to
  ///     enforce.</summary>
  public static bool IsEnforced(RetentionPolicy policy) => policy switch
  {
    RetentionPolicy.ZeroDataRetention => true,
    RetentionPolicy.SafetyRetention => true,
    RetentionPolicy.RetentionAndTraining => false,
    _ => throw Unhandled(policy),
  };

  /// <summary>The refusal for a member this build does not know. Written once so every
  ///     switch states the same consequence: an unhandled tier would silently put a request
  ///     on the wire under a retention promise the user did not make.</summary>
  private static ArgumentOutOfRangeException Unhandled(RetentionPolicy policy)
      => new(nameof(policy), policy,
          "Every retention tier must state its own name, badge, and enforcement; an "
          + "unhandled member would silently send a request under a policy nobody chose.");
}
