namespace eThangAgent.ToolDomain.Tests;

/// <summary>Byte-count helper mirroring the ACL's UTF-8 transfer-size accounting.</summary>
internal static class TestBodyLength
{
  public static long Of(string body) => System.Text.Encoding.UTF8.GetByteCount(body);
}
