using TapeLibNET.Services;

namespace TapeWinNET;

/// <summary>
/// The media-identity prompt (<see cref="MediaMismatchChoice"/>): a <see cref="MediaChangeDialog"/> with Retry / Proceed
///  / Always proceed / Abort buttons, the explanation in the severity-graded notice pane and the offending header's own
///  description in the code pane.
/// </summary>
/// <remarks>
/// No XAML of its own — the layout is <see cref="MediaChangeDialog"/>'s. The constructor and <see cref="Result"/> are
///  unchanged apart from the severity type, so every caller (media mismatch, set anomaly) keeps working.
/// </remarks>
public class MediaMismatchDialog : MediaChangeDialog
{
    private readonly MediaMismatchChoice[] m_choices;

    /// <summary>The user's choice. <see cref="MediaMismatchChoice.Abort"/> when the window was closed without one.</summary>
    public MediaMismatchChoice Result => SelectedIndex >= 0 ? m_choices[SelectedIndex] : MediaMismatchChoice.Abort;

    /// <summary>Creates a media-mismatch prompt.</summary>
    /// <param name="title">Window title (e.g. "Overwrite media?").</param>
    /// <param name="headline">Short bold summary shown next to the severity glyph.</param>
    /// <param name="detail">The explanation and its implications — shown in the severity-graded pane.</param>
    /// <param name="headerDescription">The offending header's own description, or "Unidentified media".</param>
    /// <param name="severity">Glyph and colours: Info, Warning or Error.</param>
    /// <param name="proceedLabel">Label for the Proceed button (e.g. "Proceed &amp; overwrite" vs "Proceed").</param>
    /// <param name="allowRetry">Show the Retry ("Insert different media") button.</param>
    /// <param name="allowProceedAlways">Show the "Always proceed" button.</param>
    public MediaMismatchDialog(
        string title,
        string headline,
        string detail,
        string headerDescription,
        WarningLevel severity,
        string proceedLabel,
        bool allowRetry,
        bool allowProceedAlways)
        : this(BuildButtons(proceedLabel, allowRetry, allowProceedAlways),
            title, headline, detail, headerDescription, severity)
    {
    }

    private MediaMismatchDialog(
        (MediaPromptButton[] Buttons, MediaMismatchChoice[] Choices) layout,
        string title, string headline, string detail, string headerDescription, WarningLevel severity)
        : base(title, headline, message: null, detail: null, layout.Buttons,
            notice: detail, severity: severity, code: headerDescription)
    {
        m_choices = layout.Choices;
    }

    private static (MediaPromptButton[] Buttons, MediaMismatchChoice[] Choices) BuildButtons(
        string proceedLabel, bool allowRetry, bool allowProceedAlways)
    {
        var buttons = new List<MediaPromptButton>(4);
        var choices = new List<MediaMismatchChoice>(4);

        if (allowRetry)
        {
            buttons.Add(new MediaPromptButton("Insert different media"));
            choices.Add(MediaMismatchChoice.Retry);
        }
        buttons.Add(new MediaPromptButton(proceedLabel));
        choices.Add(MediaMismatchChoice.Proceed);
        if (allowProceedAlways)
        {
            buttons.Add(new MediaPromptButton("Always proceed"));
            choices.Add(MediaMismatchChoice.ProceedAlways);
        }
        buttons.Add(new MediaPromptButton("Abort", IsCancel: true, MinWidth: 90));
        choices.Add(MediaMismatchChoice.Abort);

        return ([.. buttons], [.. choices]);
    }
}
