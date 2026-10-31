using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spice.Wire;

namespace SpiceViewer;

enum ScalingMode
{
    [JsonStringEnumMemberName("fit")] Fit,
    [JsonStringEnumMemberName("oneToOne")] OneToOne,
}

enum ShortcutMode
{
    [JsonStringEnumMemberName("always")] Always,
    [JsonStringEnumMemberName("fullscreenOnly")] FullscreenOnly,
    [JsonStringEnumMemberName("never")] Never,
}

/// <summary>Server mouse mode: show the guest's cursor shape on the captured pointer, or hide the pointer (the draft's behaviour).</summary>
enum ServerCursorMode
{
    [JsonStringEnumMemberName("guest")] Guest,
    [JsonStringEnumMemberName("hidden")] Hidden,
}

/// <summary>How video streams are timed (docs/decisions/0025); the session's <c>Spice.Session.VideoPacing</c>.</summary>
enum VideoPacingMode
{
    [JsonStringEnumMemberName("spacing")] Spacing,
    [JsonStringEnumMemberName("clock")] Clock,
    [JsonStringEnumMemberName("off")] Off,
}

/// <summary>
/// Reads <see cref="VideoPacingMode"/> leniently: an unknown name or a value of another type falls back to the default
/// instead of making the whole file unusable.
/// </summary>
sealed class VideoPacingModeConverter : JsonConverter<VideoPacingMode>
{
    public override VideoPacingMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) { reader.Skip(); return VideoPacingMode.Off; }
        return reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "spacing" => VideoPacingMode.Spacing,
            "clock" => VideoPacingMode.Clock,
            _ => VideoPacingMode.Off,
        };
    }

    public override void Write(Utf8JsonWriter writer, VideoPacingMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch { VideoPacingMode.Spacing => "spacing", VideoPacingMode.Clock => "clock", _ => "off" });
}

/// <summary>
/// The image compression asked of the server (docs/decisions/0028): <c>auto</c> asks for nothing, so the server decides.
/// More values may follow; an unknown one reads as <c>auto</c>.
/// </summary>
enum ImageCompressionMode
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("lz4")] Lz4,
}

/// <summary>Reads <see cref="ImageCompressionMode"/> leniently, as <see cref="VideoPacingModeConverter"/> does.</summary>
sealed class ImageCompressionModeConverter : JsonConverter<ImageCompressionMode>
{
    public override ImageCompressionMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) { reader.Skip(); return ImageCompressionMode.Auto; }
        return reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "lz4" => ImageCompressionMode.Lz4,
            _ => ImageCompressionMode.Auto,
        };
    }

    public override void Write(Utf8JsonWriter writer, ImageCompressionMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == ImageCompressionMode.Lz4 ? "lz4" : "auto");
}

/// <summary>
/// The video codec order asked of the server (docs/decisions/0037): <c>auto</c> prefers the codecs decoded in hardware
/// here (H.264, then VP9, then HEVC), <c>server</c> asks for nothing, and a codec name puts that codec first. An unknown
/// value reads as <c>auto</c>.
/// </summary>
enum VideoCodecMode
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("server")] Server,
    [JsonStringEnumMemberName("mjpeg")] Mjpeg,
    [JsonStringEnumMemberName("h264")] H264,
    [JsonStringEnumMemberName("vp9")] Vp9,
    [JsonStringEnumMemberName("h265")] H265,
}

static class VideoCodecModes
{
    /// <summary>
    /// The codec order asked of the server (docs/decisions/0037, provisional): automatic names the hardware-decoded
    /// codecs, H.264 first, so the server prefers them over its own MJPEG-first list; a chosen codec goes first, followed
    /// by the automatic order; server default asks for nothing (null). The session sends only the codecs it advertises.
    /// </summary>
    public static VideoCodecType[]? ServerOrder(this VideoCodecMode mode) => mode switch
    {
        VideoCodecMode.Server => null,
        VideoCodecMode.Mjpeg => [VideoCodecType.Mjpeg],
        VideoCodecMode.H264 => [VideoCodecType.H264, VideoCodecType.Vp9, VideoCodecType.H265],
        VideoCodecMode.Vp9 => [VideoCodecType.Vp9, VideoCodecType.H264, VideoCodecType.H265],
        VideoCodecMode.H265 => [VideoCodecType.H265, VideoCodecType.H264, VideoCodecType.Vp9],
        _ => [VideoCodecType.H264, VideoCodecType.Vp9, VideoCodecType.H265],
    };
}

/// <summary>Reads <see cref="VideoCodecMode"/> leniently, as <see cref="VideoPacingModeConverter"/> does.</summary>
sealed class VideoCodecModeConverter : JsonConverter<VideoCodecMode>
{
    public override VideoCodecMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) { reader.Skip(); return VideoCodecMode.Auto; }
        return reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "server" => VideoCodecMode.Server,
            "mjpeg" => VideoCodecMode.Mjpeg,
            "h264" => VideoCodecMode.H264,
            "vp9" => VideoCodecMode.Vp9,
            "h265" or "hevc" => VideoCodecMode.H265,
            _ => VideoCodecMode.Auto,
        };
    }

    public override void Write(Utf8JsonWriter writer, VideoCodecMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            VideoCodecMode.Server => "server",
            VideoCodecMode.Mjpeg => "mjpeg",
            VideoCodecMode.H264 => "h264",
            VideoCodecMode.Vp9 => "vp9",
            VideoCodecMode.H265 => "h265",
            _ => "auto",
        });
}

/// <summary>
/// Reads an off-by-default switch leniently: <c>true</c> (or the string "true") is on; anything else, including a value
/// of another type, is off instead of making the whole file unusable.
/// </summary>
sealed class OffByDefaultConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.String: return string.Equals(reader.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            default: reader.Skip(); return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

/// <summary>
/// Reads an on-by-default switch leniently: <c>false</c> (or the string "false") is off; anything else, including a
/// value of another type, is on instead of making the whole file unusable. A missing key keeps the property's default.
/// </summary>
sealed class OnByDefaultConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.False: return false;
            case JsonTokenType.String: return !string.Equals(reader.GetString()?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            default: reader.Skip(); return true;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

/// <summary>settings.json: global preferences and the remembered window of each VM. See `docs/decisions/`.</summary>
sealed class ViewerSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public ServerCursorMode ServerCursor { get; set; } = ServerCursorMode.Guest;
    /// <summary>Scaling for a VM without a remembered entry.</summary>
    public ScalingMode Scaling { get; set; } = ScalingMode.Fit;
    public ShortcutMode SystemShortcuts { get; set; } = ShortcutMode.Always;
    public bool ResizeGuest { get; set; } = true;
    [JsonConverter(typeof(VideoPacingModeConverter))]
    public VideoPacingMode VideoPacing { get; set; } = VideoPacingMode.Off;
    /// <summary>Fast JPEG decoding, slightly lower quality (docs/decisions/0028). Off keeps the accurate decoder.</summary>
    [JsonConverter(typeof(OffByDefaultConverter))]
    public bool FastJpeg { get; set; }
    [JsonConverter(typeof(ImageCompressionModeConverter))]
    public ImageCompressionMode ImageCompression { get; set; } = ImageCompressionMode.Auto;
    /// <summary>Video codec order asked of the server (docs/decisions/0037).</summary>
    [JsonConverter(typeof(VideoCodecModeConverter))]
    public VideoCodecMode VideoCodec { get; set; } = VideoCodecMode.Auto;
    /// <summary>
    /// Decode the H.264 streams no hardware decoder takes (spice-server's 4:4:4) in software (docs/decisions/0037,
    /// amended 2026-10-31). On by default; off spends bandwidth (MJPEG) instead of CPU.
    /// </summary>
    [JsonConverter(typeof(OnByDefaultConverter))]
    public bool SoftwareVideo { get; set; } = true;
    /// <summary>
    /// Trim Linux guest padding: infer a Linux guest's monitor from its full-frame draws when the server names the whole
    /// padded surface (docs/decisions/0035, finding L16). On by default; a file without the key reads as on.
    /// </summary>
    [JsonConverter(typeof(OnByDefaultConverter))]
    public bool TrimGuestPadding { get; set; } = true;
    /// <summary>
    /// The status overlay (docs/decisions/0044): connection, display, video, traffic, audio and memory figures over the
    /// guest picture. Off by default; saved for all VMs.
    /// </summary>
    [JsonConverter(typeof(OffByDefaultConverter))]
    public bool StatusOverlay { get; set; }
    /// <summary>Keyed by <see cref="SettingsStore.VmKey"/>.</summary>
    public Dictionary<string, VmWindowState> Vms { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A VM's window: the normal (restored) rectangle in screen pixels at <see cref="Dpi"/>, and its modes.</summary>
sealed class VmWindowState
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public int Dpi { get; set; } = 96;
    public bool Maximized { get; set; }
    public bool Fullscreen { get; set; }
    public ScalingMode Scaling { get; set; } = ScalingMode.Fit;
    public DateTime LastUsed { get; set; }

    /// <summary>The rectangle for a monitor at <paramref name="dpi"/>: the size scales with the DPI, the top-left stays.</summary>
    public (int Left, int Top, int Right, int Bottom) RectForDpi(int dpi)
    {
        if (dpi <= 0 || Dpi <= 0 || dpi == Dpi) return (Left, Top, Right, Bottom);
        int w = (int)Math.Round((double)(Right - Left) * dpi / Dpi), h = (int)Math.Round((double)(Bottom - Top) * dpi / Dpi);
        return (Left, Top, Left + w, Top + h);
    }

    internal bool IsValid =>
        Right > Left && Bottom > Top && Right - Left <= MaxSide && Bottom - Top <= MaxSide
        && Math.Abs((long)Left) <= MaxCoord && Math.Abs((long)Top) <= MaxCoord && Dpi is >= 48 and <= 960;

    const int MaxSide = 32768, MaxCoord = 1 << 20;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    WriteIndented = true, UseStringEnumConverter = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ViewerSettings))]
sealed partial class SettingsJsonContext : JsonSerializerContext;

enum SettingsLoadResult { Missing, Loaded, Invalid }

/// <summary>
/// settings.json in one directory. Read once at startup; written only on events (window close, a menu change), never
/// per message. Each write re-reads the file and applies just its own change, so viewers of other VMs running at the
/// same time don't lose each other's entries; a named mutex per settings file makes that read-modify-write exclusive
/// across processes, and the file is replaced atomically. A file that can't be read is left alone
/// until the next successful write, which first renames it to settings.json.bad. Nothing here throws to the caller.
/// </summary>
sealed class SettingsStore(string directory)
{
    public const string FileName = "settings.json";
    public const int MaxFileBytes = 1 << 20;
    public const int MaxVms = 200;
    public const int MaxKeyChars = 512;

    readonly object _gate = new();
    Mutex? _fileLock;

    // The attribute's options, plus readable titles: only a local file, never embedded in HTML.
    static readonly SettingsJsonContext Json = new(new JsonSerializerOptions(SettingsJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public string Directory { get; } = directory;
    public string FilePath => Path.Combine(Directory, FileName);
    /// <summary>The settings as last loaded or written; defaults when there is no usable file.</summary>
    public ViewerSettings Current { get; private set; } = new();

    /// <summary>
    /// The key a VM's window is remembered under: proxy host (or host without a proxy) and the .vv title. Ports and
    /// tickets change every session and a proxied host is an opaque token, so without a title nothing is remembered.
    /// </summary>
    public static string? VmKey(string host, string? proxyHost, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var h = (string.IsNullOrWhiteSpace(proxyHost) ? host : proxyHost).Trim().ToLowerInvariant();
        var key = $"{h}|{title.Trim()}";
        return key.Length <= MaxKeyChars ? key : null;
    }

    public SettingsLoadResult Load()
    {
        lock (_gate)
        {
            var result = ReadFile(out var s, out var problem);
            if (result == SettingsLoadResult.Invalid) Log.Warn($"settings: {FilePath} can't be used ({problem}); using defaults");
            Current = s ?? new ViewerSettings();
            return result;
        }
    }

    /// <summary>Applies <paramref name="change"/> to the file's current contents and writes it. Returns false (logged) on failure.</summary>
    public bool Update(Action<ViewerSettings> change)
    {
        lock (_gate)
        {
            string path = FilePath, tmp = $"{path}.{Environment.ProcessId}.tmp";
            bool locked = false;
            try
            {
                locked = LockFile();
                if (!locked) { Log.Warn($"settings: {path} stayed locked by another viewer; not saved"); return false; }
                System.IO.Directory.CreateDirectory(Directory);
                var result = ReadFile(out var onDisk, out var problem);
                var s = onDisk ?? Current;
                change(s);
                Prune(s.Vms, MaxVms);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
                    fs.Write(Serialize(s));
                if (result == SettingsLoadResult.Invalid)
                {
                    File.Move(path, path + ".bad", overwrite: true);
                    Log.Warn($"settings: kept the unusable settings file as {path}.bad ({problem})");
                }
                File.Move(tmp, path, overwrite: true);
                Current = s;
                return true;
            }
            catch (Exception e)
            {
                Log.Warn($"settings: could not save {path}: {e.Message}");
                try { File.Delete(tmp); } catch (Exception) { }
                return false;
            }
            finally { if (locked) _fileLock!.ReleaseMutex(); }
        }
    }

    /// <summary>
    /// Takes the cross-process lock for this settings file (caller holds _gate, on the thread that releases it).
    /// Named after the file's full path, so portable copies in different folders don't contend.
    /// </summary>
    bool LockFile()
    {
        if (_fileLock is null)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(FilePath).ToUpperInvariant()));
            _fileLock = new Mutex(false, $@"Local\SpiceViewer.settings.{Convert.ToHexString(hash, 0, 16)}");
        }
        try { return _fileLock.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { return true; } // a viewer died mid-write; the file is replaced atomically, so it's intact
    }

    SettingsLoadResult ReadFile(out ViewerSettings? settings, out string? problem)
    {
        settings = null;
        problem = null;
        byte[] data;
        try
        {
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            data = new byte[Math.Min(fs.Length, MaxFileBytes + 1)];
            int total = 0, n;
            while (total < data.Length && (n = fs.Read(data, total, data.Length - total)) > 0) total += n;
            if (total > MaxFileBytes) { problem = "larger than 1 MiB"; return SettingsLoadResult.Invalid; }
            Array.Resize(ref data, total);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return SettingsLoadResult.Missing; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not corrupt: never rename a file we merely couldn't open.
            Log.Warn($"settings: can't read {FilePath}: {e.Message}");
            return SettingsLoadResult.Missing;
        }
        settings = Parse(data, out problem);
        return settings is null ? SettingsLoadResult.Invalid : SettingsLoadResult.Loaded;
    }

    /// <summary>Parses and sanitizes settings.json; null (with a reason) when it is not a version-1 settings file.</summary>
    public static ViewerSettings? Parse(ReadOnlySpan<byte> utf8, out string? problem)
    {
        problem = null;
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) utf8 = utf8[3..];
        ViewerSettings? s;
        try { s = JsonSerializer.Deserialize(utf8, Json.ViewerSettings); }
        catch (JsonException e) { problem = e.Message; return null; }
        if (s is null) { problem = "empty"; return null; }
        if (s.Version != ViewerSettings.CurrentVersion) { problem = $"version {s.Version}, expected {ViewerSettings.CurrentVersion}"; return null; }
        Sanitize(s);
        return s;
    }

    public static byte[] Serialize(ViewerSettings s) => JsonSerializer.SerializeToUtf8Bytes(s, Json.ViewerSettings);

    /// <summary>Hand edits and integer enum values can be out of range: fall back per field, drop unusable entries.</summary>
    static void Sanitize(ViewerSettings s)
    {
        if (!Enum.IsDefined(s.ServerCursor)) s.ServerCursor = ServerCursorMode.Guest;
        if (!Enum.IsDefined(s.Scaling)) s.Scaling = ScalingMode.Fit;
        if (!Enum.IsDefined(s.SystemShortcuts)) s.SystemShortcuts = ShortcutMode.Always;
        if (!Enum.IsDefined(s.VideoPacing)) s.VideoPacing = VideoPacingMode.Off;
        if (!Enum.IsDefined(s.ImageCompression)) s.ImageCompression = ImageCompressionMode.Auto;
        if (!Enum.IsDefined(s.VideoCodec)) s.VideoCodec = VideoCodecMode.Auto;
        var vms = new Dictionary<string, VmWindowState>(StringComparer.Ordinal);
        if (s.Vms is not null)
            foreach (var (key, vm) in s.Vms)
            {
                if (vm is null || key.Length == 0 || key.Length > MaxKeyChars || !vm.IsValid) continue;
                if (!Enum.IsDefined(vm.Scaling)) vm.Scaling = s.Scaling;
                vms[key] = vm;
            }
        s.Vms = vms;
        Prune(s.Vms, MaxVms);
    }

    /// <summary>Keeps the <paramref name="max"/> most recently used entries.</summary>
    public static void Prune(Dictionary<string, VmWindowState> vms, int max)
    {
        if (vms.Count <= max) return;
        foreach (var key in vms.OrderBy(kv => kv.Value.LastUsed).Take(vms.Count - max).Select(kv => kv.Key).ToArray())
            vms.Remove(key);
    }
}
