using System.Windows;

namespace TapeWinNET.Help.Overlays;

/// <summary>
/// Carries the element and its control-name when a Reveal target is clicked.
/// </summary>
public sealed record RevealTarget(FrameworkElement Element, string ControlName);
