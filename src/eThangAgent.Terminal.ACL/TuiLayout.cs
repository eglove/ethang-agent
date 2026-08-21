namespace eThangAgent.Terminal.ACL;

/// <summary>Row layout for the full-screen TUI: transcript, separator rule, input, statusline.</summary>
public readonly record struct TuiLayout(int TranscriptTop, int TranscriptHeight, int SeparatorRow, int InputRow, int StatusRow)
{
    public static TuiLayout Compute(int height)
    {
        if (height >= 4)
        {
            var transcriptHeight = height - 3;
            return new TuiLayout(0, transcriptHeight, transcriptHeight, transcriptHeight + 1, transcriptHeight + 2);
        }

        // Too small for a separator: no rule, transcript and input share the space.
        var clamped = Math.Max(1, height - 2);
        return new TuiLayout(0, clamped, -1, clamped, clamped + 1);
    }
}
