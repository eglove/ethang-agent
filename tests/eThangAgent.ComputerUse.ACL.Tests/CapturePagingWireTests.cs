namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>The paging window survives the wire: the broker's element_offset /
///     element_total / element_truncated keys parse into the capture, reach the
///     observation, and render as the model-facing paging line. A capture that omits
///     them (an older broker) reads as a complete single page rather than as a
///     truncation of unknown size.</summary>
public class CapturePagingWireTests
{
  private static BrokerReply Reply(string resultJson) =>
      new(1, System.Text.Json.JsonElement.Parse(resultJson), null);

  private const string Header =
      "\"state_id\":\"s-1\",\"snapshot_mode\":\"full\"," +
      "\"app\":{\"pid\":42,\"name\":\"app\",\"exe\":\"a.exe\",\"aumid\":\"\"}," +
      "\"window\":{\"title\":\"w\",\"window_id\":7,\"bounds\":[0,0,10,10],\"surface_kind\":\"window\",\"surface_lifecycle\":\"stable\"}";

  private static string Row(int index) =>
      "{\"index\":" + index + ",\"role\":\"Button\",\"kind\":\"control\",\"title\":\"b" + index +
      "\",\"value\":null,\"bounds\":[0,0,1,1],\"enabled\":true,\"editable\":false,\"actions\":[]," +
      "\"focused\":false,\"selected\":false,\"pressable\":true,\"has_menu\":false}";

  [Fact]
  public void PagingFields_ParseIntoTheCapture()
  {
    string json = "{" + Header + ",\"elements\":[" + Row(400) + "]," +
        "\"element_offset\":400,\"element_total\":2129,\"element_truncated\":false}";
    CaptureAppResult capture = Assert.IsType<CaptureAppResult>(CaptureAppResult.From(Reply(json)));
    Assert.Equal(400, capture.ElementOffset);
    Assert.Equal(2129, capture.ElementTotal);
    Assert.False(capture.ElementTruncated);
  }

  [Fact]
  public void PagingFields_Absent_ReadAsACompleteSinglePage()
  {
    string json = "{" + Header + ",\"elements\":[" + Row(0) + "]}";
    CaptureAppResult capture = Assert.IsType<CaptureAppResult>(CaptureAppResult.From(Reply(json)));
    Assert.Equal(0, capture.ElementOffset);
    Assert.Equal(1, capture.ElementTotal);
    Assert.False(capture.ElementTruncated);
  }

  [Fact]
  public void Renderer_DisclosesTheNextPage_WhenTheTreeContinues()
  {
    string json = "{" + Header + ",\"elements\":[" + Row(0) + "," + Row(1) + "]," +
        "\"element_offset\":0,\"element_total\":100,\"element_truncated\":false}";
    CaptureAppResult capture = Assert.IsType<CaptureAppResult>(CaptureAppResult.From(Reply(json)));
    string text = TreeTextRenderer.Render(capture);
    Assert.Contains("paging: showing elements 0-1 of 100", text, StringComparison.Ordinal);
    Assert.Contains("offset=2", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Renderer_SaysNothing_WhenTheWholeTreeTravelled()
  {
    string json = "{" + Header + ",\"elements\":[" + Row(0) + "," + Row(1) + "]," +
        "\"element_offset\":0,\"element_total\":2,\"element_truncated\":false}";
    CaptureAppResult capture = Assert.IsType<CaptureAppResult>(CaptureAppResult.From(Reply(json)));
    string text = TreeTextRenderer.Render(capture);
    Assert.DoesNotContain("paging:", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Renderer_ReportsATruncatedTree_AsAFloorWithNoFurtherPages()
  {
    string json = "{" + Header + ",\"elements\":[" + Row(0) + "]," +
        "\"element_offset\":0,\"element_total\":1,\"element_truncated\":true}";
    CaptureAppResult capture = Assert.IsType<CaptureAppResult>(CaptureAppResult.From(Reply(json)));
    string text = TreeTextRenderer.Render(capture);
    Assert.Contains("at least 1", text, StringComparison.Ordinal);
    Assert.Contains("depth cap", text, StringComparison.Ordinal);
  }
}
