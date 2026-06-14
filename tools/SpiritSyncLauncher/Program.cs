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

    var spotifyProxyFolder = TryPrepareSpotifyProxyAudio(bridgeRoot, bridgeBaseUrl);

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
        : StartRuntimePatcher(bridgeRoot, inGameUrl, bridgeBaseUrl, spotifyProxyFolder);

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

static Process? StartRuntimePatcher(
    string bridgeRoot,
    string inGameUrl,
    string bridgeBaseUrl,
    string? spotifyProxyFolder
)
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
        startInfo.ArgumentList.Add($"--bridge-url={bridgeBaseUrl}");
        if (!string.IsNullOrWhiteSpace(spotifyProxyFolder))
        {
            startInfo.ArgumentList.Add($"--spotify-proxy-folder={spotifyProxyFolder}");
        }

        var process = Process.Start(startInfo);
        if (process is not null)
        {
            Console.WriteLine($"Started Spirit Sync runtime patcher process {process.Id}.");
            if (!string.IsNullOrWhiteSpace(spotifyProxyFolder))
            {
                Console.WriteLine($"Spotify proxy custom audio folder: {spotifyProxyFolder}");
            }
        }

        return process;
    }
    catch (Exception error)
    {
        Console.WriteLine($"Runtime patcher could not start: {error.Message}");
        return null;
    }
}

static string? TryPrepareSpotifyProxyAudio(string bridgeRoot, string bridgeBaseUrl)
{
    try
    {
        var spotifyProxyFolder = EnsureSpotifyProxyAudio(bridgeRoot, bridgeBaseUrl);
        TryRetargetCustomMusicSaveToSpotifyProxy(bridgeRoot, spotifyProxyFolder);
        return spotifyProxyFolder;
    }
    catch (Exception error)
    {
        Console.WriteLine($"Spotify native custom audio proxy could not be prepared: {error.Message}");
        return null;
    }
}

static string EnsureSpotifyProxyAudio(string bridgeRoot, string bridgeBaseUrl)
{
    var labels = ReadSpotifyProxyLabels(bridgeBaseUrl);
    var proxyRoot = Path.Combine(bridgeRoot, "CustomAudio", "Spotify");
    ResetSpotifyProxyRoot(bridgeRoot, proxyRoot);

    var proxyFolder = Path.Combine(proxyRoot, SanitizeFileName(labels.Artist, "Spotify"));
    Directory.CreateDirectory(proxyFolder);

    var proxyAudioPath = Path.Combine(
        proxyFolder,
        $"{SanitizeFileName(labels.Title, "Spotify")}.wav"
    );
    if (File.Exists(proxyAudioPath) && new FileInfo(proxyAudioPath).Length > 1024 * 1024)
    {
        return proxyFolder;
    }

    const int sampleRate = 8000;
    const short channels = 1;
    const short bitsPerSample = 16;
    const int durationSeconds = 60 * 60;
    var bytesPerSample = bitsPerSample / 8;
    var blockAlign = (short)(channels * bytesPerSample);
    var byteRate = sampleRate * blockAlign;
    var dataBytes = sampleRate * durationSeconds * blockAlign;

    using var stream = File.Create(proxyAudioPath);
    using var writer = new BinaryWriter(stream, Encoding.ASCII);
    writer.Write(Encoding.ASCII.GetBytes("RIFF"));
    writer.Write(36 + dataBytes);
    writer.Write(Encoding.ASCII.GetBytes("WAVE"));
    writer.Write(Encoding.ASCII.GetBytes("fmt "));
    writer.Write(16);
    writer.Write((short)1);
    writer.Write(channels);
    writer.Write(sampleRate);
    writer.Write(byteRate);
    writer.Write(blockAlign);
    writer.Write(bitsPerSample);
    writer.Write(Encoding.ASCII.GetBytes("data"));
    writer.Write(dataBytes);

    var buffer = new byte[Math.Min(byteRate, dataBytes)];
    var remaining = dataBytes;
    while (remaining > 0)
    {
        var count = Math.Min(buffer.Length, remaining);
        writer.Write(buffer, 0, count);
        remaining -= count;
    }

    return proxyFolder;
}

static void ResetSpotifyProxyRoot(string bridgeRoot, string proxyRoot)
{
    if (!IsPathUnder(proxyRoot, bridgeRoot))
    {
        throw new InvalidOperationException($"Refusing to reset Spotify proxy folder outside bridge root: {proxyRoot}");
    }

    Directory.CreateDirectory(proxyRoot);

    foreach (var file in Directory.EnumerateFiles(proxyRoot, "*", SearchOption.AllDirectories))
    {
        File.SetAttributes(file, FileAttributes.Normal);
        File.Delete(file);
    }

    foreach (var directory in Directory
        .EnumerateDirectories(proxyRoot, "*", SearchOption.AllDirectories)
        .OrderByDescending(path => path.Length))
    {
        Directory.Delete(directory, recursive: false);
    }
}

static void TryRetargetCustomMusicSaveToSpotifyProxy(string bridgeRoot, string spotifyProxyFolder)
{
    var customMusicSavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpiritCity",
        "Saved",
        "SaveGames",
        "SCLS_CustomMusic.sav"
    );
    if (!File.Exists(customMusicSavePath))
    {
        Console.WriteLine(
            "Spotify native custom audio folder is ready. Import it once from Spirit City's Custom music tab to use the native bar."
        );
        return;
    }

    var buffer = File.ReadAllBytes(customMusicSavePath);
    var currentImportFolder = TryReadTaggedString(buffer, "ImportFolderAddress");
    if (string.IsNullOrWhiteSpace(currentImportFolder))
    {
        Console.WriteLine("Custom music save was found, but Spirit Sync could not read its import folder.");
        return;
    }

    var proxyRoot = Path.Combine(bridgeRoot, "CustomAudio", "Spotify");
    if (!ShouldSeedSpotifyProxy(currentImportFolder, proxyRoot))
    {
        Console.WriteLine(
            "Spotify native custom audio folder is ready. Spirit City's Custom tab is currently pointed at your own folder, so Spirit Sync left it alone. Import the Spotify folder once to use the native bar."
        );
        return;
    }

    var gamePath = ToSpiritCitySavePath(spotifyProxyFolder);
    if (PathsEqual(currentImportFolder, gamePath))
    {
        return;
    }

    var updated = TryReplaceTaggedString(buffer, "ImportFolderAddress", gamePath);
    if (updated is null)
    {
        Console.WriteLine("Custom music save was found, but Spirit Sync could not update its import folder.");
        return;
    }

    var backupPath = customMusicSavePath + ".spirit-sync.bak";
    if (!File.Exists(backupPath))
    {
        File.Copy(customMusicSavePath, backupPath);
    }

    File.WriteAllBytes(customMusicSavePath, updated);
    Console.WriteLine("Updated Spirit City's Custom music import folder for the Spotify native proxy.");
}

static string? TryReadTaggedString(byte[] buffer, string propertyName)
{
    var location = TryFindTaggedString(buffer, propertyName);
    return location is null
        ? null
        : Encoding.UTF8.GetString(buffer, location.ValueOffset, location.ValueLength)
            .TrimEnd('\0')
            .Trim();
}

static byte[]? TryReplaceTaggedString(byte[] buffer, string propertyName, string value)
{
    var location = TryFindTaggedString(buffer, propertyName);
    if (location is null)
    {
        return null;
    }

    var newValue = Encoding.UTF8.GetBytes(value.TrimEnd('/', '\\') + "/\0");
    var newPropertySize = BitConverter.GetBytes(newValue.Length + 4);
    var newValueLength = BitConverter.GetBytes(newValue.Length);
    var oldEnd = location.ValueOffset + location.ValueLength;

    using var stream = new MemoryStream(buffer.Length - location.ValueLength + newValue.Length);
    stream.Write(buffer, 0, location.PropertySizeOffset);
    stream.Write(newPropertySize, 0, newPropertySize.Length);
    stream.Write(
        buffer,
        location.PropertySizeOffset + sizeof(int),
        location.ValueLengthOffset - (location.PropertySizeOffset + sizeof(int))
    );
    stream.Write(newValueLength, 0, newValueLength.Length);
    stream.Write(newValue, 0, newValue.Length);
    stream.Write(buffer, oldEnd, buffer.Length - oldEnd);
    return stream.ToArray();
}

static TaggedStringLocation? TryFindTaggedString(byte[] buffer, string propertyName)
{
    var nameBytes = Encoding.UTF8.GetBytes($"{propertyName}\0");
    var nameIndex = buffer.AsSpan().IndexOf(nameBytes);
    if (nameIndex < 0)
    {
        return null;
    }

    var typeLengthOffset = nameIndex + nameBytes.Length;
    if (typeLengthOffset + sizeof(int) > buffer.Length)
    {
        return null;
    }

    var typeLength = BitConverter.ToInt32(buffer, typeLengthOffset);
    if (typeLength <= 0 || typeLength > 128)
    {
        return null;
    }

    var typeOffset = typeLengthOffset + sizeof(int);
    var propertySizeOffset = typeOffset + typeLength + sizeof(int);
    var valueLengthOffset = typeOffset + typeLength + 9;
    if (valueLengthOffset + sizeof(int) > buffer.Length)
    {
        return null;
    }

    var typeName = Encoding.UTF8.GetString(buffer, typeOffset, typeLength).TrimEnd('\0');
    if (!typeName.Equals("StrProperty", StringComparison.Ordinal))
    {
        return null;
    }

    var valueLength = BitConverter.ToInt32(buffer, valueLengthOffset);
    if (valueLength <= 0 || valueLength > 4096)
    {
        return null;
    }

    var valueOffset = valueLengthOffset + sizeof(int);
    if (valueOffset + valueLength > buffer.Length)
    {
        return null;
    }

    return new TaggedStringLocation(
        propertySizeOffset,
        valueLengthOffset,
        valueOffset,
        valueLength
    );
}

static string ToSpiritCitySavePath(string path)
{
    return Path.GetFullPath(path).Replace(Path.DirectorySeparatorChar, '/');
}

static bool ShouldSeedSpotifyProxy(string currentImportFolder, string proxyRoot)
{
    // Already pointing inside the Spirit Sync proxy area: retarget to the newest proxy folder.
    if (IsPathUnder(currentImportFolder, proxyRoot))
    {
        return true;
    }

    // A Spirit Sync-owned path (an old proxy or probe folder) is ours to replace. Matches both
    // "SpiritSync" and "Spirit Sync" by ignoring spaces.
    if (currentImportFolder.Replace(" ", "").Contains("SpiritSync", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    // A recorded folder that no longer exists would only yield an empty/broken custom playlist,
    // so it is safe to repoint at the working Spotify proxy. The previous value is preserved in
    // the .spirit-sync.bak backup written before any change.
    if (!DirectoryExistsSafe(currentImportFolder))
    {
        return true;
    }

    // A real, existing user folder: never clobber it.
    return false;
}

static bool DirectoryExistsSafe(string path)
{
    var normalized = NormalizePath(path);
    return normalized is not null && Directory.Exists(normalized);
}

static bool IsPathUnder(string path, string parent)
{
    var normalizedPath = NormalizePath(path);
    var normalizedParent = NormalizePath(parent);
    if (normalizedPath is null || normalizedParent is null)
    {
        return false;
    }

    return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
        || normalizedPath.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        );
}

static bool PathsEqual(string left, string right)
{
    var normalizedLeft = NormalizePath(left);
    var normalizedRight = NormalizePath(right);
    return normalizedLeft is not null
        && normalizedRight is not null
        && normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
}

static string? NormalizePath(string path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return null;
    }

    try
    {
        return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
    catch
    {
        return null;
    }
}

static SpotifyProxyLabels ReadSpotifyProxyLabels(string bridgeBaseUrl)
{
    try
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
        var status = http.GetFromJsonAsync<NowPlayingStatus>(
                $"{bridgeBaseUrl}/api/now-playing"
            )
            .GetAwaiter()
            .GetResult();
        var playback = status?.Playback;
        if (
            status?.Connected == true
            && playback is not null
            && !string.IsNullOrWhiteSpace(playback.Title)
            && !playback.Title.Equals("Nothing playing", StringComparison.OrdinalIgnoreCase)
        )
        {
            return new SpotifyProxyLabels(
                playback.Title.Trim(),
                string.IsNullOrWhiteSpace(playback.Artist) ? "Spotify" : playback.Artist.Trim()
            );
        }
    }
    catch
    {
        // Keep game launch fail-safe; the proxy can use generic labels until Spotify is active.
    }

    return new SpotifyProxyLabels("Spotify", "Spirit Sync");
}

static string SanitizeFileName(string value, string fallback)
{
    var invalid = Path.GetInvalidFileNameChars().ToHashSet();
    var builder = new StringBuilder(value.Length);
    foreach (var character in value.Trim())
    {
        builder.Append(invalid.Contains(character) ? ' ' : character);
    }

    var sanitized = string.Join(" ", builder.ToString().Split(
        ' ',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    ));
    if (string.IsNullOrWhiteSpace(sanitized))
    {
        sanitized = fallback;
    }

    return sanitized.Length > 80 ? sanitized[..80].TrimEnd() : sanitized;
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
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("playback")] NowPlayingPlayback? Playback
);

sealed record NowPlayingPlayback(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist
);

sealed record SpotifyProxyLabels(string Title, string Artist);

sealed record TaggedStringLocation(
    int PropertySizeOffset,
    int ValueLengthOffset,
    int ValueOffset,
    int ValueLength
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
