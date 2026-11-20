using System.Globalization;
using System.Text.Json;

namespace StubBroker;

/// <summary>The stub broker's controller lease: one owner, connection id 1 being this
///     stub's only connection. It mirrors the real broker's wire contract - input is
///     refused with controller_busy unless the calling connection owns the lease, and
///     both stop methods release it.</summary>
internal sealed class LeaseState
{
    private int? _owner;

    public string Route(int id, string method) => method switch
    {
        "controller_takeover" => Take(id),
        "controller_status" => Status(id),
        "controller_stop" => Stop(id),
        "stop_computer_control" => Stop(id),
        _ when method.Contains("deny", StringComparison.Ordinal) => Wire.Reply(id, new { action_sent = false, dispatch_status = "possibly_sent", effect_evidence = "unchanged" }),
        _ => Input(id, method),
    };

    private string Take(int id) => _owner is { } held
        ? Wire.Fail(id, "controller_busy", "another connection holds the controller lease (owner=" + Wire.Num(held) + ").", "owner=" + Wire.Num(held))
        : Grant(id);

    private string Status(int id) => Wire.Reply(id, new { owned = _owner == 1, owner = _owner });

    private string Stop(int id)
    {
        if (_owner != 1)
        {
            return Wire.Fail(id, "invalid_request", "stop_computer_control ignored: this connection does not hold the controller lease (owner=none); nothing was dispatched.", "action_sent=false");
        }

        _owner = null;
        return Accepted(id);
    }

    private string Input(int id, string method)
    {
        if (!IsInput(method) || _owner == 1)
        {
            return Accepted(id);
        }

        string ownerText = _owner is { } held ? Wire.Num(held) : "none";
        return Wire.Fail(id, "controller_busy", "input requires the controller lease; take over first (owner=" + ownerText + ").", "owner=" + ownerText);
    }

    private string Grant(int id)
    {
        _owner = 1;
        return Wire.Reply(id, new { owned = true });
    }

    private static string Accepted(int id) => Wire.Reply(id, new { action_sent = true, dispatch_status = "accepted" });

    private static bool IsInput(string method) => method is "click" or "scroll" or "drag" or "type_text" or "press_key" or "hold_key" or "paste"
        or "element_focus" or "element_set_value" or "element_perform_action" or "element_select_text" or "element_press";
}

/// <summary>Frame rendering: one JSON object per line, as the real broker writes it.</summary>
internal static class Wire
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Reply(int id, object result) => JsonSerializer.Serialize(new { id, result }, Options);

    public static string Fail(int id, string code, string message, string? details = null) =>
        JsonSerializer.Serialize(new { id, error = new { code, message, details } }, Options);

    public static int GetId(string frame)
    {
        using JsonDocument d = JsonDocument.Parse(frame);
        return d.RootElement.GetProperty("id").GetInt32();
    }
}
