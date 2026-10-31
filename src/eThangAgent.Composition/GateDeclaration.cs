namespace eThangAgent.Composition;

/// <summary>A workspace's gate declaration, as the Open Workspace dialog collects it (issue
///     #285): either the commands to run, or an explicit DECLINE.
///     <para>THE DECISION IS TRI-STATE AND ALL THREE ARE REAL (D1). <c>Declare</c> writes
///     <c>goal/check</c>; <c>Decline</c> writes <c>goal/declined</c>; and a dialog that was never
///     shown - or shown for a workspace the user left alone - writes NOTHING, which keeps the
///     workspace <c>Unset</c>. 'Nobody asked' and 'the user said no' must stay distinguishable,
///     or a recorded decision is indistinguishable from an omission.</para>
///     <para>NOTHING IS EVER AUTO-FILLED FROM A TEMPLATE. The dialog may SHOW a suggestion, but
///     an accepted suggestion must be an explicit act: a pre-filled box the user clicks past is a
///     default wearing a decision's clothes.</para></summary>
public enum GateDeclarationKind
{
  /// <summary>The dialog did not speak about the gate: write nothing, leave the workspace as it
  ///     was. This is the DEFAULT, so a caller that has not been taught about gates (a test, an
  ///     older host) cannot accidentally decline one on the user's behalf.</summary>
  Unchanged,

  /// <summary>The user stated the commands to run. They are stored verbatim at
  ///     <c>goal/check</c> and take effect for the very next completion claim.</summary>
  Declare,

  /// <summary>The user was asked and declined. Recorded at <c>goal/declined</c> so the absence
  ///     is a stated fact, and the dialog never asks again for this workspace.</summary>
  Decline,
}

/// <summary>What the dialog decided about one workspace's gate (issue #285).
///     <para>THE TWO PAYLOADS ARE MUTUALLY EXCLUSIVE BY CONSTRUCTION: a <c>Declare</c> carries
///     commands and no decline; a <c>Decline</c> carries a reason and no commands; an
///     <c>Unchanged</c> carries neither. Making them one nullable field per kind would allow a
///     record that both declares and declines, which the store would then have to arbitrate.
///     </para></summary>
/// <param name="Kind">Which decision was made.</param>
/// <param name="Commands">The commands for a <see cref="GateDeclarationKind.Declare"/>; empty
///     otherwise. Blank entries are dropped at construction, because a blank command line is not
///     a command and would fail the gate for a reason nobody can act on.</param>
/// <param name="Reason">An optional note for a <see cref="GateDeclarationKind.Decline"/>, kept
///     so the record says WHY when the user offered a reason.</param>
public sealed record GateDeclaration(GateDeclarationKind Kind,
    IReadOnlyList<string> Commands, string? Reason)
{
  /// <summary>The default: say nothing, change nothing.</summary>
  public static GateDeclaration Unchanged { get; } = new(GateDeclarationKind.Unchanged, [], null);

  /// <summary>A declaration of <paramref name="commands"/>, with blanks and surrounding
  ///     whitespace dropped.</summary>
  /// <param name="commands">The command lines, in the order they should run.</param>
  public static GateDeclaration Declare(IEnumerable<string> commands)
  {
    ArgumentNullException.ThrowIfNull(commands);
    List<string> cleaned = [.. commands
        .Where(command => !string.IsNullOrWhiteSpace(command))
        .Select(command => command.Trim())];
    return cleaned.Count == 0
        ? throw new ArgumentException("a declaration needs at least one command.", nameof(commands))
        : new GateDeclaration(GateDeclarationKind.Declare, cleaned, null);
  }

  /// <summary>An explicit decline, optionally with the user's reason.</summary>
  /// <param name="reason">Why the user declined, or null.</param>
  public static GateDeclaration Decline(string? reason = null) =>
      new(GateDeclarationKind.Decline, [],
          string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());

  /// <summary>The commands as the JSON array <c>goal/check</c> stores. Only meaningful for a
  ///     <see cref="GateDeclarationKind.Declare"/>.</summary>
  public string CommandsJson() =>
      System.Text.Json.JsonSerializer.Serialize(Commands);

  /// <summary>The recorded value for <c>goal/declined</c>: when, and why when a reason was
  ///     given. Its PRESENCE is the fact the gate reads; the payload is for a human auditing the
  ///     workspace later.</summary>
  /// <param name="now">When the decision was made.</param>
  public string DeclineJson(DateTimeOffset now) =>
      System.Text.Json.JsonSerializer.Serialize(new
      {
        declinedAt = now,
        reason = Reason,
      });
}
