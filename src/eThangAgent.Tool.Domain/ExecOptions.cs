namespace eThangAgent.ToolDomain;

public sealed record ExecOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxProgramChars { get; init; } = 64 * 1024;
    public int MaxOutputChars { get; init; } = 50 * 1024;
    public int MaxErrorChars { get; init; } = 20 * 1024;
    public int MaxParseErrors { get; init; } = 10;
    public string ArtifactDirectory { get; init; } = Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? ".", "eThangAgent", "exec-artifacts");

    public static ExecOptions Default { get; } = new();
}
