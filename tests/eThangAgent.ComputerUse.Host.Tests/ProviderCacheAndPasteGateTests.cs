using System.Text.Json;
using eThangAgent.ComputerUse.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix round 5 (F4/F6/F7): the per-workspace access cache, the paste
///     foreground gate, and type_text's element-focus are pinned here. F4 tests live
///     in the Composition test project (they resolve the registered provider); this
///     file carries the F6/F7 wire pins.</summary>
public class BrokerComputerAccessProviderTests
{
  [Fact]
  public void ForWorkspace_SameRoot_Twice_ReturnsSameInstance()
  {
    BrokerRegistry registry = new(() => "host-exe-does-not-matter", root => "pipe-" + root);
    BrokerComputerAccessProvider provider = new(registry);
    string root = Path.Combine(Path.GetTempPath(), "ethang-f4-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(root);
    try
    {
      IComputerAccess first = provider.ForWorkspace(root)!;
      IComputerAccess second = provider.ForWorkspace(root)!;
      Assert.Same(first, second);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void ForWorkspace_DifferentRoots_ReturnDifferentInstances()
  {
    BrokerRegistry registry = new(() => "host-exe-does-not-matter", root => "pipe-" + root);
    BrokerComputerAccessProvider provider = new(registry);
    string rootA = Path.Combine(Path.GetTempPath(), "ethang-f4a-" + Guid.NewGuid().ToString("N"));
    string rootB = Path.Combine(Path.GetTempPath(), "ethang-f4b-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(rootA);
    _ = Directory.CreateDirectory(rootB);
    try
    {
      IComputerAccess first = provider.ForWorkspace(rootA)!;
      IComputerAccess second = provider.ForWorkspace(rootB)!;
      Assert.NotSame(first, second);
    }
    finally
    {
      Directory.Delete(rootA, recursive: true);
      Directory.Delete(rootB, recursive: true);
    }
  }

  [Fact]
  public void Paste_MismatchedForeground_IsRefusedWithNothingDispatched()
  {
    // F6: paste without an element target gates like click: the foreground window
    // must match the target app, else foreground_required and NO clipboard write.
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 4242, actual: 1);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "paste",
      JsonDocument.Parse("{\"text\":\"hi\",\"app_ref\":{\"pid\":4242}}").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
  }

  [Fact]
  public void TypeText_ElementTarget_FocusesTheElementThenSendsText_NoRealInput()
  {
    // F7: type with an element target runs the element_focus path first, then the
    // real text send; the recording sinks prove the ORDER without real input.
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    CountingElementOps ops = new();
    server.InputDispatch.SetElementOps(ops);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "type_text",
      JsonDocument.Parse("{\"text\":\"hello\",\"element\":5}").RootElement, connectionId: 1);
    Assert.Null(reply.Error);
    Assert.Equal([5], ops.Focused);       // the element_focus path ran
    Assert.Equal(1, server.InputDispatch.SendInputCalls); // then the text dispatch
  }

  [Fact]
  public void TypeText_NoTarget_TypesAtCurrentFocus_NoElementCall()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    CountingElementOps ops = new();
    server.InputDispatch.SetElementOps(ops);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "type_text",
      JsonDocument.Parse("{\"text\":\"hello\"}").RootElement, connectionId: 1);
    Assert.Null(reply.Error);
    Assert.Empty(ops.Focused);
    Assert.Equal(1, server.InputDispatch.SendInputCalls);
  }

  [Fact]
  public void TypeText_UnfocusableElement_IsHonestRefusal_NoTextDispatched()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    server.InputDispatch.SetElementOps(new BoundsOnlyResolver()); // bounds-only: cannot focus
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "type_text",
      JsonDocument.Parse("{\"text\":\"hello\",\"element\":3}").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("invalid_request", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }
}

/// <summary>A bounds-only resolver: answers bounds requests but implements no
///     IElementActionSink, so focus-capable type_text must refuse honestly.</summary>
internal sealed class BoundsOnlyResolver : IElementBoundsResolver
{
  public bool TryResolveBoundsCenter(int element, out int centerX, out int centerY)
  {
    centerX = 10;
    centerY = 10;
    return true;
  }
}
