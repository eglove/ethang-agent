
namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>AppIdentity unit tests (task 17): ApplicationFrameHost.exe is never an
///     identity (the real hosting app is resolved instead), packaged apps resolve an
///     AUMID, unpackaged apps normalize to their exe path. The native AUMID lookup is
///     abstracted so these tests run without a live desktop.</summary>
public class AppIdentityTests
{
  [Fact]
  public void ApplicationFrameHost_IsNeverAnIdentity()
  {
    AppIdentity identity = AppIdentity.Resolve(pid: 4242, exePath: "C:\\Windows\\System32\\ApplicationFrameHost.exe", aumidResolver: _ => null);
    // Without a resolvable real-app AUMID the frame host is NOT a usable identity.
    Assert.True(identity.IsApplicationFrameHost);
  }

  [Fact]
  public void PlainExe_NormalizesToFullPath()
  {
    AppIdentity identity = AppIdentity.Resolve(pid: 7, exePath: "C:\\Apps\\Notepad\\notepad.exe", aumidResolver: _ => null);
    Assert.Equal("notepad.exe", identity.ExeName);
    Assert.Equal("C:\\Apps\\Notepad\\notepad.exe", identity.ExePath);
    Assert.Null(identity.Aumid);
  }

  [Fact]
  public void PackagedApp_CarriesAumid()
  {
    AppIdentity identity = AppIdentity.Resolve(pid: 9, exePath: "C:\\Program Files\\WindowsApps\\App\\app.exe", aumidResolver: _ => "MyCompany.MyApp_abc!app");
    Assert.Equal("MyCompany.MyApp_abc!app", identity.Aumid);
  }

  [Fact]
  public void EmptyExe_IsStillAValidIdentity()
  {
    AppIdentity identity = AppIdentity.Resolve(pid: 3, exePath: "", aumidResolver: _ => null);
    Assert.Equal(3, identity.Pid);
    Assert.Equal(string.Empty, identity.ExePath);
  }

  [Fact]
  public void FrameHostExe_WithRealAumid_KeepsTheRealIdentity()
  {
    AppIdentity identity = AppIdentity.Resolve(pid: 11, exePath: "C:\\Windows\\System32\\ApplicationFrameHost.exe", aumidResolver: _ => "RealApp_id!window");
    Assert.Equal("RealApp_id!window", identity.Aumid);
  }
};
