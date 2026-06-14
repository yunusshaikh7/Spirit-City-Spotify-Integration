using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;

var mode = args.FirstOrDefault() ?? "scan";
var processName = GetArg(args, "--process") ?? "SpiritCity-Win64-Shipping";
var quiet = args.Any(arg => arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
var replacementUrl = GetArg(args, "--url") ?? "http://127.0.0.1:8012/spirit-sync";
var bridgeBaseUrl = (GetArg(args, "--bridge-url") ?? "http://127.0.0.1:8012").TrimEnd('/');
var musicSavePath = GetArg(args, "--music-save")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpiritCity",
        "Saved",
        "SaveGames",
        "SCLS_MusicPlayer.sav"
    );
var customMusicSavePath = GetArg(args, "--custom-music-save")
    ?? Path.Combine(
        Path.GetDirectoryName(musicSavePath) ?? "",
        "SCLS_CustomMusic.sav"
    );
var spotifyProxyFolder = GetArg(args, "--spotify-proxy-folder");
var nativeSpotifyPauseEnabled = !args.Any(arg =>
    arg.Equals("--no-native-spotify-pause", StringComparison.OrdinalIgnoreCase)
);
var nativeTrackSkipEnabled = !args.Any(arg =>
    arg.Equals("--no-native-track-skip", StringComparison.OrdinalIgnoreCase)
);
var process = Process.GetProcessesByName(processName).FirstOrDefault()
    ?? throw new InvalidOperationException($"Process not found: {processName}");

var access = ProcessAccess.QueryInformation | ProcessAccess.VirtualMemoryRead;
if (
    mode.Equals("patch", StringComparison.OrdinalIgnoreCase)
    || mode.Equals("watch", StringComparison.OrdinalIgnoreCase)
)
{
    access |= ProcessAccess.VirtualMemoryWrite | ProcessAccess.VirtualMemoryOperation;
}

using var handle = OpenProcess(access, false, process.Id);
if (handle.IsInvalid)
{
    throw new Win32Exception(Marshal.GetLastWin32Error());
}

if (mode.Equals("scan", StringComparison.OrdinalIgnoreCase))
{
    var terms = args
        .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
        .Skip(1)
        .DefaultIfEmpty("Peaceful Piano")
        .ToArray();
    Scan(handle, terms);
}
else if (mode.Equals("patch", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"applied={PatchSpiritSync(handle, quiet, replacementUrl)}");
}
else if (mode.Equals("watch", StringComparison.OrdinalIgnoreCase))
{
    var interval = int.TryParse(GetArg(args, "--interval-ms") ?? "", out var parsedInterval)
        ? parsedInterval
        : 1500;
    var duration = int.TryParse(GetArg(args, "--duration-sec") ?? "", out var parsedDuration)
        ? parsedDuration
        : 120;
    var spotifyProxyDetector = new SpotifyProxyDetector(customMusicSavePath, spotifyProxyFolder);
    using var spotifyProxyController = spotifyProxyDetector.IsEnabled
        ? new SpotifyProxyController(musicSavePath, bridgeBaseUrl, spotifyProxyDetector, quiet)
        : null;
    spotifyProxyController?.Start(TimeSpan.FromMilliseconds(500));

    using var nativeTrackController =
        spotifyProxyDetector.IsEnabled && nativeTrackSkipEnabled && spotifyProxyFolder is not null
            ? new NativeTrackController(
                process.Id,
                musicSavePath,
                bridgeBaseUrl,
                spotifyProxyDetector,
                spotifyProxyFolder,
                quiet
            )
            : null;
    if (nativeTrackController?.IsUsable == true)
    {
        nativeTrackController.Start(TimeSpan.FromMilliseconds(1500));
    }

    var total = WatchSpiritSync(
        handle,
        TimeSpan.FromMilliseconds(interval),
        TimeSpan.FromSeconds(duration),
        quiet,
        replacementUrl,
        nativeSpotifyPauseEnabled
            ? new NativeMusicMonitor(musicSavePath, bridgeBaseUrl, spotifyProxyDetector, quiet)
            : null
    );
    Console.WriteLine($"totalApplied={total}");
}
else if (mode.Equals("dump", StringComparison.OrdinalIgnoreCase))
{
    var addressText = args.Skip(1).FirstOrDefault()
        ?? throw new ArgumentException("Usage: SpiritCityRuntimePatch dump <hex-address> [length]");
    var lengthText = args.Skip(2).FirstOrDefault();
    var length = lengthText is not null && int.TryParse(lengthText, out var parsedLength)
        ? parsedLength
        : 1024;

    Dump(handle, ParseAddress(addressText), length);
}
else if (mode.Equals("refs", StringComparison.OrdinalIgnoreCase))
{
    var terms = args
        .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
        .Skip(1)
        .DefaultIfEmpty("Peaceful Piano")
        .ToArray();
    FindReferences(handle, terms);
}
else
{
    throw new ArgumentException("Usage: SpiritCityRuntimePatch scan [terms...] | dump <hex-address> [length] | refs [terms...] | patch | watch [--interval-ms=1500] [--duration-sec=120] [--quiet] [--bridge-url=http://127.0.0.1:8012] [--music-save=<path>] [--custom-music-save=<path>] [--spotify-proxy-folder=<path>] [--no-native-spotify-pause]");
}

static void Scan(SafeProcessHandle handle, string[] terms)
{
    var patterns = terms
        .SelectMany(term => new[]
        {
            new Pattern(term, $"{term} [utf8]", Encoding.UTF8.GetBytes(term)),
            new Pattern(term, $"{term} [utf16]", Encoding.Unicode.GetBytes(term)),
        })
        .ToArray();

    foreach (var region in EnumerateReadableRegions(handle))
    {
        if (region.RegionSize > 128 * 1024 * 1024)
        {
            continue;
        }

        var buffer = new byte[(int)region.RegionSize];
        if (!Native.ReadProcessMemory(handle, region.BaseAddress, buffer, buffer.Length, out var bytesRead) || bytesRead == 0)
        {
            continue;
        }

        foreach (var pattern in patterns)
        {
            foreach (var index in FindAll(buffer, bytesRead, pattern.Bytes))
            {
                Console.WriteLine(
                    $"{pattern.Label}\t0x{region.BaseAddress.ToInt64() + index:X}\t{region.Protect}\t{region.Type}\t{Preview(buffer, index)}"
                );
            }
        }
    }
}

static int WatchSpiritSync(
    SafeProcessHandle handle,
    TimeSpan interval,
    TimeSpan duration,
    bool quiet,
    string replacementUrl,
    NativeMusicMonitor? nativeMusicMonitor
)
{
    var total = 0;
    var deadline = DateTimeOffset.UtcNow + duration;

    nativeMusicMonitor?.Start(TimeSpan.FromMilliseconds(500));
    try
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            var applied = PatchSpiritSync(handle, quiet, replacementUrl);
            total += applied;

            if (!quiet && applied > 0)
            {
                Console.WriteLine($"watchPatched={applied}");
            }

            Thread.Sleep(interval);
        }
    }
    finally
    {
        nativeMusicMonitor?.Stop();
    }

    return total;
}

static int PatchSpiritSync(SafeProcessHandle handle, bool quiet, string replacementUrl)
{
    var replacements = new[]
    {
        Replacement.Utf16("Peaceful Piano", "Spirit Sync"),
        Replacement.Utf8("Peaceful Piano", "Spirit Sync"),
        Replacement.Utf16("Peaceful Day .... [calm piano] - YouTube", "Spirit Sync"),
        Replacement.Utf8("Peaceful Day .... [calm piano] - YouTube", "Spirit Sync"),
        Replacement.Utf16("https://youtu.be/cYPJaHT5f3E?si=Y9jpHpyDpU8WPzgY", replacementUrl),
        Replacement.Utf8("https://youtu.be/cYPJaHT5f3E?si=Y9jpHpyDpU8WPzgY", replacementUrl),
        Replacement.Utf16("http://youtu.be/cYPJaHT5f3E?si=Y9jpHpyDpU8WPzgY", replacementUrl),
        Replacement.Utf8("http://youtu.be/cYPJaHT5f3E?si=Y9jpHpyDpU8WPzgY", replacementUrl),
        Replacement.Utf16("https://www.youtube.com/watch?si=Y9jpHpyDpU8WPzgY&v=cYPJaHT5f3E&feature=youtu.be", replacementUrl),
        Replacement.Utf8("https://www.youtube.com/watch?si=Y9jpHpyDpU8WPzgY&v=cYPJaHT5f3E&feature=youtu.be", replacementUrl),
        Replacement.Utf16("http://www.youtube.com/watch?si=Y9jpHpyDpU8WPzgY&v=cYPJaHT5f3E&feature=youtu.be", replacementUrl),
        Replacement.Utf8("http://www.youtube.com/watch?si=Y9jpHpyDpU8WPzgY&v=cYPJaHT5f3E&feature=youtu.be", replacementUrl),
        Replacement.Utf16("watch?si=Y9jpHpyDpU8WPzgY&v=cYPJaHT5f3E&feature=youtu.be", replacementUrl),
        Replacement.Utf8("watch?si=Y9jpHpyDpU8WPzgY&v=cYPJaHT5f3E&feature=youtu.be", replacementUrl),
        Replacement.Utf16("https://www.youtube.com/watch?v=cYPJaHT5f3E", replacementUrl),
        Replacement.Utf8("https://www.youtube.com/watch?v=cYPJaHT5f3E", replacementUrl),
        Replacement.Utf16("http://www.youtube.com/watch?v=cYPJaHT5f3E", replacementUrl),
        Replacement.Utf8("http://www.youtube.com/watch?v=cYPJaHT5f3E", replacementUrl),
        Replacement.Utf16("https://youtube.com/watch?v=cYPJaHT5f3E", replacementUrl),
        Replacement.Utf8("https://youtube.com/watch?v=cYPJaHT5f3E", replacementUrl),
        Replacement.Utf16("http://youtube.com/watch?v=cYPJaHT5f3E", replacementUrl),
        Replacement.Utf8("http://youtube.com/watch?v=cYPJaHT5f3E", replacementUrl),
        Replacement.Utf16("youtube.com/watch?v=cYPJaHT5f3E", replacementUrl),
        Replacement.Utf8("youtube.com/watch?v=cYPJaHT5f3E", replacementUrl),
    }.Where(replacement => replacement.CanFit).ToArray();

    var applied = 0;

    foreach (var region in EnumerateWritableRegions(handle))
    {
        if (region.RegionSize > 128 * 1024 * 1024)
        {
            continue;
        }

        var buffer = new byte[(int)region.RegionSize];
        if (!Native.ReadProcessMemory(handle, region.BaseAddress, buffer, buffer.Length, out var bytesRead) || bytesRead == 0)
        {
            continue;
        }

        foreach (var replacement in replacements)
        {
            foreach (var index in FindAll(buffer, bytesRead, replacement.From))
            {
                var address = region.BaseAddress + index;
                var bytes = replacement.ToPadded();
                if (!Native.WriteProcessMemory(handle, address, bytes, bytes.Length, out var written) || written != bytes.Length)
                {
                    continue;
                }

                applied += 1;
                if (!quiet)
                {
                    Console.WriteLine(
                        $"patched\t{replacement.Label}\t0x{address.ToInt64():X}\t{region.Protect}\t{region.Type}"
                    );
                }
            }
        }
    }

    return applied;
}

static void Dump(SafeProcessHandle handle, long address, int length)
{
    if (length <= 0)
    {
        throw new ArgumentOutOfRangeException(nameof(length));
    }

    var region = EnumerateReadableRegions(handle)
        .FirstOrDefault(item => address >= item.BaseAddress.ToInt64() && address < item.BaseAddress.ToInt64() + item.RegionSize);

    if (region is null)
    {
        throw new InvalidOperationException($"Address is not in a readable region: 0x{address:X}");
    }

    var offset = address - region.BaseAddress.ToInt64();
    var readableLength = (int)Math.Min(length, region.RegionSize - offset);
    var buffer = new byte[readableLength];
    if (!Native.ReadProcessMemory(handle, new IntPtr(address), buffer, buffer.Length, out var bytesRead) || bytesRead == 0)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    Console.WriteLine($"region\t0x{region.BaseAddress.ToInt64():X}\t{region.RegionSize}\t{region.Protect}\t{region.Type}");
    Console.WriteLine($"dump\t0x{address:X}\t{bytesRead}");
    Console.WriteLine();
    Console.WriteLine(HexDump(buffer, bytesRead, address));
    Console.WriteLine();
    Console.WriteLine("utf8-printable:");
    Console.WriteLine(Printable(Encoding.UTF8.GetString(buffer, 0, bytesRead)));
    Console.WriteLine();
    Console.WriteLine("utf16le-printable:");
    var evenLength = bytesRead - (bytesRead % 2);
    Console.WriteLine(Printable(Encoding.Unicode.GetString(buffer, 0, evenLength)));
}

static void FindReferences(SafeProcessHandle handle, string[] terms)
{
    foreach (var term in terms)
    {
        var stringHits = FindPatternHits(handle, Encoding.Unicode.GetBytes(term))
            .Where(hit => hit.Region.Type == MemoryType.Private)
            .Take(128)
            .ToArray();

        Console.WriteLine($"term\t{term}\tstringHits={stringHits.Length}");

        foreach (var hit in stringHits)
        {
            var pointer = BitConverter.GetBytes(hit.Address);
            var pointerHits = FindPatternHits(handle, pointer)
                .Where(reference => reference.Address != hit.Address)
                .Take(32)
                .ToArray();

            Console.WriteLine($"string\t0x{hit.Address:X}\t{hit.Region.Protect}\t{hit.Region.Type}\trefs={pointerHits.Length}\t{Preview(hit.Buffer, hit.Index)}");

            foreach (var reference in pointerHits)
            {
                Console.WriteLine(
                    $"ref\t0x{reference.Address:X}\t{reference.Region.Protect}\t{reference.Region.Type}\t{Preview(reference.Buffer, reference.Index)}"
                );
            }
        }
    }
}

static IEnumerable<PatternHit> FindPatternHits(SafeProcessHandle handle, byte[] pattern)
{
    foreach (var region in EnumerateReadableRegions(handle))
    {
        if (region.RegionSize > 128 * 1024 * 1024)
        {
            continue;
        }

        var buffer = new byte[(int)region.RegionSize];
        if (!Native.ReadProcessMemory(handle, region.BaseAddress, buffer, buffer.Length, out var bytesRead) || bytesRead == 0)
        {
            continue;
        }

        foreach (var index in FindAll(buffer, bytesRead, pattern))
        {
            yield return new PatternHit(region.BaseAddress.ToInt64() + index, region, buffer, index);
        }
    }
}

static IEnumerable<MemoryRegion> EnumerateReadableRegions(SafeProcessHandle handle)
{
    foreach (var region in EnumerateRegions(handle))
    {
        if (
            region.State == MemoryState.Commit
            && !region.Protect.HasFlag(MemoryProtect.Guard)
            && !region.Protect.HasFlag(MemoryProtect.NoAccess)
            && IsReadable(region.Protect)
        )
        {
            yield return region;
        }
    }
}

static IEnumerable<MemoryRegion> EnumerateWritableRegions(SafeProcessHandle handle)
{
    foreach (var region in EnumerateReadableRegions(handle))
    {
        if (IsWritable(region.Protect))
        {
            yield return region;
        }
    }
}

static IEnumerable<MemoryRegion> EnumerateRegions(SafeProcessHandle handle)
{
    var address = IntPtr.Zero;
    while (Native.VirtualQueryEx(handle, address, out var info, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
    {
        yield return new MemoryRegion(
            info.BaseAddress,
            checked((long)info.RegionSize),
            info.State,
            info.Protect,
            info.Type
        );

        var next = info.BaseAddress.ToInt64() + checked((long)info.RegionSize);
        if (next <= address.ToInt64())
        {
            break;
        }

        address = new IntPtr(next);
    }
}

static bool IsReadable(MemoryProtect protect)
{
    var normalized = protect & ~MemoryProtect.Guard;
    return normalized is MemoryProtect.ReadOnly
        or MemoryProtect.ReadWrite
        or MemoryProtect.WriteCopy
        or MemoryProtect.ExecuteRead
        or MemoryProtect.ExecuteReadWrite
        or MemoryProtect.ExecuteWriteCopy;
}

static bool IsWritable(MemoryProtect protect)
{
    var normalized = protect & ~MemoryProtect.Guard;
    return normalized is MemoryProtect.ReadWrite
        or MemoryProtect.WriteCopy
        or MemoryProtect.ExecuteReadWrite
        or MemoryProtect.ExecuteWriteCopy;
}

static List<int> FindAll(byte[] haystack, int length, byte[] needle)
{
    var results = new List<int>();
    var offset = 0;
    var span = haystack.AsSpan(0, length);
    while (offset < span.Length)
    {
        var index = span[offset..].IndexOf(needle);
        if (index < 0)
        {
            break;
        }

        results.Add(offset + index);
        offset += index + Math.Max(1, needle.Length);
    }

    return results;
}

static string Preview(byte[] buffer, int index)
{
    var start = Math.Max(0, index - 32);
    var length = Math.Min(buffer.Length - start, 160);
    return string.Concat(
        buffer.AsSpan(start, length).ToArray().Select(value =>
            value is >= 32 and <= 126 ? (char)value : '.'
        )
    );
}

static string HexDump(byte[] buffer, int length, long baseAddress)
{
    var builder = new StringBuilder();
    for (var offset = 0; offset < length; offset += 16)
    {
        var lineLength = Math.Min(16, length - offset);
        var bytes = string.Join(
            " ",
            buffer.Skip(offset).Take(lineLength).Select(value => value.ToString("X2"))
        ).PadRight(47);
        var text = string.Concat(
            buffer.Skip(offset).Take(lineLength).Select(value =>
                value is >= 32 and <= 126 ? (char)value : '.'
            )
        );
        builder.AppendLine($"{baseAddress + offset:X16}  {bytes}  {text}");
    }

    return builder.ToString().TrimEnd();
}

static string Printable(string value)
{
    var builder = new StringBuilder(value.Length);
    foreach (var character in value)
    {
        builder.Append(char.IsControl(character) ? '.' : character);
    }

    return builder.ToString();
}

static long ParseAddress(string text)
{
    var normalized = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? text[2..]
        : text;
    return Convert.ToInt64(normalized, 16);
}

static string? GetArg(string[] args, string name)
{
    var prefix = $"{name}=";
    return args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
}

static SafeProcessHandle OpenProcess(ProcessAccess access, bool inheritHandle, int processId)
{
    return Native.OpenProcess(access, inheritHandle, processId);
}

sealed class NativeMusicMonitor
{
    private static readonly TimeSpan SpotifyPauseEnforcementInterval = TimeSpan.FromSeconds(4);
    private readonly string musicSavePath;
    private readonly string bridgeBaseUrl;
    private readonly SpotifyProxyDetector spotifyProxyDetector;
    private readonly bool quiet;
    private readonly HttpClient http = new();
    private Thread? thread;
    private volatile bool stopping;
    private bool wasPlaying;
    private DateTimeOffset nextSpotifyPauseEnforcement = DateTimeOffset.MinValue;
    private SaveSnapshot? lastSaveSnapshot;

    public NativeMusicMonitor(
        string musicSavePath,
        string bridgeBaseUrl,
        SpotifyProxyDetector spotifyProxyDetector,
        bool quiet
    )
    {
        this.musicSavePath = musicSavePath;
        this.bridgeBaseUrl = bridgeBaseUrl;
        this.spotifyProxyDetector = spotifyProxyDetector;
        this.quiet = quiet;
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Start(TimeSpan pollInterval)
    {
        thread = new Thread(() =>
        {
            while (!stopping)
            {
                Poll();
                Thread.Sleep(pollInterval);
            }
        })
        {
            IsBackground = true,
            Name = "Spirit Sync native music monitor",
        };
        thread.Start();
    }

    public void Stop()
    {
        stopping = true;
        thread?.Join(TimeSpan.FromSeconds(1));
    }

    public void Poll()
    {
        var snapshot = NativeMusicSave.Read(musicSavePath);
        var isPlaying = snapshot.IsPlaying;
        if (isPlaying && spotifyProxyDetector.IsProxy(snapshot))
        {
            wasPlaying = isPlaying;
            lastSaveSnapshot = snapshot;
            return;
        }

        var startedPlaying = isPlaying && !wasPlaying;
        var changedWhilePlaying = isPlaying
            && lastSaveSnapshot is not null
            && (
                snapshot.Length != lastSaveSnapshot.Length
                || snapshot.LastWriteTimeUtc != lastSaveSnapshot.LastWriteTimeUtc
            );
        var enforcementDue = isPlaying
            && DateTimeOffset.UtcNow >= nextSpotifyPauseEnforcement;

        if (startedPlaying || changedWhilePlaying || enforcementDue)
        {
            PauseSpotifyIfPlaying(
                startedPlaying
                    ? "native-started"
                    : changedWhilePlaying
                        ? "native-save-changed"
                        : "native-still-playing"
            );
            nextSpotifyPauseEnforcement = DateTimeOffset.UtcNow + SpotifyPauseEnforcementInterval;
        }

        wasPlaying = isPlaying;
        lastSaveSnapshot = snapshot;
    }

    private void PauseSpotifyIfPlaying(string reason)
    {
        try
        {
            if (!IsSpotifyPlaying())
            {
                return;
            }

            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = http.PostAsync($"{bridgeBaseUrl}/api/player/pause", content)
                .GetAwaiter()
                .GetResult();

            if (!quiet)
            {
                Console.WriteLine(
                    response.IsSuccessStatusCode
                        ? $"nativeMusicPausedSpotify=true reason={reason}"
                        : $"nativeMusicPausedSpotify=false reason={reason} status={(int)response.StatusCode}"
                );
            }
        }
        catch (Exception error)
        {
            if (!quiet)
            {
                Console.WriteLine($"nativeMusicPausedSpotify=false reason={reason} error={error.Message}");
            }
        }
    }

    private bool IsSpotifyPlaying()
    {
        using var response = http.GetAsync($"{bridgeBaseUrl}/api/status")
            .GetAwaiter()
            .GetResult();
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("playback", out var playback)
            && playback.ValueKind == JsonValueKind.Object
            && playback.TryGetProperty("is_playing", out var isPlaying)
            && isPlaying.ValueKind == JsonValueKind.True;
    }

}

static class NativeMusicSave
{
    // Spirit City (build 2.4.1) serializes the SCLS_MusicPlayer save's "IsPlaying" BoolProperty
    // ONLY while playing and omits the whole property when paused. So the presence of the
    // "IsPlaying" property name is itself the play signal. We match the full property name rather
    // than a bare "Playing" substring so an unrelated string in a future build cannot false-match
    // (the class name "SAVE_Sessions_MusicPlayer_C" does not contain "IsPlaying").
    private static readonly byte[] PlayingToken = Encoding.UTF8.GetBytes("IsPlaying");

    // Shuffle uses the same conditional serialization as IsPlaying: the "isShuffle" BoolProperty is
    // written only while shuffle is ON and omitted when off, so presence == shuffle on. (Verified
    // in build 2.4.1. Repeat/loop and next/previous are NOT written to any save, so they cannot be
    // mapped from the native bar.)
    private static readonly byte[] ShuffleToken = Encoding.UTF8.GetBytes("isShuffle");

    // Repeat/loop uses the same conditional serialization: "isLoop" is written only while on.
    private static readonly byte[] LoopToken = Encoding.UTF8.GetBytes("isLoop");

    public static SaveSnapshot Read(string musicSavePath)
    {
        try
        {
            var file = new FileInfo(musicSavePath);
            if (!file.Exists)
            {
                return SaveSnapshot.NotPlaying;
            }

            using var stream = new FileStream(
                musicSavePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            if (stream.Length <= 0 || stream.Length > 1024 * 1024)
            {
                return SaveSnapshot.NotPlaying;
            }

            var buffer = new byte[stream.Length];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            return new SaveSnapshot(
                Contains(buffer, bytesRead, PlayingToken),
                stream.Length,
                file.LastWriteTimeUtc,
                TryReadTaggedInt32(buffer, bytesRead, "currentPlaylistID"),
                TryReadTaggedDouble(buffer, bytesRead, "currentVolume"),
                Contains(buffer, bytesRead, ShuffleToken),
                Contains(buffer, bytesRead, LoopToken)
            );
        }
        catch
        {
            return SaveSnapshot.NotPlaying;
        }
    }

    private static bool Contains(byte[] buffer, int length, byte[] needle)
    {
        if (needle.Length == 0 || length < needle.Length)
        {
            return false;
        }

        return buffer.AsSpan(0, length).IndexOf(needle) >= 0;
    }

    private static int? TryReadTaggedInt32(byte[] buffer, int length, string propertyName)
    {
        var nameBytes = Encoding.UTF8.GetBytes($"{propertyName}\0");
        var nameIndex = buffer.AsSpan(0, length).IndexOf(nameBytes);
        if (nameIndex < 0)
        {
            return null;
        }

        var typeLengthOffset = nameIndex + nameBytes.Length;
        if (typeLengthOffset + 4 > length)
        {
            return null;
        }

        var typeLength = BitConverter.ToInt32(buffer, typeLengthOffset);
        if (typeLength <= 0 || typeLength > 128)
        {
            return null;
        }

        var typeOffset = typeLengthOffset + 4;
        var valueOffset = typeOffset + typeLength + 9;
        if (valueOffset + 4 > length)
        {
            return null;
        }

        var typeName = Encoding.UTF8.GetString(buffer, typeOffset, typeLength).TrimEnd('\0');
        if (!typeName.Equals("IntProperty", StringComparison.Ordinal))
        {
            return null;
        }

        return BitConverter.ToInt32(buffer, valueOffset);
    }

    private static double? TryReadTaggedDouble(byte[] buffer, int length, string propertyName)
    {
        var nameBytes = Encoding.UTF8.GetBytes($"{propertyName}\0");
        var nameIndex = buffer.AsSpan(0, length).IndexOf(nameBytes);
        if (nameIndex < 0)
        {
            return null;
        }

        var typeLengthOffset = nameIndex + nameBytes.Length;
        if (typeLengthOffset + 4 > length)
        {
            return null;
        }

        var typeLength = BitConverter.ToInt32(buffer, typeLengthOffset);
        if (typeLength <= 0 || typeLength > 128)
        {
            return null;
        }

        var typeOffset = typeLengthOffset + 4;
        var valueOffset = typeOffset + typeLength + 9;
        if (valueOffset + 8 > length)
        {
            return null;
        }

        var typeName = Encoding.UTF8.GetString(buffer, typeOffset, typeLength).TrimEnd('\0');
        if (!typeName.Equals("DoubleProperty", StringComparison.Ordinal))
        {
            return null;
        }

        return BitConverter.ToDouble(buffer, valueOffset);
    }
}

static class CustomMusicSave
{
    public static string? ReadImportFolderAddress(string customMusicSavePath)
    {
        try
        {
            if (!File.Exists(customMusicSavePath))
            {
                return null;
            }

            var buffer = File.ReadAllBytes(customMusicSavePath);
            return TryReadTaggedString(buffer, buffer.Length, "ImportFolderAddress");
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadTaggedString(byte[] buffer, int length, string propertyName)
    {
        var nameBytes = Encoding.UTF8.GetBytes($"{propertyName}\0");
        var nameIndex = buffer.AsSpan(0, length).IndexOf(nameBytes);
        if (nameIndex < 0)
        {
            return null;
        }

        var typeLengthOffset = nameIndex + nameBytes.Length;
        if (typeLengthOffset + 4 > length)
        {
            return null;
        }

        var typeLength = BitConverter.ToInt32(buffer, typeLengthOffset);
        if (typeLength <= 0 || typeLength > 128)
        {
            return null;
        }

        var typeOffset = typeLengthOffset + 4;
        var valueLengthOffset = typeOffset + typeLength + 9;
        if (valueLengthOffset + 4 > length)
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

        var valueOffset = valueLengthOffset + 4;
        if (valueOffset + valueLength > length)
        {
            return null;
        }

        return Encoding.UTF8.GetString(buffer, valueOffset, valueLength)
            .TrimEnd('\0')
            .Trim();
    }
}

sealed class SpotifyProxyDetector
{
    private readonly string customMusicSavePath;
    private readonly string? spotifyProxyFolder;
    private string? cachedImportFolder;
    private DateTime cachedImportFolderTimestamp = DateTime.MinValue;

    public SpotifyProxyDetector(string customMusicSavePath, string? spotifyProxyFolder)
    {
        this.customMusicSavePath = customMusicSavePath;
        this.spotifyProxyFolder = string.IsNullOrWhiteSpace(spotifyProxyFolder)
            ? null
            : NormalizePath(spotifyProxyFolder);
    }

    public bool IsEnabled => spotifyProxyFolder is not null;

    public bool IsProxy(SaveSnapshot snapshot)
    {
        return IsEnabled
            && snapshot.CurrentPlaylistId is >= 200 and < 300
            && PathsEqual(GetImportFolder(), spotifyProxyFolder);
    }

    private string? GetImportFolder()
    {
        try
        {
            var lastWriteTime = File.Exists(customMusicSavePath)
                ? File.GetLastWriteTimeUtc(customMusicSavePath)
                : DateTime.MinValue;
            if (lastWriteTime == cachedImportFolderTimestamp)
            {
                return cachedImportFolder;
            }

            cachedImportFolderTimestamp = lastWriteTime;
            cachedImportFolder = NormalizePath(CustomMusicSave.ReadImportFolderAddress(customMusicSavePath));
            return cachedImportFolder;
        }
        catch
        {
            cachedImportFolderTimestamp = DateTime.MinValue;
            cachedImportFolder = null;
            return null;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return left is not null
            && right is not null
            && left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Replace('/', Path.DirectorySeparatorChar).Trim();
        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
            return null;
        }

        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

sealed class SpotifyProxyController : IDisposable
{
    private static readonly TimeSpan SpotifyPlayEnforcementInterval = TimeSpan.FromSeconds(4);
    private readonly string musicSavePath;
    private readonly string bridgeBaseUrl;
    private readonly SpotifyProxyDetector spotifyProxyDetector;
    private readonly bool quiet;
    private readonly HttpClient http = new();
    private Thread? thread;
    private volatile bool stopping;
    private bool? wasProxyPlaying;
    private DateTimeOffset nextSpotifyPlayEnforcement = DateTimeOffset.MinValue;
    // Baselines for native -> Spotify mapping. Null means "not yet baselined for this proxy
    // session"; the first observed value becomes the baseline and is NOT pushed, so entering the
    // proxy never clobbers Spotify's existing volume/shuffle. Only later changes are forwarded.
    private int? lastVolumePercent;
    private bool? lastShuffle;
    private bool? lastLoop;

    public SpotifyProxyController(
        string musicSavePath,
        string bridgeBaseUrl,
        SpotifyProxyDetector spotifyProxyDetector,
        bool quiet
    )
    {
        this.musicSavePath = musicSavePath;
        this.bridgeBaseUrl = bridgeBaseUrl;
        this.spotifyProxyDetector = spotifyProxyDetector;
        this.quiet = quiet;
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Start(TimeSpan pollInterval)
    {
        thread = new Thread(() =>
        {
            while (!stopping)
            {
                Poll();
                Thread.Sleep(pollInterval);
            }
        })
        {
            IsBackground = true,
            Name = "Spirit Sync Spotify proxy controller",
        };
        thread.Start();
    }

    public void Stop()
    {
        stopping = true;
        thread?.Join(TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        Stop();
        http.Dispose();
    }

    private void Poll()
    {
        var snapshot = NativeMusicSave.Read(musicSavePath);
        if (!spotifyProxyDetector.IsProxy(snapshot))
        {
            wasProxyPlaying = null;
            lastVolumePercent = null;
            lastShuffle = null;
            lastLoop = null;
            return;
        }

        SyncVolume(snapshot);
        SyncShuffle(snapshot);
        SyncRepeat(snapshot);

        var changed = wasProxyPlaying != snapshot.IsPlaying;
        var enforcementDue = snapshot.IsPlaying
            && DateTimeOffset.UtcNow >= nextSpotifyPlayEnforcement;

        if (changed || enforcementDue)
        {
            if (snapshot.IsPlaying)
            {
                PlaySpotifyIfNeeded(changed ? "proxy-play" : "proxy-still-playing");
                nextSpotifyPlayEnforcement = DateTimeOffset.UtcNow + SpotifyPlayEnforcementInterval;
            }
            else
            {
                PauseSpotifyIfPlaying("proxy-pause");
            }
        }

        wasProxyPlaying = snapshot.IsPlaying;
    }

    private void PlaySpotifyIfNeeded(string reason)
    {
        try
        {
            if (IsSpotifyPlaying())
            {
                return;
            }

            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = http.PostAsync($"{bridgeBaseUrl}/api/player/play", content)
                .GetAwaiter()
                .GetResult();

            if (!quiet)
            {
                Console.WriteLine(
                    response.IsSuccessStatusCode
                        ? $"spotifyProxyPlayedSpotify=true reason={reason}"
                        : $"spotifyProxyPlayedSpotify=false reason={reason} status={(int)response.StatusCode}"
                );
            }
        }
        catch (Exception error)
        {
            if (!quiet)
            {
                Console.WriteLine($"spotifyProxyPlayedSpotify=false reason={reason} error={error.Message}");
            }
        }
    }

    private void PauseSpotifyIfPlaying(string reason)
    {
        try
        {
            if (!IsSpotifyPlaying())
            {
                return;
            }

            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = http.PostAsync($"{bridgeBaseUrl}/api/player/pause", content)
                .GetAwaiter()
                .GetResult();

            if (!quiet)
            {
                Console.WriteLine(
                    response.IsSuccessStatusCode
                        ? $"spotifyProxyPausedSpotify=true reason={reason}"
                        : $"spotifyProxyPausedSpotify=false reason={reason} status={(int)response.StatusCode}"
                );
            }
        }
        catch (Exception error)
        {
            if (!quiet)
            {
                Console.WriteLine($"spotifyProxyPausedSpotify=false reason={reason} error={error.Message}");
            }
        }
    }

    private void SyncVolume(SaveSnapshot snapshot)
    {
        if (snapshot.Volume is not double volume)
        {
            return;
        }

        var percent = Math.Clamp((int)Math.Round(volume * 100), 0, 100);
        if (lastVolumePercent is null)
        {
            lastVolumePercent = percent; // baseline; do not push on first sight
            return;
        }

        if (percent == lastVolumePercent)
        {
            return;
        }

        lastVolumePercent = percent;
        PostPlayerCommand(
            "/api/player/volume",
            $"{{\"volume\":{percent}}}",
            $"spotifyProxyVolume={percent}"
        );
    }

    private void SyncShuffle(SaveSnapshot snapshot)
    {
        if (lastShuffle is null)
        {
            lastShuffle = snapshot.IsShuffle; // baseline; do not push on first sight
            return;
        }

        if (snapshot.IsShuffle == lastShuffle)
        {
            return;
        }

        lastShuffle = snapshot.IsShuffle;
        PostPlayerCommand(
            "/api/player/shuffle",
            $"{{\"state\":{(snapshot.IsShuffle ? "true" : "false")}}}",
            $"spotifyProxyShuffle={snapshot.IsShuffle}"
        );
    }

    private void SyncRepeat(SaveSnapshot snapshot)
    {
        if (lastLoop is null)
        {
            lastLoop = snapshot.IsLoop; // baseline; do not push on first sight
            return;
        }

        if (snapshot.IsLoop == lastLoop)
        {
            return;
        }

        lastLoop = snapshot.IsLoop;
        // Native loop is a single on/off; map on -> repeat the playlist, off -> no repeat.
        var state = snapshot.IsLoop ? "context" : "off";
        PostPlayerCommand(
            "/api/player/repeat",
            $"{{\"state\":\"{state}\"}}",
            $"spotifyProxyRepeat={state}"
        );
    }

    private void PostPlayerCommand(string path, string jsonBody, string label)
    {
        try
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var response = http.PostAsync($"{bridgeBaseUrl}{path}", content)
                .GetAwaiter()
                .GetResult();

            if (!quiet)
            {
                Console.WriteLine(
                    response.IsSuccessStatusCode
                        ? $"{label} ok=true"
                        : $"{label} ok=false status={(int)response.StatusCode}"
                );
            }
        }
        catch (Exception error)
        {
            if (!quiet)
            {
                Console.WriteLine($"{label} ok=false error={error.Message}");
            }
        }
    }

    private bool IsSpotifyPlaying()
    {
        using var response = http.GetAsync($"{bridgeBaseUrl}/api/status")
            .GetAwaiter()
            .GetResult();
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("playback", out var playback)
            && playback.ValueKind == JsonValueKind.Object
            && playback.TryGetProperty("is_playing", out var isPlaying)
            && isPlaying.ValueKind == JsonValueKind.True;
    }
}

// Maps native next/previous on the Custom proxy bar to Spotify. The save records no track index,
// so instead the proxy is a multi-track playlist of identically-silent tracks with uniform names;
// the game references the CURRENT track's title far more often in memory than the others, so the
// title with the most occurrences identifies the current index. A change in that index means the
// user pressed native next/previous, which we forward to Spotify. See "Verified Native Save
// Behavior" in AGENTS.md. This is best-effort and fail-safe: ambiguous scans are ignored.
sealed class NativeTrackController : IDisposable
{
    private readonly int processId;
    private readonly string musicSavePath;
    private readonly string bridgeBaseUrl;
    private readonly SpotifyProxyDetector detector;
    private readonly bool quiet;
    private readonly HttpClient http = new();
    private readonly int trackCount; // tracks in game order (= file creation order)
    private readonly byte[][] displayNeedles; // UTF-16 file name without extension + null
    private readonly byte[][] fileNeedles; // UTF-16 full file name (with .wav) + null
    private Thread? thread;
    private volatile bool stopping;
    private int? lastIndex;

    public NativeTrackController(
        int processId,
        string musicSavePath,
        string bridgeBaseUrl,
        SpotifyProxyDetector detector,
        string proxyFolder,
        bool quiet
    )
    {
        this.processId = processId;
        this.musicSavePath = musicSavePath;
        this.bridgeBaseUrl = bridgeBaseUrl;
        this.detector = detector;
        this.quiet = quiet;
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The game references the current track by both its display name and its file name; we
        // count both so the current track stands out from the others even when the music panel is
        // closed (only the now-playing bar copy remains).
        var fileNames = LoadTrackFileNames(proxyFolder);
        trackCount = fileNames.Length;
        displayNeedles = fileNames
            .Select(name => Encoding.Unicode.GetBytes(Path.GetFileNameWithoutExtension(name) + "\0"))
            .ToArray();
        fileNeedles = fileNames
            .Select(name => Encoding.Unicode.GetBytes(name + "\0"))
            .ToArray();
    }

    // Needs at least two tracks for next/previous to move an index.
    public bool IsUsable => trackCount >= 2;

    public void Start(TimeSpan pollInterval)
    {
        thread = new Thread(() =>
        {
            while (!stopping)
            {
                try
                {
                    Poll();
                }
                catch
                {
                    // Best-effort; never let a scan failure crash the patcher.
                }

                Thread.Sleep(pollInterval);
            }
        })
        {
            IsBackground = true,
            Name = "Spirit Sync native track controller",
        };
        thread.Start();
    }

    public void Stop()
    {
        stopping = true;
        thread?.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Stop();
        http.Dispose();
    }

    private static string[] LoadTrackFileNames(string? proxyFolder)
    {
        if (string.IsNullOrWhiteSpace(proxyFolder))
        {
            return Array.Empty<string>();
        }

        try
        {
            // Order by creation time to match how the game lists custom tracks, so a +1 step in
            // this list corresponds to native "next".
            return new DirectoryInfo(proxyFolder)
                .GetFiles("*.wav")
                .OrderBy(file => file.CreationTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .Select(file => file.Name)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void Poll()
    {
        var snapshot = NativeMusicSave.Read(musicSavePath);
        if (!detector.IsProxy(snapshot))
        {
            lastIndex = null; // re-baseline next time the proxy becomes active
            return;
        }

        var index = DetectCurrentIndex();
        if (index < 0)
        {
            return; // ambiguous scan; keep the last known index
        }

        if (lastIndex is null)
        {
            lastIndex = index; // baseline; do not skip on first detection
            return;
        }

        if (index == lastIndex)
        {
            return;
        }

        var previous = lastIndex.Value;
        lastIndex = index;

        var count = trackCount;
        var forward = ((index - previous) % count + count) % count; // steps moving forward on the ring
        bool next;
        if (snapshot.IsShuffle)
        {
            next = true; // shuffle picks a random track; map any change to a single Spotify next
        }
        else if (forward == 1)
        {
            next = true;
        }
        else if (forward == count - 1)
        {
            next = false; // moved back one (wrapped around the ring)
        }
        else
        {
            next = forward <= count / 2; // unusual multi-step jump: take the nearest direction
        }

        PostSkip(next, $"nativeIndex {previous}->{index}");
    }

    private int DetectCurrentIndex()
    {
        using var handle = Native.OpenProcess(
            ProcessAccess.QueryInformation | ProcessAccess.VirtualMemoryRead,
            false,
            processId
        );
        if (handle.IsInvalid)
        {
            return -1;
        }

        var counts = new long[trackCount];
        var address = IntPtr.Zero;
        var infoSize = (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (Native.VirtualQueryEx(handle, address, out var info, infoSize) != 0)
        {
            var regionSize = checked((long)info.RegionSize);
            var nextAddress = info.BaseAddress.ToInt64() + regionSize;
            if (nextAddress <= address.ToInt64())
            {
                break;
            }

            address = new IntPtr(nextAddress);

            // The track-title strings are runtime allocations in private committed heap. Limiting
            // the scan to that keeps it far lighter than a full-process scan.
            if (info.State != MemoryState.Commit || info.Type != MemoryType.Private)
            {
                continue;
            }

            var protect = info.Protect & ~MemoryProtect.Guard;
            var readable = protect is MemoryProtect.ReadWrite
                or MemoryProtect.ReadOnly
                or MemoryProtect.WriteCopy
                or MemoryProtect.ExecuteRead
                or MemoryProtect.ExecuteReadWrite
                or MemoryProtect.ExecuteWriteCopy;
            if (!readable || regionSize <= 0 || regionSize > 128 * 1024 * 1024)
            {
                continue;
            }

            var buffer = new byte[(int)regionSize];
            if (!Native.ReadProcessMemory(handle, info.BaseAddress, buffer, buffer.Length, out var read) || read == 0)
            {
                continue;
            }

            for (var i = 0; i < trackCount; i += 1)
            {
                counts[i] += CountOccurrences(buffer, read, displayNeedles[i])
                    + CountOccurrences(buffer, read, fileNeedles[i]);
            }
        }

        // The current track's title appears the most; require a clear margin over the runner-up so
        // a transient or tied scan does not produce a phantom skip.
        var best = -1;
        long bestCount = 0;
        long secondCount = 0;
        for (var i = 0; i < counts.Length; i += 1)
        {
            if (counts[i] > bestCount)
            {
                secondCount = bestCount;
                bestCount = counts[i];
                best = i;
            }
            else if (counts[i] > secondCount)
            {
                secondCount = counts[i];
            }
        }

        return best >= 0 && bestCount >= secondCount + 2 ? best : -1;
    }

    private static long CountOccurrences(byte[] buffer, int length, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        long count = 0;
        var offset = 0;
        var span = buffer.AsSpan(0, length);
        while (offset <= span.Length - needle.Length)
        {
            var index = span[offset..].IndexOf(needle);
            if (index < 0)
            {
                break;
            }

            count += 1;
            offset += index + needle.Length;
        }

        return count;
    }

    private void PostSkip(bool next, string reason)
    {
        var path = next ? "/api/player/next" : "/api/player/previous";
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = http.PostAsync($"{bridgeBaseUrl}{path}", content)
                .GetAwaiter()
                .GetResult();
            if (!quiet)
            {
                Console.WriteLine(
                    $"spotifyProxySkip={(next ? "next" : "previous")} reason={reason} ok={response.IsSuccessStatusCode}"
                );
            }
        }
        catch (Exception error)
        {
            if (!quiet)
            {
                Console.WriteLine($"spotifyProxySkip={(next ? "next" : "previous")} reason={reason} error={error.Message}");
            }
        }
    }
}

sealed record SaveSnapshot(
    bool IsPlaying,
    long Length,
    DateTime LastWriteTimeUtc,
    int? CurrentPlaylistId,
    double? Volume,
    bool IsShuffle,
    bool IsLoop
)
{
    public static readonly SaveSnapshot NotPlaying = new(false, 0, DateTime.MinValue, null, null, false, false);
}

[Flags]
enum ProcessAccess : uint
{
    QueryInformation = 0x0400,
    VirtualMemoryOperation = 0x0008,
    VirtualMemoryRead = 0x0010,
    VirtualMemoryWrite = 0x0020,
}

[Flags]
enum MemoryProtect : uint
{
    NoAccess = 0x01,
    ReadOnly = 0x02,
    ReadWrite = 0x04,
    WriteCopy = 0x08,
    Execute = 0x10,
    ExecuteRead = 0x20,
    ExecuteReadWrite = 0x40,
    ExecuteWriteCopy = 0x80,
    Guard = 0x100,
}

enum MemoryState : uint
{
    Commit = 0x1000,
}

enum MemoryType : uint
{
    Private = 0x20000,
    Mapped = 0x40000,
    Image = 0x1000000,
}

[StructLayout(LayoutKind.Sequential)]
struct MEMORY_BASIC_INFORMATION
{
    public IntPtr BaseAddress;
    public IntPtr AllocationBase;
    public MemoryProtect AllocationProtect;
    public nuint RegionSize;
    public MemoryState State;
    public MemoryProtect Protect;
    public MemoryType Type;
}

sealed record MemoryRegion(
    IntPtr BaseAddress,
    long RegionSize,
    MemoryState State,
    MemoryProtect Protect,
    MemoryType Type
);

sealed record Pattern(string Term, string Label, byte[] Bytes);

sealed record PatternHit(long Address, MemoryRegion Region, byte[] Buffer, int Index);

sealed record Replacement(string Label, byte[] From, byte[] To)
{
    public static Replacement Utf8(string from, string to) =>
        new($"{from} -> {to} [utf8]", Encoding.UTF8.GetBytes(from), Encoding.UTF8.GetBytes(to));

    public static Replacement Utf16(string from, string to) =>
        new($"{from} -> {to} [utf16]", Encoding.Unicode.GetBytes(from), Encoding.Unicode.GetBytes(to));

    public bool CanFit => To.Length <= From.Length;

    public byte[] ToPadded()
    {
        if (To.Length > From.Length)
        {
            throw new InvalidOperationException($"{Label} replacement is longer than source.");
        }

        var result = new byte[From.Length];
        To.CopyTo(result.AsSpan());
        return result;
    }
}

sealed class SafeProcessHandle : SafeHandle
{
    public SafeProcessHandle() : base(IntPtr.Zero, true) { }

    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    protected override bool ReleaseHandle()
    {
        return Native.CloseHandle(handle);
    }
}

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(ProcessAccess processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nuint VirtualQueryEx(
        SafeProcessHandle processHandle,
        IntPtr address,
        out MEMORY_BASIC_INFORMATION buffer,
        nuint length
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(
        SafeProcessHandle processHandle,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out int numberOfBytesRead
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(
        SafeProcessHandle processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out int numberOfBytesWritten
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
