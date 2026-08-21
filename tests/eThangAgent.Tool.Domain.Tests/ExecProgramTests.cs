using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecProgramTests
{
    [Fact]
    public void NullProgram_IsRejected_ExecProgramRequired()
    {
        var result = ExecProgram.Create(null, ExecOptions.Default);
        Assert.False(result.IsSuccess);
        Assert.Equal("ExecProgramRequired", result.Error!.Code);
    }

    [Fact]
    public void EmptyProgram_IsRejected_ExecProgramRequired()
    {
        var result = ExecProgram.Create("", ExecOptions.Default);
        Assert.False(result.IsSuccess);
        Assert.Equal("ExecProgramRequired", result.Error!.Code);
    }

    [Fact]
    public void OversizedProgram_IsRejected_ExecProgramTooLarge()
    {
        var options = new ExecOptions { MaxProgramChars = 10 };
        var result = ExecProgram.Create("12345678901", options);
        Assert.False(result.IsSuccess);
        Assert.Equal("ExecProgramTooLarge", result.Error!.Code);
        Assert.Contains("11 characters", result.Error!.Message);
    }

    [Fact]
    public void ProgramAtExactLimit_IsAccepted()
    {
        var options = new ExecOptions { MaxProgramChars = 10 };
        var result = ExecProgram.Create("1234567890", options);
        Assert.True(result.IsSuccess);
        Assert.Equal("1234567890", result.Value!.Text);
    }
}
