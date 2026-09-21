namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Cached relaxed-encoding options: raw multi-byte UTF-8 travels the wire.</summary>
internal static class RelaxedOptions
{
  public static readonly System.Text.Json.JsonSerializerOptions Instance = new(System.Text.Json.JsonSerializerDefaults.Web)
  {
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  };
}
