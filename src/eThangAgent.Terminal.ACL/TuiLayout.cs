namespace eThangAgent.Terminal.ACL;

/// <summary>Row layout for the full-screen TUI: transcript on top, input above a statusline.</summary>
public readonly record struct TuiLayout(int TranscriptTop, int TranscriptHeight, int InputRow, int StatusRow)
{
    public static TuiLayout Compute(int height)
    {
        var transcriptHeight = Math.Max(1, height - 2);
        return new TuiLayout(0, transcriptHeight, transcriptHeight, transcriptHeight + 1);
    }
}
