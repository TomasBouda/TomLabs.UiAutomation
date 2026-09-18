using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace TomLabs.UiAutomation.Tests;

public partial class CounterViewModel : ObservableObject
{
    [ObservableProperty] private int _count;
    [ObservableProperty] private string _text = string.Empty;

    [RelayCommand] private void Increment() => Count++;
}

public class UiAutomationServerTests
{
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static (Window Window, CounterViewModel Vm, Button Button, TextBox Box) BuildWindow()
    {
        var vm = new CounterViewModel();
        var button = new Button { Name = "Bump", Content = "Bump", Command = vm.IncrementCommand, Width = 120, Height = 32 };
        var box = new TextBox { Name = "Input", Width = 200 };
        box.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(nameof(CounterViewModel.Text)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        var label = new TextBlock { Name = "Label" };
        label.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(CounterViewModel.Count)));
        var window = new Window
        {
            Title = "Counter",
            Width = 400,
            Height = 300,
            DataContext = vm,
            Content = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(20), Children = { button, box, label } },
        };
        window.Show();
        return (window, vm, button, box);
    }

    [AvaloniaFact]
    public async Task Status_screenshot_and_tree_describe_the_window()
    {
        var (window, _, _, _) = BuildWindow();
        var port = FreePort();
        using var server = new UiAutomationServer(UiAutomationHost.ForWindow(window), port, "counter", "1.2.3");
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        var status = JsonDocument.Parse(await http.GetStringAsync("")).RootElement;
        Assert.Equal("counter", status.GetProperty("name").GetString());
        Assert.Equal("1.2.3", status.GetProperty("version").GetString());
        Assert.Equal("Counter", status.GetProperty("windows")[0].GetProperty("title").GetString());

        var png = await http.GetByteArrayAsync("screenshot");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4));

        var tree = await http.GetStringAsync("tree");
        Assert.Contains("Button #Bump", tree);
        Assert.Contains("TextBox #Input", tree);

        var found = JsonDocument.Parse(await http.GetStringAsync("find?text=Bump")).RootElement;
        Assert.Equal("Button", found[0].GetProperty("type").GetString());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Click_type_get_set_and_invoke_reach_the_view_model()
    {
        var (window, vm, _, _) = BuildWindow();
        var port = FreePort();
        using var server = new UiAutomationServer(UiAutomationHost.ForWindow(window), port, "counter", "1.0.0") { SettleMilliseconds = 20 };
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        await http.GetStringAsync("click?name=Bump");
        Assert.Equal(1, vm.Count);

        await http.GetStringAsync("invoke?command=IncrementCommand");
        Assert.Equal(2, vm.Count);
        Assert.Equal("2", (await http.GetStringAsync("get?path=Count")).Trim());

        await http.GetStringAsync("click?name=Input");
        await http.GetStringAsync("type?text=hello");
        Assert.Equal("hello", vm.Text);

        await http.GetStringAsync("set?path=Text&value=world");
        Assert.Equal("world", vm.Text);

        var response = await http.GetAsync("click?text=Nothing here");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Custom_actions_and_theme()
    {
        var (window, vm, _, _) = BuildWindow();
        var port = FreePort();
        using var server = new UiAutomationServer(UiAutomationHost.ForWindow(window), port, "counter", "1.0.0") { SettleMilliseconds = 20 };
        server.Actions["reset"] = (_, arg) => { vm.Count = int.Parse(arg ?? "0"); return new { count = vm.Count }; };
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        var result = JsonDocument.Parse(await http.GetStringAsync("do/reset?arg=7")).RootElement;
        Assert.Equal(7, result.GetProperty("count").GetInt32());
        Assert.Equal(7, vm.Count);

        var theme = JsonDocument.Parse(await http.GetStringAsync("theme?variant=dark")).RootElement;
        Assert.Equal("Dark", theme.GetProperty("theme").GetString());
        window.Close();
    }
}
