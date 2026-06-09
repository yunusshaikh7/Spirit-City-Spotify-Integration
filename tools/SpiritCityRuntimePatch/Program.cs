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
var nativeSpotifyPauseEnabled = !args.Any(arg =>
    arg.Equals("--no-native-spotify-pause", StringComparison.OrdinalIgnoreCase)
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
    var total = WatchSpiritSync(
        handle,
        TimeSpan.FromMilliseconds(interval),
        TimeSpan.FromSeconds(duration),
        quiet,
        replacementUrl,
        nativeSpotifyPauseEnabled
            ? new NativeMusicMonitor(musicSavePath, bridgeBaseUrl, quiet)
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
    throw new ArgumentException("Usage: SpiritCityRuntimePatch scan [terms...] | dump <hex-address> [length] | refs [terms...] | patch | watch [--interval-ms=1500] [--duration-sec=120] [--quiet] [--bridge-url=http://127.0.0.1:8012] [--music-save=<path>] [--no-native-spotify-pause]");
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
    private static readonly byte[] PlayingToken = Encoding.UTF8.GetBytes("Playing");
    private readonly string musicSavePath;
    private readonly string bridgeBaseUrl;
    private readonly bool quiet;
    private readonly HttpClient http = new();
    private Thread? thread;
    private volatile bool stopping;
    private bool wasPlaying;

    public NativeMusicMonitor(string musicSavePath, string bridgeBaseUrl, bool quiet)
    {
        this.musicSavePath = musicSavePath;
        this.bridgeBaseUrl = bridgeBaseUrl;
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
        var isPlaying = IsNativeMusicPlaying();
        if (isPlaying && !wasPlaying)
        {
            PauseSpotifyIfPlaying();
        }

        wasPlaying = isPlaying;
    }

    private bool IsNativeMusicPlaying()
    {
        try
        {
            using var stream = new FileStream(
                musicSavePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            if (stream.Length <= 0 || stream.Length > 1024 * 1024)
            {
                return false;
            }

            var buffer = new byte[stream.Length];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            return Contains(buffer, bytesRead, PlayingToken);
        }
        catch
        {
            return false;
        }
    }

    private void PauseSpotifyIfPlaying()
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
                        ? "nativeMusicPausedSpotify=true"
                        : $"nativeMusicPausedSpotify=false status={(int)response.StatusCode}"
                );
            }
        }
        catch (Exception error)
        {
            if (!quiet)
            {
                Console.WriteLine($"nativeMusicPausedSpotify=false error={error.Message}");
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

    private static bool Contains(byte[] buffer, int length, byte[] needle)
    {
        if (needle.Length == 0 || length < needle.Length)
        {
            return false;
        }

        return buffer.AsSpan(0, length).IndexOf(needle) >= 0;
    }
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
