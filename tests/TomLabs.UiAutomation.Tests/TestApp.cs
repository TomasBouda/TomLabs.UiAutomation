using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using TomLabs.UiAutomation.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace TomLabs.UiAutomation.Tests;

public class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadlessWithSkia();
}
