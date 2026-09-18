using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace TomLabs.UiAutomation.Tool;

/// <summary>
/// `tomlabs-ui` drives an app that embeds TomLabs.UiAutomation: builds it, starts it headless (or visible), waits
/// for the automation channel and forwards endpoint calls. Run it from the repository root of the app.
/// </summary>
internal static class Program
{
    private const string Usage = """
        tomlabs-ui - drive an Avalonia app through its TomLabs.UiAutomation channel (no desktop input involved)

          tomlabs-ui start   [--project <csproj>] [--visible] [--data-dir <dir>] [--fixture <dir>] [--no-build]
                             [--configuration Release] [--env NAME=VALUE]... [--port 47831] [--timeout 40]
          tomlabs-ui stop    [--port 47831]
          tomlabs-ui status  [--port 47831]
          tomlabs-ui call    <endpoint?query> [--window top] [--port 47831]
          tomlabs-ui shot    <file.png> [--window top] [--scale 1] [--port 47831]
          tomlabs-ui tree    [<query>] [--window top] [--port 47831]

        start builds the WinExe project found under the current folder (Release, into ./.ui-build), starts it with
        --headless (or a real window with --visible), <APP>_AUTOMATION=<port> and <APP>_DATA_DIR pointing at a fresh
        copy of --fixture (default tests/ui-fixture, or an empty folder), then waits until the channel answers.
        Endpoints: /screenshot /tree /find /click /key /type /get /set /invoke /do/<action> /theme /resize /wait /quit.
        """;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var (command, positional, options) = Parse(args);
        var port = options.TryGetValue("port", out var p) ? int.Parse(p) : 47831;
        var timeout = TimeSpan.FromSeconds(options.TryGetValue("timeout", out var t) ? int.Parse(t) : 40);
        var channel = new Channel(port, timeout);

        switch (command)
        {
            case "start":
                return await StartAsync(channel, options);
            case "stop":
                if (!await channel.IsUpAsync()) { Console.WriteLine($"Nothing answers on {channel.BaseUrl}."); return 0; }
                await channel.StopAsync();
                Console.WriteLine("Stopped.");
                return 0;
            case "status":
                if (!await channel.IsUpAsync()) { Console.WriteLine($"Nothing answers on {channel.BaseUrl}. Start with: tomlabs-ui start"); return 1; }
                Console.WriteLine(await channel.GetStringAsync(string.Empty));
                return 0;
            case "call":
                if (positional.Count == 0) throw new ArgumentException("Usage: tomlabs-ui call '<endpoint>?<query>'");
                Console.WriteLine(await channel.GetStringAsync(WithWindow(positional[0], options)));
                return 0;
            case "shot":
            {
                if (positional.Count == 0) throw new ArgumentException("Usage: tomlabs-ui shot <file.png> [--window top]");
                var file = Path.GetFullPath(positional[0]);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var query = options.TryGetValue("scale", out var scale) ? $"screenshot?scale={scale}" : "screenshot";
                await File.WriteAllBytesAsync(file, await channel.GetBytesAsync(WithWindow(query, options)));
                Console.WriteLine(file);
                return 0;
            }
            case "tree":
                Console.WriteLine(await channel.GetStringAsync(WithWindow(positional.Count > 0 ? "tree?" + positional[0] : "tree", options)));
                return 0;
            default:
                Console.Error.WriteLine($"Unknown command '{command}'.");
                Console.Error.WriteLine(Usage);
                return 1;
        }
    }

    private static async Task<int> StartAsync(Channel channel, Dictionary<string, string> options)
    {
        var repoRoot = Directory.GetCurrentDirectory();
        var project = options.TryGetValue("project", out var explicitProject) ? Path.GetFullPath(explicitProject) : FindWinExeProject(repoRoot);
        var appName = ReadAssemblyName(project);
        var buildDir = Path.Combine(repoRoot, ".ui-build");
        var visible = options.ContainsKey("visible");

        if (await channel.IsUpAsync())
        {
            Console.WriteLine($"An instance already answers on {channel.BaseUrl} - stopping it first.");
            await channel.StopAsync();
        }

        if (!options.ContainsKey("no-build"))
        {
            var configuration = options.TryGetValue("configuration", out var c) ? c : "Release";
            Console.WriteLine($"Building {Path.GetRelativePath(repoRoot, project)} ({configuration}) into {buildDir} ...");
            var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{project}\" -c {configuration} -o \"{buildDir}\" -v q --nologo") { UseShellExecute = false })!;
            await build.WaitForExitAsync();
            if (build.ExitCode != 0)
                throw new InvalidOperationException("Build failed.");
        }

        var exe = Path.Combine(buildDir, OperatingSystem.IsWindows() ? appName + ".exe" : appName);
        if (!File.Exists(exe))
            throw new FileNotFoundException($"Executable not found: {exe} (run without --no-build).");

        string dataDir;
        if (options.TryGetValue("data-dir", out var explicitData))
        {
            dataDir = Path.GetFullPath(explicitData);
        }
        else
        {
            // A fresh copy of the fixture per run: the app writes to its data folder, the fixture stays clean.
            dataDir = Path.Combine(Path.GetTempPath(), $"{appName}-ui-{channel.Port}");
            if (Directory.Exists(dataDir))
                Directory.Delete(dataDir, recursive: true);
            Directory.CreateDirectory(dataDir);
            var fixture = options.TryGetValue("fixture", out var f) ? Path.GetFullPath(f) : Path.Combine(repoRoot, "tests", "ui-fixture");
            if (Directory.Exists(fixture))
                CopyDirectory(fixture, dataDir);
            else
                Console.WriteLine($"No fixture at {fixture}; starting on an empty data folder.");
        }

        // Environment is inherited by the child; the app reads <APP>_AUTOMATION and its data-directory variable.
        var prefix = appName.ToUpperInvariant();
        Environment.SetEnvironmentVariable($"{prefix}_AUTOMATION", channel.Port.ToString());
        Environment.SetEnvironmentVariable($"{prefix}_DATA_DIR", dataDir);
        foreach (var (key, value) in options.Where(o => o.Key.StartsWith("env:", StringComparison.Ordinal)))
            Environment.SetEnvironmentVariable(key[4..], value);

        // UseShellExecute = true starts the app detached without inheriting this process's stdio handles; a shell
        // waiting for those pipes (Claude Code, CI) would otherwise hang until the app exits.
        var process = Process.Start(new ProcessStartInfo(exe, visible ? string.Empty : "--headless")
        {
            UseShellExecute = true,
            WorkingDirectory = buildDir,
        }) ?? throw new InvalidOperationException("The application did not start.");

        var deadline = DateTime.UtcNow + channel.Timeout;
        while (!await channel.IsUpAsync())
        {
            if (process.HasExited)
                throw new InvalidOperationException($"The application exited with code {process.ExitCode}. See its log under {dataDir}.");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The automation channel did not answer within {channel.Timeout.TotalSeconds:0} s.");
            await Task.Delay(250);
        }
        // First layout and fade-in animations finish within a moment; wait so the first screenshot is settled.
        await channel.GetStringAsync("wait?ms=600");

        Console.WriteLine($"{appName} is running (pid {process.Id}, {(visible ? "visible window" : "headless")}).");
        Console.WriteLine($"  channel : {channel.BaseUrl}");
        Console.WriteLine($"  data    : {dataDir}");
        Console.WriteLine(await channel.GetStringAsync(string.Empty));
        return 0;
    }

    // ----- helpers -----

    private static (string Command, List<string> Positional, Dictionary<string, string> Options) Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(args[i]);
                continue;
            }
            var key = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                && key is not ("visible" or "no-build");
            var value = hasValue ? args[++i] : "true";
            if (key == "env")
            {
                var eq = value.IndexOf('=');
                if (eq <= 0) throw new ArgumentException("--env expects NAME=VALUE");
                options["env:" + value[..eq]] = value[(eq + 1)..];
            }
            else
            {
                options[key] = value;
            }
        }
        return (args[0].ToLowerInvariant(), positional, options);
    }

    private static string WithWindow(string endpoint, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("window", out var window))
            return endpoint;
        return endpoint + (endpoint.Contains('?') ? '&' : '?') + "window=" + Uri.EscapeDataString(window);
    }

    private static readonly string[] SkippedFolders = { "bin", "obj", "node_modules", ".git", ".ui-build" };

    /// <summary>The single WinExe project under the folder; several or none need --project.</summary>
    private static string FindWinExeProject(string root)
    {
        var candidates = EnumerateProjects(root, 0)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"<OutputType>\s*WinExe\s*</OutputType>", RegexOptions.IgnoreCase))
            .ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new FileNotFoundException("No WinExe project found under the current folder; pass --project <csproj>."),
            _ => throw new InvalidOperationException("Several WinExe projects found; pass --project <csproj>:\n  " + string.Join("\n  ", candidates)),
        };
    }

    private static IEnumerable<string> EnumerateProjects(string folder, int depth)
    {
        if (depth > 4)
            yield break;
        foreach (var file in Directory.EnumerateFiles(folder, "*.csproj"))
            yield return file;
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.') || SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            foreach (var file in EnumerateProjects(sub, depth + 1))
                yield return file;
        }
    }

    private static string ReadAssemblyName(string project)
    {
        var match = Regex.Match(File.ReadAllText(project), @"<AssemblyName>\s*([^<]+?)\s*</AssemblyName>", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(project);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private sealed class Channel(int port, TimeSpan timeout)
    {
        private readonly HttpClient _http = new() { Timeout = timeout };

        public int Port { get; } = port;
        public TimeSpan Timeout { get; } = timeout;
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        public async Task<bool> IsUpAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var response = await _http.GetAsync($"{BaseUrl}/", cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<string> GetStringAsync(string endpoint)
        {
            using var response = await _http.GetAsync($"{BaseUrl}/{endpoint.TrimStart('/')}");
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{(int)response.StatusCode} {body}");
            return body.TrimEnd();
        }

        public async Task<byte[]> GetBytesAsync(string endpoint)
        {
            using var response = await _http.GetAsync($"{BaseUrl}/{endpoint.TrimStart('/')}");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task StopAsync()
        {
            try { await GetStringAsync("quit"); } catch (Exception) { /* the app may close the connection first */ }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (await IsUpAsync() && DateTime.UtcNow < deadline)
                await Task.Delay(200);
        }
    }
}
