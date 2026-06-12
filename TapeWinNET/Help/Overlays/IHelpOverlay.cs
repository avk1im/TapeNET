namespace TapeWinNET.Help.Overlays;

/// <summary>
/// Contract for help overlays (Reveal, Walkthrough) that adorn the host's
/// content area with highlights and handle their own input routing.
/// </summary>
internal interface IHelpOverlay
{
    /// <summary><c>true</c> while the overlay is active.</summary>
    bool IsActive { get; }

    /// <summary>Activates the overlay: adds the adorner and starts intercepting input.</summary>
    void Activate();

    /// <summary>Deactivates the overlay: removes the adorner and releases input.</summary>
    void Deactivate();

    /// <summary>Raised when the overlay deactivates (by any means: outside-click, Esc, or code).</summary>
    event EventHandler? Deactivated;
}
