using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace TomLabs.UiAutomation;

/// <summary>
/// What the server needs from the application: the open windows, which one is the main window and how to quit.
/// <see cref="FromLifetime"/> covers the usual desktop app; tests and custom hosts supply their own delegates.
/// </summary>
public sealed record UiAutomationHost(Func<IReadOnlyList<Window>> Windows, Func<Window?> MainWindow, Action Shutdown)
{
    public static UiAutomationHost FromLifetime(IClassicDesktopStyleApplicationLifetime lifetime) =>
        new(() => lifetime.Windows, () => lifetime.MainWindow, () => lifetime.Shutdown());

    /// <summary>A single window, for tests: the window is the main one and "quit" closes it.</summary>
    public static UiAutomationHost ForWindow(Window window) =>
        new(() => new[] { window }, () => window, window.Close);
}
