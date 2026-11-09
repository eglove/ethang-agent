namespace eThangAgent.ToolDomain;

/// <summary>The model serving the turn currently executing on this async flow.
///     <para>ASYNC-LOCAL, so the value flows into the tools a turn executes and never
///     sideways: concurrent sessions in one process cannot observe each other's model.
///     The run loop sets it around each tool call and restores the previous value
///     afterwards. Null means no model is known, and a tool that attributes work
///     renders no attribution.</para></summary>
public static class AmbientServingModel
{
  private static readonly AsyncLocal<string?> CurrentValue = new();

  /// <summary>The serving model id for the current async flow, or null when unset.</summary>
  public static string? Current
  {
    get => CurrentValue.Value;
    set => CurrentValue.Value = value;
  }
}
