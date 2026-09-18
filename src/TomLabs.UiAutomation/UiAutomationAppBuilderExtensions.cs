using Avalonia;
using Avalonia.Headless;

namespace TomLabs.UiAutomation;

public static class UiAutomationAppBuilderExtensions
{
    /// <summary>The command-line switch that runs the app on the headless platform.</summary>
    public const string HeadlessSwitch = "--headless";

    public static bool IsHeadlessRequested(string[] args) =>
        Array.Exists(args, a => a.Equals(HeadlessSwitch, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>UsePlatformDetect()</c> for a normal start; with <c>--headless</c> on the command line the headless platform
    /// with the Skia renderer instead, so the app runs without any window yet renders pixel-identically to the real
    /// one. Pair it with <see cref="UiAutomationServer.StartIfEnabled(Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime, string, string, Action{string}?)"/>.
    /// </summary>
    public static AppBuilder UsePlatformDetectOrHeadless(this AppBuilder builder, string[] args) =>
        IsHeadlessRequested(args) ? builder.UseHeadlessWithSkia() : builder.UsePlatformDetect();

    /// <summary>The headless platform rendering through Skia (screenshots work, nothing is shown).</summary>
    public static AppBuilder UseHeadlessWithSkia(this AppBuilder builder) =>
        builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
