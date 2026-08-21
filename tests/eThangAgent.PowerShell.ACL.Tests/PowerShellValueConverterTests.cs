using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using eThangAgent.PowerShell.ACL;

namespace eThangAgent.PowerShell.ACL.Tests;

public class PowerShellValueConverterTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void String_ConvertsVerbatim()
    {
        var element = Parse(PowerShellValueConverter.ToJson("hello"));

        Assert.Equal("hello", element.GetString());
    }

    [Fact]
    public void Int_Converts()
    {
        var element = Parse(PowerShellValueConverter.ToJson(42));

        Assert.Equal(42, element.GetInt32());
    }

    [Fact]
    public void Bool_Converts()
    {
        var element = Parse(PowerShellValueConverter.ToJson(true));

        Assert.True(element.GetBoolean());
    }

    [Fact]
    public void Null_Converts()
    {
        var element = Parse(PowerShellValueConverter.ToJson(null));

        Assert.Equal(JsonValueKind.Null, element.ValueKind);
    }

    [Fact]
    public void Hashtable_ConvertsToObject()
    {
        var table = new Hashtable { ["path"] = "a.txt", ["startLine"] = 1 };

        var element = Parse(PowerShellValueConverter.ToJson(table));

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("a.txt", element.GetProperty("path").GetString());
        Assert.Equal(1, element.GetProperty("startLine").GetInt32());
    }

    [Fact]
    public void NestedHashtable_AndArray_Convert()
    {
        var table = new Hashtable
        {
            ["outer"] = new Hashtable { ["inner"] = "x" },
            ["items"] = new object[] { 1, "two", true },
        };

        var element = Parse(PowerShellValueConverter.ToJson(table));

        Assert.Equal("x", element.GetProperty("outer").GetProperty("inner").GetString());
        var items = element.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal(1, items[0].GetInt32());
        Assert.Equal("two", items[1].GetString());
        Assert.True(items[2].GetBoolean());
    }

    [Fact]
    public void PSObjectWrappedHashtable_Unwraps()
    {
        var wrapped = PSObject.AsPSObject(new Hashtable { ["a"] = 1 });

        var element = Parse(PowerShellValueConverter.ToJson(wrapped));

        Assert.Equal(1, element.GetProperty("a").GetInt32());
    }

    [Fact]
    public void PSCustomObject_ConvertsProperties()
    {
        using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();
        using var powershell = System.Management.Automation.PowerShell.Create(runspace);
        powershell.AddScript("[pscustomobject]@{ a = 1 }");
        var json = PowerShellValueConverter.ToJson(powershell.Invoke()[0]);

        Assert.Contains("\"a\":1", json);
    }

    [Fact]
    public void ScriptBlock_IsRejected_WithClearError()
    {
        var scriptBlock = ScriptBlock.Create("{ 1 + 1 }");

        var ex = Assert.Throws<ExecInputConversionException>(
            () => PowerShellValueConverter.ToJson(scriptBlock));
        Assert.Contains("cannot be converted", ex.Message);
    }

    [Fact]
    public void DeeplyNested_Rejects_AtDepthLimit()
    {
        object current = "leaf";
        for (var i = 0; i < 40; i++)
            current = new Hashtable { ["n"] = current };

        Assert.Throws<ExecInputConversionException>(
            () => PowerShellValueConverter.ToJson(current));
    }
}
