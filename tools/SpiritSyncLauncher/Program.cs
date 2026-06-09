using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

const int DefaultBridgePort = 8012;

var options = LauncherOptions.Parse(args);
var gameRoot = Path.GetFullPath(options.GameRoot ?? AppContext.BaseDirectory);
var bridgeRoot = Path.GetFullPath(
    options.BridgeRoot ?? Path.Combine(gameRoot, "SpiritSync")
);
var envPath = Path.Combine(gameRoot, "spirit-sync.env");
var bridgePort = ReadPort(envPath);
var bridgeBaseUrl = $"http://127.0.0.1:{bridgePort}";
var inGameUrl = ReadInGameUrl(envPath, bridgeBaseUrl);

Console.Title = "Spirit Sync";
Console.WriteLine("Spirit Sync launcher starting...");
Console.WriteLine($"Game root: {gameRoot}");
Console.WriteLine($"Bridge root: {bridgeRoot}");

using var cancellation = new CancellationTokenSource();
using var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(3),
};

try
{
    var bridgeProcess = await EnsureBridgeAsync(
        http,
        bridgeRoot,
        gameRoot,
        envPath,
        bridgeBaseUrl,
        cancellation.Token
    );

    await WaitForBridgeAsync(http, bridgeBaseUrl, inGameUrl, cancellation.Token);
    await EnsureSpotifyLoginAsync(http, bridgeBaseUrl, options.SkipAuthWait, cancellation.Token);

    Task? cefWatcher = null;
    if (!options.NoCefWatch)
    {
        cefWatcher = WatchCefTargetsAsync(
            http,
            bridgeBaseUrl,
            inGameUrl,
            options.CefDebugPort,
            cancellation.Token
        );
    }

    using var gameProcess = StartGame(gameRoot, options.GameArgs, options.CefDebugPort);
    Console.WriteLine($"Started Spirit City process {gameProcess.Id}.");

    var runtimePatcher = options.NoRuntimePatch
        ? null
        : StartRuntimePatcher(bridgeRoot, inGameUrl);

    await gameProcess.WaitForExitAsync();
    cancellation.Cancel();

    if (cefWatcher is not null)
    {
        await cefWatcher.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
    }

    if (runtimePatcher is { HasExited: false })
    {
        runtimePatcher.Kill(entireProcessTree: true);
    }

    if (bridgeProcess is { HasExited: false })
    {
        bridgeProcess.Kill(entireProcessTree: true);
    }

    return gameProcess.ExitCode;
}
catch (Exception error)
{
    Console.Error.WriteLine("Error Loading Spirit Sync, loading without it.");
    Console.Error.WriteLine(error.Message);

    using var gameProcess = StartGame(gameRoot, options.GameArgs, options.CefDebugPort);
    await gameProcess.WaitForExitAsync();
    return gameProcess.ExitCode;
}

static async Task<Process?> EnsureBridgeAsync(
    HttpClient http,
    string bridgeRoot,
    string gameRoot,
    string envPath,
    string bridgeBaseUrl,
    CancellationToken cancellationToken
)
{
    if (await IsBridgeRunningAsync(http, bridgeBaseUrl, cancellationToken))
    {
        Console.WriteLine("Spotify bridge is already running.");
        return null;
    }

    var serverPath = Path.Combine(bridgeRoot, "dist", "server.js");
    if (!File.Exists(serverPath))
    {
        throw new FileNotFoundException(
            "The bridge was not installed correctly. dist\\server.js is missing.",
            serverPath
        );
    }

    var nodePath = FindNode();
    if (nodePath is null)
    {
        throw new InvalidOperationException(
            "Node.js was not found on PATH. Install Node.js 20 or newer, or place node.exe in the SpiritSync folder."
        );
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = nodePath,
        Arguments = Quote(serverPath),
        WorkingDirectory = bridgeRoot,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.Environment["SPIRIT_CITY_INSTALL_DIR"] = gameRoot;
    startInfo.Environment["SPIRIT_SYNC_ENV_PATH"] = envPath;
    startInfo.Environment["TOKEN_STORE_PATH"] = Path.Combine(bridgeRoot, ".spotify-tokens.json");

    var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start the Spotify bridge.");

    Console.WriteLine($"Started Spotify bridge process {process.Id}.");
    return process;
}

static async Task WaitForBridgeAsync(
    HttpClient http,
    string bridgeBaseUrl,
    string inGameUrl,
    CancellationToken cancellationToken
)
{
    Console.WriteLine("Waiting for Spotify bridge...");

    for (var attempt = 0; attempt < 60; attempt += 1)
    {
        if (await IsBridgeRunningAsync(http, bridgeBaseUrl, cancellationToken))
        {
            Console.WriteLine($"Bridge URL: {inGameUrl}");
            return;
        }

        await Task.Delay(500, cancellationToken);
    }

    throw new TimeoutException("Spotify bridge did not become ready.");
}

static async Task<bool> IsBridgeRunningAsync(
    HttpClient http,
    string bridgeBaseUrl,
    CancellationToken cancellationToken
)
{
    try
    {
        using var response = await http.GetAsync($"{bridgeBaseUrl}/api/config", cancellationToken);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

static async Task EnsureSpotifyLoginAsync(
    HttpClient http,
    string bridgeBaseUrl,
    bool skipAuthWait,
    CancellationToken cancellationToken
)
{
    var config = await http.GetFromJsonAsync<BridgeConfig>(
        $"{bridgeBaseUrl}/api/config",
        cancellationToken
    );

    if (config?.HasClientId != true)
    {
        Console.WriteLine("Spotify ClientID is missing. Edit spirit-sync.env before logging in.");
        OpenBrowser($"{bridgeBaseUrl}");
        return;
    }

    if (await IsSpotifyConnectedAsync(http, bridgeBaseUrl, cancellationToken))
    {
        Console.WriteLine("Spotify account is connected.");
        return;
    }

    Console.WriteLine("Hello! This seems to be your first time launching Spirit City with Spirit Sync!");
    Console.WriteLine("Opening Spotify login...");
    Console.WriteLine($"Please open this link in your browser: {bridgeBaseUrl}/login");
    OpenBrowser($"{bridgeBaseUrl}/login");

    if (skipAuthWait)
    {
        return;
    }

    Console.WriteLine("Waiting for approval...");
    for (var attempt = 0; attempt < 600; attempt += 1)
    {
        if (await IsSpotifyConnectedAsync(http, bridgeBaseUrl, cancellationToken))
        {
            Console.WriteLine("Spotify account connected. Launching Spirit City...");
            return;
        }

        await Task.Delay(1000, cancellationToken);
    }

    Console.WriteLine("Spotify login was not completed yet. Launching Spirit City anyway.");
}

static async Task<bool> IsSpotifyConnectedAsync(
    HttpClient http,
    string bridgeBaseUrl,
    CancellationToken cancellationToken
)
{
    try
    {
        var status = await http.GetFromJsonAsync<NowPlayingStatus>(
            $"{bridgeBaseUrl}/api/now-playing",
            cancellationToken
        );
        return status?.Connected == true;
    }
    catch
    {
        return false;
    }
}

static Process StartGame(string gameRoot, IReadOnlyList<string> gameArgs, int cefDebugPort)
{
    var backupLauncher = Path.Combine(gameRoot, "SpiritCityBackup.exe");
    var shippingExe = Path.Combine(
        gameRoot,
        "SpiritCity",
        "Binaries",
        "Win64",
        "SpiritCity-Win64-Shipping.exe"
    );

    var exePath = File.Exists(shippingExe) ? shippingExe : backupLauncher;
    if (!File.Exists(exePath))
    {
        throw new FileNotFoundException("Could not find the Spirit City executable.", exePath);
    }

    var args = new List<string>();
    if (Path.GetFileName(exePath).Equals("SpiritCity-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase))
    {
        args.Add("SpiritCity");
    }

    args.AddRange(gameArgs);
    if (!args.Any(arg => arg.StartsWith("cefdebug=", StringComparison.OrdinalIgnoreCase)))
    {
        args.Add($"cefdebug={cefDebugPort}");
    }

    return Process.Start(
        new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = string.Join(" ", args.Select(Quote)),
            WorkingDirectory = gameRoot,
            UseShellExecute = false,
        }
    ) ?? throw new InvalidOperationException("Could not start Spirit City.");
}

static Process? StartRuntimePatcher(string bridgeRoot, string inGameUrl)
{
    var patcherPath = new[]
    {
        Path.Combine(bridgeRoot, "SpiritCityRuntimePatch.exe"),
        Path.Combine(AppContext.BaseDirectory, "SpiritCityRuntimePatch.exe"),
    }.FirstOrDefault(File.Exists);

    if (patcherPath is null)
    {
        Console.WriteLine("Runtime patcher was not installed; Spirit Sync will still redirect external browser pages.");
        return null;
    }

    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = patcherPath,
            WorkingDirectory = bridgeRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("watch");
        startInfo.ArgumentList.Add("--duration-sec=43200");
        startInfo.ArgumentList.Add("--interval-ms=1500");
        startInfo.ArgumentList.Add("--quiet");
        startInfo.ArgumentList.Add($"--url={inGameUrl}");

        var process = Process.Start(startInfo);
        if (process is not null)
        {
            Console.WriteLine($"Started Spirit Sync runtime patcher process {process.Id}.");
        }

        return process;
    }
    catch (Exception error)
    {
        Console.WriteLine($"Runtime patcher could not start: {error.Message}");
        return null;
    }
}

static async Task WatchCefTargetsAsync(
    HttpClient http,
    string bridgeBaseUrl,
    string inGameUrl,
    int cefDebugPort,
    CancellationToken cancellationToken
)
{
    var debugBaseUrl = $"http://127.0.0.1:{cefDebugPort}";
    var redirectedTargets = new Dictionary<string, DateTimeOffset>();

    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var targets = await http.GetFromJsonAsync<List<CefTarget>>(
                $"{debugBaseUrl}/json",
                cancellationToken
            ) ?? [];

            foreach (var target in targets.Where(ShouldRedirectTarget))
            {
                if (
                    redirectedTargets.TryGetValue(target.Id, out var redirectedAt)
                    && DateTimeOffset.UtcNow - redirectedAt < TimeSpan.FromSeconds(30)
                )
                {
                    continue;
                }

                await NavigateTargetAsync(
                    target,
                    WithLauncherCacheBust(inGameUrl),
                    cancellationToken
                );
                redirectedTargets[target.Id] = DateTimeOffset.UtcNow;
                Console.WriteLine("Redirected external music browser to Spirit Sync.");
            }
        }
        catch
        {
            // CEF is not ready until the in-game browser has been opened.
        }

        await Task.Delay(1500, cancellationToken);
    }
}

static string WithLauncherCacheBust(string url)
{
    var separator = url.Contains('?') ? '&' : '?';
    return $"{url}{separator}launcher={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
}

static bool ShouldRedirectTarget(CefTarget target)
{
    if (!string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri))
    {
        return false;
    }

    if (uri.Host is "127.0.0.1" or "localhost")
    {
        return false;
    }

    return uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
}

static async Task NavigateTargetAsync(
    CefTarget target,
    string url,
    CancellationToken cancellationToken
)
{
    using var socket = new ClientWebSocket();
    await socket.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), cancellationToken);
    await SendCdpMessageAsync(
        socket,
        new
        {
            id = 1,
            method = "Page.navigate",
            @params = new
            {
                url,
            },
        },
        cancellationToken
    );
}

static async Task SendCdpMessageAsync(
    ClientWebSocket socket,
    object message,
    CancellationToken cancellationToken
)
{
    var json = JsonSerializer.Serialize(message);
    var bytes = Encoding.UTF8.GetBytes(json);
    await socket.SendAsync(
        bytes,
        WebSocketMessageType.Text,
        true,
        cancellationToken
    );
}

static int ReadPort(string envPath)
{
    return int.TryParse(ReadEnvValue(envPath, "PORT"), out var port)
        ? port
        : DefaultBridgePort;
}

static string ReadInGameUrl(string envPath, string bridgeBaseUrl)
{
    var value =
        ReadEnvValue(envPath, "SPIRIT_SYNC_EXTERNAL_URL")
        ?? ReadEnvValue(envPath, "SpiritSyncExternalUrl")
        ?? ReadEnvValue(envPath, "SPIRIT_SYNC_INGAME_URL")
        ?? ReadEnvValue(envPath, "SpiritSyncInGameUrl");

    if (string.IsNullOrWhiteSpace(value))
    {
        return $"{bridgeBaseUrl}/spirit-sync";
    }

    if (value.StartsWith("/", StringComparison.Ordinal))
    {
        return $"{bridgeBaseUrl}{value}";
    }

    return Uri.TryCreate(value, UriKind.Absolute, out var uri)
        ? uri.ToString()
        : $"{bridgeBaseUrl}/spirit-sync";
}

static string? ReadEnvValue(string envPath, string name)
{
    if (!File.Exists(envPath))
    {
        return null;
    }

    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('#') || !trimmed.Contains('='))
        {
            continue;
        }

        var parts = trimmed.Split('=', 2);
        if (parts[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return Unquote(parts[1].Trim());
        }
    }

    return null;
}

static string? FindNode()
{
    var localNode = Path.Combine(AppContext.BaseDirectory, "SpiritSync", "node.exe");
    if (File.Exists(localNode))
    {
        return localNode;
    }

    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
    foreach (var directory in path.Split(Path.PathSeparator))
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            continue;
        }

        var candidate = Path.Combine(directory.Trim(), "node.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}

static void OpenBrowser(string url)
{
    Process.Start(
        new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        }
    );
}

static string Quote(string value)
{
    if (string.IsNullOrEmpty(value))
    {
        return "\"\"";
    }

    return value.Contains(' ') || value.Contains('"')
        ? $"\"{value.Replace("\"", "\\\"")}\""
        : value;
}

static string Unquote(string value)
{
    return value.Trim().Trim('"');
}

sealed record BridgeConfig(
    [property: JsonPropertyName("hasClientId")] bool HasClientId
);

sealed record NowPlayingStatus(
    [property: JsonPropertyName("connected")] bool Connected
);

sealed record CefTarget(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("webSocketDebuggerUrl")] string WebSocketDebuggerUrl
);

sealed record LauncherOptions(
    string? GameRoot,
    string? BridgeRoot,
    int CefDebugPort,
    bool NoCefWatch,
    bool NoRuntimePatch,
    bool SkipAuthWait,
    IReadOnlyList<string> GameArgs
)
{
    public static LauncherOptions Parse(string[] args)
    {
        string? gameRoot = null;
        string? bridgeRoot = null;
        var cefDebugPort = 9222;
        var noCefWatch = false;
        var noRuntimePatch = false;
        var skipAuthWait = false;
        var gameArgs = new List<string>();

        foreach (var arg in args)
        {
            if (arg.StartsWith("--spirit-sync-game-root=", StringComparison.OrdinalIgnoreCase))
            {
                gameRoot = arg["--spirit-sync-game-root=".Length..];
            }
            else if (arg.StartsWith("--spirit-sync-bridge-root=", StringComparison.OrdinalIgnoreCase))
            {
                bridgeRoot = arg["--spirit-sync-bridge-root=".Length..];
            }
            else if (
                arg.StartsWith("--spirit-sync-cef-port=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg["--spirit-sync-cef-port=".Length..], out var parsedPort)
            )
            {
                cefDebugPort = parsedPort;
            }
            else if (arg.Equals("--spirit-sync-no-cef-watch", StringComparison.OrdinalIgnoreCase))
            {
                noCefWatch = true;
            }
            else if (arg.Equals("--spirit-sync-no-runtime-patch", StringComparison.OrdinalIgnoreCase))
            {
                noRuntimePatch = true;
            }
            else if (arg.Equals("--spirit-sync-skip-auth-wait", StringComparison.OrdinalIgnoreCase))
            {
                skipAuthWait = true;
            }
            else
            {
                gameArgs.Add(arg);
            }
        }

        return new LauncherOptions(
            gameRoot,
            bridgeRoot,
            cefDebugPort,
            noCefWatch,
            noRuntimePatch,
            skipAuthWait,
            gameArgs
        );
    }
}
