using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace Via;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    // ── Logging ──────────────────────────────────────────
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VIA x4", "logs");
    private static readonly string LogFile = Path.Combine(LogDir, "via.log");
    private static readonly object _logLock = new();

    private static void Log(string message)
    {
        try
        {
            if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n";
            lock (_logLock) File.AppendAllText(LogFile, line);
        }
        catch { }
    }

    private static void LogError(string context, Exception ex)
    {
        Log($"ERROR [{context}] {ex.GetType().Name}: {ex.Message}");
    }

    private static void CleanupOldLogs()
    {
        try
        {
            if (!File.Exists(LogFile)) return;
            var info = new FileInfo(LogFile);
            if (info.Length > 2 * 1024 * 1024) // 2 MB max
            {
                var lines = File.ReadAllLines(LogFile);
                File.WriteAllLines(LogFile, lines.Skip(lines.Length / 2)); // keep recent half
            }
        }
        catch { }
    }

    private volatile bool isClosing;
    private string? crocPath;
    private string _cachedLocalIp = "Unknown";
    private readonly List<string> selectedFiles = new();
    private string? saveFolder;
    private Process? sendProc;
    private Process? recvProc;
    private string? currentCode;
    private bool isSending;
    private System.Windows.Threading.DispatcherTimer? connectTimeoutTimer;
    private bool sendConnected;
    private bool recvConnected;
    private bool isReceiving;
    private readonly List<string> sendErrors = new();
    private readonly List<string> recvErrors = new();
    private long totalFileSize;
    private double lastSendPercent;
    private double lastRecvPercent;
    private readonly List<string> lastSendFiles = new();
    private string? lastRecvCode;
    private string? tempZipPath;
    private string? lastRecvFileName;
    private readonly List<string> lastRecvLines = new();
    private readonly List<string> lastSendLines = new();
    private System.Windows.Threading.DispatcherTimer? waitingDotsTimer;
    private int waitingDots;
    private int sendRetryCount;
    private int recvRetryCount;
    private int recvTimeoutRetryCount; // separate counter for connection-timeout retries on receiver
    private const int MaxAutoRetries = 3;      // sender retries (sender controls the code, fewer needed)
    private const int MaxRecvAutoRetries = 5;   // receiver retries (receiver can't regenerate the code, be more patient)
    private const int MaxRecvTimeoutRetries = 2; // how many times receiver retries after a 60s connection timeout
    private const int MaxRecvTextDisplayLength = 50_000; // truncate received text display at 50K chars
    private string? _fullReceivedText; // holds full text when display is truncated
    private CancellationTokenSource? sendRetryCts;
    private CancellationTokenSource? recvRetryCts;
    private string? retryRelayOverride; // set to fallback relay when primary retries exhaust
    private System.Windows.Threading.DispatcherTimer? sendResetTimer;
    private System.Windows.Threading.DispatcherTimer? recvResetTimer;
    private System.Windows.Threading.DispatcherTimer? _recvPollTimer;
    private int  _sendConPtyPid;
    private int  _recvConPtyPid;
    // ConPTY causes STATUS_DLL_INIT_FAILED in Go's runtime on many Windows builds; keep disabled.
    private bool _conPtyDisabled = true;
    private long _recvPollPrevBytes;
    private DateTime _recvPollPrevTime;
    private static readonly Regex ProgressRegex = new(@"(\d+\.?\d*)%\s*\|[^|]*\|\s*\(?[^,)]*,\s*(\d+\.?\d*)\s*(B|kB|MB|GB)/s", RegexOptions.Compiled);

    // ── Transfer timing ──────────────────────────────────
    private DateTime sendStartTime;
    private DateTime recvStartTime;
    private System.Windows.Threading.DispatcherTimer? elapsedTimer;
    private bool isSendElapsed;

    private bool _settingsLoading;
    private bool _sendConnTimedOut;
    private bool _recvConnTimedOut;

    // ── Direct P2P ────────────────────────────────────────
    private bool _isDirectMode;
    private DirectP2pHelper? _directP2pHelper;
    private string? _shareCode; // the full code to share (d:BASE64:croccode format)
    private string? _currentRelayOverride; // overrides retryRelayOverride + appConfig.RelayServer
    private CancellationTokenSource? _directSetupCts;
    private bool _sendReceiverSeen; // true once receiver connects (we see "Sending" in output)
    private bool _pendingRecvTimeoutRetry; // true when a timeout retry is scheduled — prevents OnRecvDone from clearing state
    private long _directP2pLatencyMs = -1; // RTT to sender relay, measured on receiver side
    private string? _originalRecvCode; // stores the full "d:xxx:code" for manual resume (lastRecvCode only has the stripped croc code)
    private bool _directP2pResumeCodeChanged; // true when sender resumes Direct P2P and needs to share new code
    private System.Windows.Threading.DispatcherTimer? _clipboardClearTimer;
    private bool isTextMode;
    private bool isZipping;
    private CancellationTokenSource? zipCts;
    private int zipGeneration;
    private int recvGeneration;
    private bool isReceivingText;
    private string? receivedText;
    private string? _lastSendText; // stored for text send retry
    private long totalRecvBytes; // parsed from croc's "Receiving 'file' (X MB)" line

    private const string CurrentVersion = "2.33.1";

    // ── Smart compression: skip compression for already-compressed formats ──
    private static readonly HashSet<string> IncompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".vob",
        // Audio
        ".mp3", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".wav",
        // Images
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".bmp", ".tiff",
        // Archives
        ".zip", ".rar", ".7z", ".gz", ".xz", ".zst", ".bz2", ".tar", ".cab", ".lz4", ".br",
        // Executables & binaries
        ".exe", ".dll", ".so", ".dylib",
        // Disk images
        ".iso", ".img", ".dmg", ".wim",
        // Game / 3D assets (typically pre-compressed)
        ".pak", ".vpk", ".unity3d", ".unitypackage",
        // Documents (already zip-based internally)
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub",
        // Database
        ".sqlite", ".db",
    };

    private static CompressionLevel GetCompressionLevel(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return IncompressibleExtensions.Contains(ext)
            ? CompressionLevel.NoCompression
            : CompressionLevel.Fastest;
    }

    // ── Transfer History ──────────────────────────────────

    private class TransferRecord
    {
        public string FileName { get; set; } = "";
        public string Direction { get; set; } = "";
        public string Date { get; set; } = "";
        public string Size { get; set; } = "";
        public string Status { get; set; } = "";
        public string FilePath { get; set; } = ""; // full path for received files (enables open-from-history)
    }

    private class AppConfig
    {
        public string SaveFolder { get; set; } = "";
        public List<TransferRecord> History { get; set; } = new();
        public string LastSendCode { get; set; } = "";
        public List<string> LastSendFilePaths { get; set; } = new();
        // Settings
        public string ThrottleUpload { get; set; } = "";
        public string RelayServer { get; set; } = "";
        public bool NoCompress { get; set; } = false;
        public bool ScanReceivedFiles { get; set; } = true;
        public string Socks5Proxy { get; set; } = "";
        public string HashAlgorithm { get; set; } = "xxhash";
        public string EncryptionCurve { get; set; } = "p256";
        public string FallbackRelayServer { get; set; } = "";
        public bool ForceLocal { get; set; } = false;
        public bool OverwriteExisting { get; set; } = true;
        public string Passphrase { get; set; } = "";
        public bool CleanOnExit { get; set; } = false;
    }

    // ── Croc flags from settings ───────────────────────────

    // Adds croc global flags to an ArgumentList — each flag/value is a separate entry,
    // which prevents any relay/proxy value from injecting additional CLI arguments.
    // Adds croc GLOBAL flags (must appear before the subcommand: send / receive code)
    private void AddCrocGlobalFlags(IList<string> args)
    {
        // _currentRelayOverride wins over retry/settings relay (used for Direct P2P mode)
        var relay = _currentRelayOverride ?? retryRelayOverride ?? appConfig.RelayServer;
        if (!string.IsNullOrEmpty(relay)) { args.Add("--relay"); args.Add(relay); }
        if (!string.IsNullOrEmpty(appConfig.ThrottleUpload)) { args.Add("--throttleUpload"); args.Add(appConfig.ThrottleUpload); }
        if (appConfig.NoCompress) args.Add("--no-compress");
        if (!string.IsNullOrEmpty(appConfig.Socks5Proxy)) { args.Add("--socks5"); args.Add(appConfig.Socks5Proxy); }
        if (appConfig.EncryptionCurve != "p256" && !string.IsNullOrEmpty(appConfig.EncryptionCurve)) { args.Add("--curve"); args.Add(appConfig.EncryptionCurve); }
        if (!string.IsNullOrEmpty(appConfig.Passphrase)) { args.Add("--pass"); args.Add(appConfig.Passphrase); }
        if (appConfig.ForceLocal) args.Add("--local");
    }

    // Adds croc SEND subcommand flags (must appear after "send")
    private void AddCrocSendFlags(IList<string> args)
    {
        if (appConfig.HashAlgorithm != "xxhash" && !string.IsNullOrEmpty(appConfig.HashAlgorithm)) { args.Add("--hash"); args.Add(appConfig.HashAlgorithm); }
    }

    // Converts a croc size string like "12.3 MB" or "500 kB" to bytes.
    private static long ParseSizeBytes(string sizeStr)
    {
        var parts = sizeStr.Trim().Split(' ', 2);
        if (parts.Length < 2) return 0;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var value)) return 0;
        return parts[1].ToUpperInvariant() switch
        {
            "B"              => (long)value,
            "KB" or "KIB"    => (long)(value * 1024),
            "MB" or "MIB"    => (long)(value * 1024 * 1024),
            "GB" or "GIB"    => (long)(value * 1024L * 1024 * 1024),
            "TB" or "TIB"    => (long)(value * 1024L * 1024 * 1024 * 1024),
            _                => 0
        };
    }

    // ── Settings persistence ──────────────────────────────
    private static string GetDefaultSaveFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VIA x4", "config.json");

    private AppConfig appConfig = new();

    private Storyboard? dropIconPulse;
    private Storyboard? borderRotateAnim;

    public MainWindow()
    {
        InitializeComponent();
        versionText.Text = $"VIA x4 v{CurrentVersion}";
        aboutVersion.Text = $"Version {CurrentVersion}";
        CleanupOldLogs();
        Log("VIA x4 starting up");
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        // Resolve local IP off-thread at startup so ShowConnInfo never blocks the UI
        _ = Task.Run(() => { _cachedLocalIp = ResolveLocalIp(); });
        CleanupStaleTempFiles();
        KillOrphanEngineProcesses();
        // Remove stale firewall rule and kill any zombie relay from a previous crashed session
        _ = Task.Run(DirectP2pHelper.RemoveFirewallRule);
        _ = DirectP2pHelper.KillZombieRelayAsync();
        ExtractCroc();
        LoadSettings();
        Closing += MainWindow_Closing;

        dropIconPulse = (Storyboard)FindResource("DropIconPulse");
        dropIconPulse.Begin(this, true);
        borderRotateAnim = (Storyboard)FindResource("BorderRotateAnimation");
        borderRotateAnim.Begin(this, true);
        dropZone.IsVisibleChanged += (_, _) =>
        {
            if (dropZone.Visibility == Visibility.Visible)
            {
                dropIconPulse.Resume(this);
                borderRotateAnim?.Resume(this);
            }
            else
            {
                dropIconPulse.Pause(this);
                borderRotateAnim?.Pause(this);
            }
        };

        // Check for unfinished transfer from a previous session
        CheckUnfinishedTransfer();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg != null)
                {
                    appConfig = cfg;
                    if (!string.IsNullOrEmpty(cfg.SaveFolder) && Directory.Exists(cfg.SaveFolder))
                    {
                        saveFolder = cfg.SaveFolder;
                        saveFolderBox.Text = saveFolder;
                        return;
                    }
                }
            }
        }
        catch (Exception ex) { LogError("LoadSettings", ex); }
        saveFolder = GetDefaultSaveFolder();
        saveFolderBox.Text = saveFolder;
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            appConfig.SaveFolder = saveFolder ?? "";
            var json = JsonSerializer.Serialize(appConfig, new JsonSerializerOptions { WriteIndented = true });
            // Atomic write: File.Replace uses ReplaceFile API which is safer than delete+rename
            var tempFile = ConfigPath + ".tmp";
            File.WriteAllText(tempFile, json);
            if (File.Exists(ConfigPath))
            {
                var backupFile = ConfigPath + ".bak";
                File.Replace(tempFile, ConfigPath, backupFile);
                try { File.Delete(backupFile); } catch { }
            }
            else
            {
                File.Move(tempFile, ConfigPath);
            }
        }
        catch (Exception ex) { LogError("SaveSettings", ex); }
    }

    private void AddHistoryRecord(string fileName, string direction, string size, string status, string filePath = "")
    {
        appConfig.History.Insert(0, new TransferRecord
        {
            FileName = fileName,
            Direction = direction,
            Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Size = size,
            Status = status,
            FilePath = filePath
        });
        if (appConfig.History.Count > 50)
            appConfig.History.RemoveRange(50, appConfig.History.Count - 50);
        SaveSettings();
    }

    // ── Stale temp cleanup ────────────────────────────────

    private static void CleanupStaleTempFiles()
    {
        try
        {
            var tempDir = Path.GetTempPath();
            // Migrate legacy engine files that older versions left in %TEMP%
            foreach (var legacy in new[] { "via_engine.exe", "via_croc.exe" }
                         .Select(n => Path.Combine(tempDir, n)))
                try { if (File.Exists(legacy)) File.Delete(legacy); } catch { }
            foreach (var f in Directory.EnumerateFiles(tempDir, "via_croc_*.exe"))
                try { File.Delete(f); } catch { }
            foreach (var f in Directory.EnumerateFiles(tempDir, "via_*.zip"))
                try { File.Delete(f); } catch { }
        }
        catch { }

        // Clean legacy persistent extraction from older versions
        try
        {
            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VIA x4", "VIA.exe");
            if (File.Exists(legacyPath))
                File.Delete(legacyPath);
        }
        catch { }
    }

    // Kill orphan croc processes from a previous session that crashed without cleanup
    private static void KillOrphanEngineProcesses()
    {
        try
        {
            var enginePath = EnginePath;
            foreach (var proc in Process.GetProcessesByName("croc"))
            {
                try
                {
                    if (proc.MainModule?.FileName?.Equals(enginePath, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        proc.Kill();
                        Log($"Killed orphan croc process (PID {proc.Id})");
                    }
                }
                catch { } // access denied or already exited
                finally { proc.Dispose(); }
            }
        }
        catch { }
    }

    // ── Croc Extraction ─────────────────────────────────

    private const string ExpectedEngineHash = "93c0bb8c2919ecb4b815ba66ba9d7e90848f5f2d49bab20d6e0587c44bcac15e";

    // Stable per-user install path — LocalAppData is where legitimate bundled tools live.
    // Keeping the engine here (not %TEMP%) preserves Windows Firewall rules across launches
    // and avoids the #1 AV heuristic trigger: executing a binary dropped into %TEMP%.
    private static string EnginePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VIA x4", "engine", "croc.exe");

    private static string ComputeFileHash(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Extracts the embedded croc engine to a stable per-user location under LocalAppData.
    /// The engine is kept between launches and only re-extracted when its hash changes,
    /// matching the behaviour of legitimate bundled-tool applications.
    /// </summary>
    private void ExtractCroc()
    {
        try
        {
            var tempPath = EnginePath;
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("croc.exe", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                ShowInfoBar("Fatal Error", "Transfer engine not found in resources.", InfoBarSeverity.Error, 60000);
                return;
            }

            // Check if already extracted and valid (fast path for restart scenarios)
            bool needsExtract = !File.Exists(tempPath);
            if (!needsExtract)
            {
                var existingHash = ComputeFileHash(tempPath);
                if (existingHash != ExpectedEngineHash)
                {
                    Log($"Engine hash mismatch: expected={ExpectedEngineHash}, got={existingHash}. Re-extracting.");
                    needsExtract = true;
                }
            }

            if (needsExtract)
            {
                // Extract to a temp file first, verify hash, then atomically move into place.
                // This prevents a half-written binary from being used if the process is interrupted.
                var stagingPath = tempPath + ".tmp";
                Exception? lastEx = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        using var stream = asm.GetManifestResourceStream(resourceName)!;
                        using var fs = File.Create(stagingPath);
                        stream.CopyTo(fs);
                        lastEx = null;
                        break;
                    }
                    catch (IOException ex)
                    {
                        lastEx = ex;
                        Log($"ExtractCroc attempt {attempt + 1} failed: {ex.Message}");
                        Thread.Sleep(1000);
                    }
                }
                if (lastEx != null) throw lastEx;

                // Verify BEFORE moving to final path
                var newHash = ComputeFileHash(stagingPath);
                if (newHash != ExpectedEngineHash)
                {
                    Log($"CRITICAL: Extracted engine hash mismatch: {newHash}");
                    ShowInfoBar("Integrity Error", "Transfer engine failed integrity check. The application may be corrupted.", InfoBarSeverity.Error, 60000);
                    try { File.Delete(stagingPath); } catch { }
                    return;
                }

                // Atomic move — replaces any existing corrupt file
                File.Move(stagingPath, tempPath, overwrite: true);
            }

            crocPath = tempPath;
            Log("Transfer engine ready");
        }
        catch (Exception ex)
        {
            LogError("ExtractCroc", ex);
            ShowInfoBar("Startup Error", $"Failed to extract transfer engine: {ex.Message}. Try running as administrator or check antivirus.", InfoBarSeverity.Error, 60000);
        }
    }

    private System.Windows.Threading.DispatcherTimer? infoBarTimer;

    private void ShowInfoBar(string title, string message, InfoBarSeverity severity, int autoDismissMs = 5000)
    {
        infoBarTimer?.Stop();
        infoBar.Title = title;
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;

        infoBarTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(autoDismissMs)
        };
        infoBarTimer.Tick += (_, _) => { infoBar.IsOpen = false; infoBarTimer.Stop(); };
        infoBarTimer.Start();
    }

    // ── Waiting dots animation ──────────────────────────

    private void StartWaitingDots(System.Windows.Controls.TextBlock target, string baseText)
    {
        StopWaitingDots();
        waitingDots = 0;
        target.Text = baseText;
        waitingDotsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        waitingDotsTimer.Tick += (_, _) =>
        {
            waitingDots = (waitingDots + 1) % 4;
            target.Text = baseText + new string('.', waitingDots);
        };
        waitingDotsTimer.Start();
    }

    private void StopWaitingDots()
    {
        waitingDotsTimer?.Stop();
        waitingDotsTimer = null;
    }

    // ── Elapsed timer ─────────────────────────────────────

    private void StartElapsedTimer(bool forSend)
    {
        StopElapsedTimer();
        isSendElapsed = forSend;
        if (forSend) sendStartTime = DateTime.Now;
        else recvStartTime = DateTime.Now;

        elapsedTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        elapsedTimer.Tick += (_, _) => UpdateElapsedDisplay();
        elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        elapsedTimer?.Stop();
        elapsedTimer = null;
    }

    // ── Receive file-size poll ────────────────────────────

    private void StartRecvPoll()
    {
        _recvPollPrevBytes = 0;
        _recvPollPrevTime = DateTime.Now;
        _recvPollTimer?.Stop();
        _recvPollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _recvPollTimer.Tick += (_, _) => UpdateRecvPoll();
        _recvPollTimer.Start();
    }

    private void StopRecvPoll()
    {
        _recvPollTimer?.Stop();
        _recvPollTimer = null;
    }

    private void UpdateRecvPoll()
    {
        if (lastRecvFileName == null || totalRecvBytes <= 0) return;
        var folder = saveFolder ?? GetDefaultSaveFolder();
        var filePath = Path.Combine(folder, lastRecvFileName);
        long current = 0;
        try
        {
            // croc v10 writes directly to the final filename during transfer
            if (File.Exists(filePath))
                current = new FileInfo(filePath).Length;
        }
        catch { return; }
        if (current <= 0) return;

        var now = DateTime.Now;
        var dt = (now - _recvPollPrevTime).TotalSeconds;
        double speed = dt > 0.1 ? (current - _recvPollPrevBytes) / dt : 0;
        _recvPollPrevBytes = current;
        _recvPollPrevTime = now;

        var pct = Math.Min(100.0, current * 100.0 / totalRecvBytes);
        lastRecvPercent = pct;
        recvProgress.IsIndeterminate = false;
        AnimateProgress(recvProgress, pct);
        recvProgressDetail.Text = $"{FormatSize(current)} / {FormatSize(totalRecvBytes)}";
        if (speed > 0)
        {
            recvSpeedText.Text = $"\u2193  {FormatSize((long)speed)}/s";
            recvSpeedRow.Visibility = Visibility.Visible;
        }
        recvEtaText.Text = FormatEta(pct, recvStartTime);
        SetTaskbarProgress(pct);
    }

    // ── ConPTY — Windows pseudo-console so croc emits live progress ───────────────────────────────

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEXW
    {
        [FieldOffset(  0)] public int    cb;
        [FieldOffset(  8)] public IntPtr lpReserved;
        [FieldOffset( 16)] public IntPtr lpDesktop;
        [FieldOffset( 24)] public IntPtr lpTitle;
        [FieldOffset( 32)] public int    dwX;
        [FieldOffset( 36)] public int    dwY;
        [FieldOffset( 40)] public int    dwXSize;
        [FieldOffset( 44)] public int    dwYSize;
        [FieldOffset( 48)] public int    dwXCountChars;
        [FieldOffset( 52)] public int    dwYCountChars;
        [FieldOffset( 56)] public int    dwFillAttribute;
        [FieldOffset( 60)] public int    dwFlags;
        [FieldOffset( 64)] public short  wShowWindow;
        [FieldOffset( 66)] public short  cbReserved2;
        [FieldOffset( 72)] public IntPtr lpReserved2;
        [FieldOffset( 80)] public IntPtr hStdInput;
        [FieldOffset( 88)] public IntPtr hStdOutput;
        [FieldOffset( 96)] public IntPtr hStdError;
        [FieldOffset(104)] public IntPtr lpAttributeList;
    }
    [StructLayout(LayoutKind.Sequential)] private struct COORD16   { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CPTY_PROC { public IntPtr hProcess, hThread; public int pid, tid; }

    [DllImport("kernel32", SetLastError = true)] private static extern int  CreatePseudoConsole(COORD16 size, IntPtr hIn, IntPtr hOut, uint f, out IntPtr hPty);
    [DllImport("kernel32", SetLastError = true)] private static extern void ClosePseudoConsole(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] private static extern bool CreatePipe(out IntPtr r, out IntPtr w, IntPtr sec, uint sz);
    [DllImport("kernel32", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, ref IntPtr val, IntPtr cbSize, IntPtr prev, IntPtr ret);
    [DllImport("kernel32", SetLastError = true)] private static extern bool DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, [In] ref STARTUPINFOEXW si, out CPTY_PROC pi);
    [DllImport("kernel32")] private static extern uint SetErrorMode(uint mode);
    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
    [DllImport("user32")] private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Regex AnsiRx = new(@"\x1b(?:\[[0-9;?]*[A-Za-z]|[()][0-9A-Za-z]|[=>M78])", RegexOptions.Compiled);

    private void KillSendProc()
    {
        if (_sendConPtyPid != 0) { try { Process.GetProcessById(_sendConPtyPid).Kill(true); } catch { } _sendConPtyPid = 0; }
        try { sendProc?.Kill(true); } catch { }
    }

    private void KillRecvProc()
    {
        if (_recvConPtyPid != 0) { try { Process.GetProcessById(_recvConPtyPid).Kill(true); } catch { } _recvConPtyPid = 0; }
        try { recvProc?.Kill(true); } catch { }
    }

    // SEM_FAILCRITICALERRORS: child process inherits this flag and suppresses hard-error dialogs
    // (e.g., 0xC0000142 STATUS_DLL_INIT_FAILED shown when Go runtime crashes under ConPTY).
    private const uint SEM_FAILCRITICALERRORS = 0x0001u;

    // Implements Windows CommandLineToArgvW quoting rules:
    // backslashes are literal unless immediately before a closing '"', where they must be doubled.
    private static string WinQuoteArg(string arg)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('"');
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; }
            else if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); slashes = 0; continue; }
            else { slashes = 0; }
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private bool TryStartWithConPty(ProcessStartInfo psi, string? workDir,
        Action<string> onLine, List<string> errList, List<string> lineBuffer,
        Action<int> onExit, out int outPid)
    {
        outPid = 0;
        if (_conPtyDisabled) return false;
        IntPtr hPipeIn = IntPtr.Zero, hWriteIn  = IntPtr.Zero;
        IntPtr hReadOut = IntPtr.Zero, hPipeOut = IntPtr.Zero;
        IntPtr hPty = IntPtr.Zero, attrList = IntPtr.Zero;
        CPTY_PROC pi = default;
        bool started = false;
        try
        {
            if (!CreatePipe(out hPipeIn,  out hWriteIn,  IntPtr.Zero, 0)) return false;
            if (!CreatePipe(out hReadOut, out hPipeOut,  IntPtr.Zero, 0)) return false;
            if (CreatePseudoConsole(new COORD16 { X = 220, Y = 50 }, hPipeIn, hPipeOut, 0, out hPty) != 0) return false;
            CloseHandle(hPipeIn);  hPipeIn  = IntPtr.Zero;
            CloseHandle(hPipeOut); hPipeOut = IntPtr.Zero;
            IntPtr attrSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            attrList = Marshal.AllocHGlobal(attrSize);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize)) return false;
            var hPtyRef = hPty;
            if (!UpdateProcThreadAttribute(attrList, 0, new IntPtr(0x00020016), ref hPtyRef,
                    new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) return false;
            var si = new STARTUPINFOEXW { cb = Marshal.SizeOf<STARTUPINFOEXW>(), lpAttributeList = attrList };
            var cmdSb = new System.Text.StringBuilder();
            cmdSb.Append(WinQuoteArg(psi.FileName));
            foreach (var a in psi.ArgumentList)
                cmdSb.Append(' ').Append(WinQuoteArg(a));
            // Suppress hard-error dialog in the child (e.g., 0xC0000142 Go/ConPTY crash);
            // child inherits SEM_FAILCRITICALERRORS unless CREATE_DEFAULT_ERROR_MODE is used.
            var prevEm = SetErrorMode(SEM_FAILCRITICALERRORS);
            bool procCreated = CreateProcess(null, cmdSb.ToString(), IntPtr.Zero, IntPtr.Zero, false,
                    0x00080000u, IntPtr.Zero, workDir, ref si, out pi);
            SetErrorMode(prevEm);
            if (!procCreated) return false;
            started = true;
            outPid  = pi.pid;
            DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); attrList = IntPtr.Zero;
            CloseHandle(pi.hThread);
            CloseHandle(hWriteIn); hWriteIn = IntPtr.Zero;
            var capRead = hReadOut; var capPty = hPty; var capProc = pi.hProcess;
            Task.Run(() =>
            {
                try
                {
                    var sfh = new Microsoft.Win32.SafeHandles.SafeFileHandle(capRead, ownsHandle: true);
                    using var fs = new FileStream(sfh, FileAccess.Read, 1024);
                    var raw = new byte[1024];
                    var lsb = new System.Text.StringBuilder();
                    while (!isClosing)
                    {
                        int n = fs.Read(raw, 0, raw.Length);
                        if (n == 0) break;
                        var txt = AnsiRx.Replace(System.Text.Encoding.UTF8.GetString(raw, 0, n), "");
                        foreach (char c in txt)
                        {
                            if (c == '\r' || c == '\n')
                            { if (lsb.Length > 0) { ProcessStderrLine(lsb.ToString(), onLine, errList, lineBuffer); lsb.Clear(); } }
                            else if (c >= 32) lsb.Append(c);
                        }
                    }
                    if (lsb.Length > 0 && !isClosing)
                        ProcessStderrLine(lsb.ToString(), onLine, errList, lineBuffer);
                }
                catch { }
                finally { ClosePseudoConsole(capPty); }
            });
            Task.Run(() =>
            {
                WaitForSingleObject(capProc, 0xFFFFFFFF);
                GetExitCodeProcess(capProc, out uint code);
                CloseHandle(capProc);
                if (!isClosing) Dispatcher.BeginInvoke(() =>
                {
                    if (isClosing) return;
                    // STATUS_DLL_INIT_FAILED: Go runtime crashed during ConPTY init.
                    // Disable ConPTY for the rest of the session so the retry uses standard Process.
                    if (code == 0xC0000142u) _conPtyDisabled = true;
                    onExit((int)code);
                });
            });
            return true;
        }
        catch
        {
            if (!started)
            {
                if (hPipeIn  != IntPtr.Zero) CloseHandle(hPipeIn);
                if (hWriteIn != IntPtr.Zero) CloseHandle(hWriteIn);
                if (hPipeOut != IntPtr.Zero) CloseHandle(hPipeOut);
                if (hReadOut != IntPtr.Zero) CloseHandle(hReadOut);
                if (hPty     != IntPtr.Zero) ClosePseudoConsole(hPty);
                if (attrList != IntPtr.Zero) { try { DeleteProcThreadAttributeList(attrList); } catch { } Marshal.FreeHGlobal(attrList); }
            }
            return false;
        }
    }

    private void UpdateElapsedDisplay()
    {
        var elapsed = DateTime.Now - (isSendElapsed ? sendStartTime : recvStartTime);
        var elapsedStr = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"m\:ss");

        var target = isSendElapsed ? sendElapsedText : recvElapsedText;
        target.Text = $"Elapsed: {elapsedStr}";
    }

    // ── Connection timeout ──────────────────────────────
    private const int ConnectTimeoutSeconds = 60;

    private void StartConnectTimeout(bool forSend)
    {
        StopConnectTimeout();
        if (forSend) { sendConnected = false; _sendConnTimedOut = false; }
        else { recvConnected = false; _recvConnTimedOut = false; }

        connectTimeoutTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(ConnectTimeoutSeconds)
        };
        connectTimeoutTimer.Tick += (_, _) =>
        {
            StopConnectTimeout();
            bool connected = forSend ? sendConnected : recvConnected;
            if (connected) return; // Connected in time

            Log($"Connection timeout after {ConnectTimeoutSeconds}s (forSend={forSend})");
            if (forSend && isSending)
            {
                _sendConnTimedOut = true;
                KillSendProc();
                // OnSendDone will handle full cleanup
                var timeoutMsg = appConfig.ForceLocal
                    ? "Could not find the other device on your local network. Make sure both devices are on the same WiFi/LAN, or disable Force Local in Settings."
                    : "Could not reach the relay server. Windows Firewall may be blocking the transfer engine — check Firewall settings, or try a different relay in Settings.";
                ShowInfoBar("Connection timed out", timeoutMsg, InfoBarSeverity.Error, 12000);
            }
            else if (!forSend && isReceiving)
            {
                // Auto-retry on receiver timeout — the relay/sender may just be slow
                if (recvTimeoutRetryCount < MaxRecvTimeoutRetries && !string.IsNullOrEmpty(lastRecvCode))
                {
                    recvTimeoutRetryCount++;
                    _pendingRecvTimeoutRetry = true; // prevent OnRecvDone from clearing _currentRelayOverride
                    KillRecvProc();
                    isReceiving = false;
                    _recvConPtyPid = 0; recvProc?.Dispose(); recvProc = null;
                    recvStatus.Text = $"Sender not responding. Reconnecting ({recvTimeoutRetryCount}/{MaxRecvTimeoutRetries})...";
                    recvStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
                    recvProgressPanel.Visibility = Visibility.Visible;
                    recvProgress.IsIndeterminate = true;
                    recvProgressDetail.Text = "";
                    recvEtaText.Text = "";
                    recvSpeedText.Text = ""; recvSpeedRow.Visibility = Visibility.Collapsed;
                    Log($"Recv timeout retry {recvTimeoutRetryCount}/{MaxRecvTimeoutRetries}");
                    recvRetryCts?.Dispose();
                    recvRetryCts = new CancellationTokenSource();
                    var tToken = recvRetryCts.Token;
                    _ = Task.Delay(3000, tToken).ContinueWith(_ =>
                    {
                        if (!tToken.IsCancellationRequested)
                            Dispatcher.BeginInvoke(() => { if (!isClosing) StartRecv(lastRecvCode!, false); });
                    }, TaskScheduler.Default);
                    return; // don't fall through to the hard-fail path
                }
                _recvConnTimedOut = true;
                KillRecvProc();
                // OnRecvDone will handle full cleanup
                var recvTimeoutMsg = appConfig.ForceLocal
                    ? "Could not find the sender on your local network. Make sure both devices are on the same WiFi/LAN, or disable Force Local in Settings."
                    : "Could not connect after multiple attempts. Check the code is correct and that the sender is still online.";
                ShowInfoBar("Connection timed out", recvTimeoutMsg, InfoBarSeverity.Error, 12000);
            }
        };
        connectTimeoutTimer.Start();
    }

    private void StopConnectTimeout()
    {
        connectTimeoutTimer?.Stop();
        connectTimeoutTimer = null;
    }

    // ── Connection Info Panel ─────────────────────────────

    private const string DefaultRelayAddress = "croc.schollz.com:9009";

    private static string GetRelayDisplay(string? custom) =>
        !string.IsNullOrEmpty(custom) ? custom : DefaultRelayAddress;

    private static string GetRelayWithPortDisplay(string relayDisplay) =>
        relayDisplay.Contains(':') ? relayDisplay : relayDisplay + ":9009";

    private static string GetConnTypeDisplay(bool forceLocal, bool isFallback) =>
        forceLocal ? "Local Network (direct)" :
        isFallback ? "Fallback Relay (encrypted)" :
                     "Via Relay (encrypted)";

    private string GetSecurityWithHashDisplay()
    {
        var curve = (appConfig.EncryptionCurve ?? "p256") switch
        {
            "p521" => "ECDH P-521",
            "p384" => "ECDH P-384",
            "siec" => "SIEC",
            _      => "ECDH P-256"
        };
        var hash = (appConfig.HashAlgorithm ?? "xxhash") switch
        {
            "imohash" => "imohash",
            "md5" => "MD5",
            _ => "xxhash"
        };
        return $"{curve} \u00b7 AES-256-GCM \u00b7 {hash}";
    }

    private static string ResolveLocalIp()
    {
        try
        {
            var addrs = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName());
            // Prefer IPv4, fall back to IPv6 if no IPv4 available
            var addr = addrs.FirstOrDefault(a =>
                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !System.Net.IPAddress.IsLoopback(a));
            addr ??= addrs.FirstOrDefault(a =>
                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                !System.Net.IPAddress.IsLoopback(a));
            return addr?.ToString() ?? "Unknown";
        }
        catch { return "Unknown"; }
    }

    private string GetLocalIpDisplay() => _cachedLocalIp;

    private void UpdateConnStatus(bool forSend, string status)
    {
        if (forSend) { if (sendConnToggle.Visibility == Visibility.Visible) sendStatusRowText.Text = status; }
        else { if (recvConnToggle.Visibility == Visibility.Visible) recvStatusRowText.Text = status; }
    }

    private void ShowConnInfo(bool forSend)
    {
        string relayDisplay;
        string type;
        string statusText;

        if (forSend && _directP2pHelper != null)
        {
            // Direct P2P send — show local relay + how IP was resolved
            relayDisplay = $"localhost:{DirectP2pHelper.RelayBasePort} (local relay)";
            type = _directP2pHelper.IsLanOnly
                ? "Direct Transfer (LAN only)"
                : "Direct Transfer (port-mapped)";
            statusText = "Starting local relay\u2026";

            // Show Public IP row with source method
            sendPublicIpLabel.Visibility = Visibility.Visible;
            sendPublicIpText.Visibility = Visibility.Visible;
            sendPublicIpText.Text = $"{_directP2pHelper.PublicIp} ({GetIpSourceLabel()})";
        }
        else if (!forSend && _currentRelayOverride != null && _currentRelayOverride != $"localhost:{DirectP2pHelper.RelayBasePort}")
        {
            // Direct P2P receive — show masked relay to protect sender's IP
            relayDisplay = MaskIp(_currentRelayOverride);
            var latencyStr = _directP2pLatencyMs >= 0
                ? $" \u2022 {_directP2pLatencyMs}ms ({DirectP2pHelper.LatencyLabel(_directP2pLatencyMs)})"
                : "";
            type = $"Direct Transfer (to sender){latencyStr}";
            statusText = "Connecting to sender\u2026";
        }
        else
        {
            relayDisplay = GetRelayWithPortDisplay(GetRelayDisplay(retryRelayOverride ?? appConfig.RelayServer));
            type = GetConnTypeDisplay(appConfig.ForceLocal, retryRelayOverride != null);
            statusText = "Connecting to relay\u2026";
        }

        var localIp = GetLocalIpDisplay();
        var security = GetSecurityWithHashDisplay();
        if (forSend)
        {
            sendRelayText.Text = relayDisplay;
            sendLocalIpText.Text = localIp;
            sendConnTypeText.Text = type;
            sendStatusRowText.Text = statusText;
            sendSecurityText.Text = security;
            sendConnToggle.Visibility = Visibility.Visible;
            // Keep card collapsed — user clicks toggle to expand
        }
        else
        {
            recvRelayText.Text = relayDisplay;
            recvLocalIpText.Text = localIp;
            recvConnTypeText.Text = type;
            recvStatusRowText.Text = statusText;
            recvSecurityText.Text = security;
            recvConnToggle.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Returns a label describing how the public IP was resolved.
    /// Derived from the SetupSummary log — "via UPnP", "via NAT-PMP", "via STUN", or "LAN IP".
    /// </summary>
    private string GetIpSourceLabel()
    {
        if (_directP2pHelper == null) return "unknown";
        return _directP2pHelper.IpSource switch
        {
            "UPnP"    => "via UPnP router query",
            "NAT-PMP" => "via NAT-PMP router query",
            "PCP"     => "via PCP router query",
            "IPv6"    => "IPv6 globally routable",
            "LAN"     => "LAN IP only",
            _         => "detected"
        };
    }

    private void HideConnInfo(bool forSend)
    {
        if (forSend)
        {
            sendConnInfoPanel.Visibility = Visibility.Collapsed;
            sendConnToggle.Visibility = Visibility.Collapsed;
            AnimateChevron(sendConnChevron, false);
            sendPublicIpLabel.Visibility = Visibility.Collapsed;
            sendPublicIpText.Visibility = Visibility.Collapsed;
        }
        else
        {
            recvConnInfoPanel.Visibility = Visibility.Collapsed;
            recvConnToggle.Visibility = Visibility.Collapsed;
            AnimateChevron(recvConnChevron, false);
        }
    }

    private void SendConnToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sendConnInfoPanel.Visibility == Visibility.Visible)
        {
            AnimateCollapseSection(sendConnInfoPanel);
            AnimateChevron(sendConnChevron, false);
        }
        else
        {
            AnimateExpandSection(sendConnInfoPanel);
            AnimateChevron(sendConnChevron, true);
        }
    }

    private void RecvConnToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (recvConnInfoPanel.Visibility == Visibility.Visible)
        {
            AnimateCollapseSection(recvConnInfoPanel);
            AnimateChevron(recvConnChevron, false);
        }
        else
        {
            AnimateExpandSection(recvConnInfoPanel);
            AnimateChevron(recvConnChevron, true);
        }
    }

    private static string FormatEta(double percent, DateTime startTime)
    {
        if (percent <= 0.5) return "";
        var elapsed = (DateTime.Now - startTime).TotalSeconds;
        var totalEstimate = elapsed / (percent / 100.0);
        var remaining = totalEstimate - elapsed;
        if (remaining < 1) return "almost done";
        var ts = TimeSpan.FromSeconds(remaining);
        if (ts.TotalHours >= 1) return $"~{ts:h\\:mm\\:ss} remaining";
        if (ts.TotalMinutes >= 1) return $"~{ts:m\\:ss} remaining";
        return $"~{(int)ts.TotalSeconds}s remaining";
    }

    // ── Stderr reader (croc uses \r for progress) ──────

    private void ReadStreamAsync(StreamReader stream, Action<string> onLine, List<string> errorList, List<string> lineBuffer)
    {
        Task.Run(() =>
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var buffer = new char[256];
                while (!isClosing)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    for (int i = 0; i < read; i++)
                    {
                        char c = buffer[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (sb.Length > 0)
                            {
                                var line = sb.ToString();
                                sb.Clear();
                                ProcessStderrLine(line, onLine, errorList, lineBuffer);
                            }
                        }
                        else
                        {
                            sb.Append(c);
                        }
                    }
                }
                if (sb.Length > 0 && !isClosing)
                {
                    ProcessStderrLine(sb.ToString(), onLine, errorList, lineBuffer);
                }
            }
            catch { }
        });
    }

    private void ReadStderrAsync(Process proc, Action<string> onLine, List<string> errorList, List<string> lineBuffer)
        => ReadStreamAsync(proc.StandardError, onLine, errorList, lineBuffer);

    private void ProcessStderrLine(string line, Action<string> onLine, List<string> errorList, List<string> lineBuffer)
    {
        lock (lineBuffer) { lineBuffer.Add(line); if (lineBuffer.Count > 10) lineBuffer.RemoveAt(0); }
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("fail", StringComparison.OrdinalIgnoreCase))
            lock (errorList) errorList.Add(line);
        if (!isClosing)
            try { Dispatcher.BeginInvoke(() => { if (!isClosing) onLine(line); }); } catch { }
    }

    // ── Notification sound ──────────────────────────────

    private static void PlayCompletionSound()
    {
        try { SystemSounds.Asterisk.Play(); } catch { }
    }

    // ── Windows 11 Toast Notifications ──────────────────

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
         .Replace("\"", "&quot;").Replace("'", "&apos;");

    private static readonly Lazy<string?> _appIconPath = new(() =>
    {
        try
        {
            var iconDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VIA x4");
            var iconPath = Path.Combine(iconDir, "icon.png");
            if (File.Exists(iconPath)) return iconPath;
            Directory.CreateDirectory(iconDir);
            // Copy the embedded via_logo.png resource to disk for toast notifications
            var uri = new Uri("pack://application:,,,/via_logo.png", UriKind.Absolute);
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo == null) return null;
            using var source = streamInfo.Stream;
            using var fs = File.Create(iconPath);
            source.CopyTo(fs);
            return iconPath;
        }
        catch { return null; }
    });

    // Returns a path to a cached PNG of the file's shell icon (keyed by extension).
    // Calling SHGetFileInfo on the UI thread is fine — it's a fast synchronous Win32 call.
    private static string? GetCachedFileIconPath(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "_file";
            var iconCacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VIA x4", "icons");
            var iconPath = Path.Combine(iconCacheDir, ext + ".png");
            if (File.Exists(iconPath)) return iconPath;

            var bmp = GetFileIcon(filePath);
            if (bmp == null) return null;

            Directory.CreateDirectory(iconCacheDir);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(iconPath);
            encoder.Save(fs);
            return iconPath;
        }
        catch { return null; }
    }

    private void ShowWindowsToast(string title, string body, bool highPriority = false, string? fileIconPath = null)
    {
        try
        {
            string logoXml;
            if (fileIconPath != null)
            {
                // File-specific icon: square (no circle crop — file icons look wrong cropped)
                logoXml = $"<image placement=\"appLogoOverride\" src=\"{XmlEscape(fileIconPath)}\"/>";
            }
            else if (_appIconPath.Value != null)
            {
                // App logo fallback: circular crop
                logoXml = $"<image placement=\"appLogoOverride\" src=\"{XmlEscape(_appIconPath.Value)}\" hint-crop=\"circle\"/>";
            }
            else
            {
                logoXml = "";
            }
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{XmlEscape(title)}</text>" +
                $"<text>{XmlEscape(body)}</text>" +
                logoXml +
                "</binding></visual></toast>");
            var toast = new Windows.UI.Notifications.ToastNotification(xml);
            if (highPriority)
                toast.Priority = Windows.UI.Notifications.ToastNotificationPriority.High;
            toast.Activated += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            });
            Windows.UI.Notifications.ToastNotificationManager
                .CreateToastNotifier(App.AppAumid)
                .Show(toast);
        }
        catch { }
    }

    // ── Tab Switching ────────────────────────────────────

    private bool isOnSendTab = true;

    private void SendTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (isOnSendTab) return;
        if (isReceiving)
        {
            ShowInfoBar("Tab locked", "A receive is in progress", InfoBarSeverity.Warning, 3000);
            return;
        }
        isOnSendTab = true;
        AnimatePill(false);
        sendTabText.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
        recvTabText.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        AnimateTabSwitch(recvPanel, sendPanel, false);
    }

    private void RecvTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (!isOnSendTab) return;
        if (isSending || isZipping)
        {
            ShowInfoBar("Tab locked", isZipping ? "Compression in progress" : "A send is in progress", InfoBarSeverity.Warning, 3000);
            return;
        }
        isOnSendTab = false;
        AnimatePill(true);
        recvTabText.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
        sendTabText.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        AnimateTabSwitch(sendPanel, recvPanel, true);
        // Auto-focus the code box after the tab animation completes
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => recvCodeBox.Focus());
    }

    private void SendTab_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            SendTab_Click(sender, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            e.Handled = true;
        }
    }

    private void RecvTab_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            RecvTab_Click(sender, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            e.Handled = true;
        }
    }

    private void AnimateTabSwitch(FrameworkElement outPanel, FrameworkElement inPanel, bool slideRight)
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
        fadeOut.Completed += (_, _) =>
        {
            outPanel.Visibility = Visibility.Collapsed;
            inPanel.Opacity = 0;
            inPanel.RenderTransform = new TranslateTransform(slideRight ? 30 : -30, 0);
            inPanel.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slideIn = new DoubleAnimation(slideRight ? 30 : -30, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            inPanel.BeginAnimation(OpacityProperty, fadeIn);
            ((TranslateTransform)inPanel.RenderTransform).BeginAnimation(TranslateTransform.XProperty, slideIn);
        };
        outPanel.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void FileMode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            FileMode_Click(sender, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            e.Handled = true;
        }
    }

    private void TextMode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            TextMode_Click(sender, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            e.Handled = true;
        }
    }

    private void FileMode_Click(object sender, MouseButtonEventArgs e)
    {
        if (!isTextMode) return;
        if (isSending || isZipping)
        {
            ShowInfoBar("Mode locked", isZipping ? "Please wait — compression in progress" : "A transfer is in progress", InfoBarSeverity.Warning, 3000);
            return;
        }
        isTextMode = false;
        fileModeBtn.Background = (Brush)FindResource("SystemAccentColorPrimaryBrush");
        textModeBtn.Background = System.Windows.Media.Brushes.Transparent;
        fileModeText.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
        textModeText.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        UpdateDirectModeChipVisibility();

        // Hide text box immediately, animate in the correct file panel
        sendTextBox.Visibility = Visibility.Collapsed;

        FrameworkElement target;
        if (selectedFiles.Count > 0)
        {
            target = filePreviewCard;
            sendBtn.IsEnabled = true;
        }
        else
        {
            target = dropZone;
            sendBtn.IsEnabled = false;
        }

        target.Opacity = 0;
        target.RenderTransform = new TranslateTransform(-30, 0);
        target.Visibility = Visibility.Visible;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slideIn = new DoubleAnimation(-30, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        target.BeginAnimation(OpacityProperty, fadeIn);
        ((TranslateTransform)target.RenderTransform).BeginAnimation(TranslateTransform.XProperty, slideIn);
    }

    private void TextMode_Click(object sender, MouseButtonEventArgs e)
    {
        if (isTextMode) return;
        if (isSending || isZipping)
        {
            ShowInfoBar("Mode locked", isZipping ? "Please wait — compression in progress" : "A transfer is in progress", InfoBarSeverity.Warning, 3000);
            return;
        }

        // Determine which panel to slide away from before collapsing anything
        var wasCard = filePreviewCard.Visibility == Visibility.Visible;

        sendProgressPanel.Visibility = Visibility.Collapsed;
        sendStatus.Text = "";
        sendBtn.IsEnabled = false;

        isTextMode = true;
        textModeBtn.Background = (Brush)FindResource("SystemAccentColorPrimaryBrush");
        fileModeBtn.Background = System.Windows.Media.Brushes.Transparent;
        textModeText.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
        fileModeText.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        UpdateDirectModeChipVisibility();

        // Hide both file-mode panels — the animation only handles the outPanel,
        // but both dropZone and filePreviewCard share Row 2 and can both be visible
        dropZone.Visibility = Visibility.Collapsed;
        filePreviewCard.Visibility = Visibility.Collapsed;

        // Slide in the text box
        sendTextBox.Opacity = 0;
        sendTextBox.RenderTransform = new TranslateTransform(30, 0);
        sendTextBox.Visibility = Visibility.Visible;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slideIn = new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        sendTextBox.BeginAnimation(OpacityProperty, fadeIn);
        ((TranslateTransform)sendTextBox.RenderTransform).BeginAnimation(TranslateTransform.XProperty, slideIn);

        sendBtn.IsEnabled = !string.IsNullOrWhiteSpace(sendTextBox.Text);
        sendTextBox.Focus();
    }

    private void SendTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (isTextMode && !isSending)
            sendBtn.IsEnabled = !string.IsNullOrWhiteSpace(sendTextBox.Text);
    }

    // ── Direct P2P toggle ─────────────────────────────────

    private void DirectMode_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleDirectMode();
    private void DirectMode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Space || e.Key == System.Windows.Input.Key.Enter)
        { e.Handled = true; ToggleDirectMode(); }
    }

    private void ToggleDirectMode()
    {
        if (isSending || isReceiving) return;
        if (isTextMode) return; // Direct P2P applies to file transfers only
        _isDirectMode = !_isDirectMode;
        var accentBrush = (Brush)FindResource("SystemAccentColorPrimaryBrush");
        var dimBrush    = (Brush)FindResource("TextFillColorSecondaryBrush");
        directModeChip.Background = _isDirectMode ? accentBrush : System.Windows.Media.Brushes.Transparent;
        directModeIcon.Foreground  = _isDirectMode ? (Brush)FindResource("TextFillColorPrimaryBrush") : dimBrush;
        directModeText.Foreground  = directModeIcon.Foreground;
        directInfoIcon.Foreground  = _isDirectMode ? (Brush)FindResource("TextFillColorSecondaryBrush") : (Brush)FindResource("TextFillColorTertiaryBrush");
    }

    /// <summary>Show or hide the Direct toggle based on current mode (hidden in text mode).</summary>
    private void UpdateDirectModeChipVisibility()
    {
        if (isTextMode && _isDirectMode)
        {
            _isDirectMode = false;
            directModeChip.Background = System.Windows.Media.Brushes.Transparent;
            directModeIcon.Foreground  = (Brush)FindResource("TextFillColorSecondaryBrush");
            directModeText.Foreground  = directModeIcon.Foreground;
            directInfoIcon.Foreground  = (Brush)FindResource("TextFillColorTertiaryBrush");
        }
        directModeChip.Visibility = isTextMode ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PillGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        pillHighlight.Width = pillGrid.ActualWidth / 2;
        if (!isOnSendTab)
            pillTranslate.X = pillGrid.ActualWidth / 2;
    }

    private void AnimatePill(bool toReceive)
    {
        double targetX = toReceive ? pillGrid.ActualWidth / 2 : 0;
        var anim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        pillTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    // ── Fluent 2 hover/pressed states ─────────────────────

    // Fluent 2 hover uses SubtleFillColorSecondaryBrush from theme resources

    private void Chip_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border b && b.Background is SolidColorBrush scb && scb.Color.A == 0)
            b.Background = (Brush)FindResource("SubtleFillColorSecondaryBrush");
    }

    private void Chip_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border b && !_isDirectMode)
            b.Background = System.Windows.Media.Brushes.Transparent;
        else if (sender is System.Windows.Controls.Border b2 && b2 != directModeChip)
            b2.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void PillTab_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border b)
        {
            // Only show hover on the inactive tab — active tab already has accent highlight
            int col = (int)b.GetValue(System.Windows.Controls.Grid.ColumnProperty);
            bool isActiveTab = (col == 0 && isOnSendTab) || (col == 1 && !isOnSendTab);
            if (!isActiveTab)
                b.Background = (Brush)FindResource("SubtleFillColorSecondaryBrush");
        }
    }

    private void PillTab_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border b)
            b.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void ModeBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border b)
        {
            // Don't add hover to the active mode button (it already has accent background)
            bool isFileActive = b == fileModeBtn && !isTextMode;
            bool isTextActive = b == textModeBtn && isTextMode;
            if (!isFileActive && !isTextActive)
                b.Background = (Brush)FindResource("SubtleFillColorSecondaryBrush");
        }
    }

    private void ModeBtn_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border b)
        {
            bool isFileActive = b == fileModeBtn && !isTextMode;
            bool isTextActive = b == textModeBtn && isTextMode;
            if (!isFileActive && !isTextActive)
                b.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void DirectChip_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDirectMode)
            directModeChip.Background = (Brush)FindResource("SubtleFillColorSecondaryBrush");
    }

    private void DirectChip_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDirectMode)
            directModeChip.Background = System.Windows.Media.Brushes.Transparent;
    }

    // ── Fluent 2 panel animations ───────────────────────

    private void AnimatePanelOpen(System.Windows.Controls.Border panel)
    {
        panel.Opacity = 0;
        panel.RenderTransform = new TranslateTransform(0, 8);
        panel.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var slideUp = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(250))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        panel.BeginAnimation(OpacityProperty, fadeIn);
        panel.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    private void SnapPanelState(System.Windows.Controls.Border panel)
    {
        // Cancel any in-flight animations and snap to current logical state
        panel.BeginAnimation(OpacityProperty, null);
        if (panel.RenderTransform is TranslateTransform)
            panel.RenderTransform.BeginAnimation(TranslateTransform.YProperty, null);
        panel.Opacity = 1;
        panel.RenderTransform = Transform.Identity;
    }

    private void AnimatePanelClose(System.Windows.Controls.Border panel)
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        var slideDown = new DoubleAnimation(0, 4, TimeSpan.FromMilliseconds(150))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };

        fadeOut.Completed += (_, _) =>
        {
            panel.Visibility = Visibility.Collapsed;
            panel.Opacity = 1;
            panel.RenderTransform = Transform.Identity;
        };

        if (panel.RenderTransform is not TranslateTransform)
            panel.RenderTransform = new TranslateTransform(0, 0);
        panel.BeginAnimation(OpacityProperty, fadeOut);
        panel.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slideDown);
    }

    // ── Fluent 2 smooth progress ────────────────────────

    private void AnimateProgress(System.Windows.Controls.ProgressBar bar, double toValue)
    {
        if (bar.IsIndeterminate) return;
        var anim = new DoubleAnimation(toValue, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
    }

    // ── Fluent 2 lightweight fade-in ─────────────────────

    // Fluent 2 hover feedback for clickable toggle TextBlocks
    private void AnimateChevron(SymbolIcon chevron, bool expand)
    {
        double targetAngle = expand ? 90 : 0;
        var rotation = chevron.RenderTransform as RotateTransform ?? new RotateTransform(0);
        chevron.RenderTransform = rotation;
        var anim = new DoubleAnimation(targetAngle, TimeSpan.FromMilliseconds(150))
        { EasingFunction = new CubicEase { EasingMode = expand ? EasingMode.EaseOut : EasingMode.EaseIn } };
        rotation.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private void DisclosureToggle_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.StackPanel sp)
            foreach (var child in sp.Children)
                if (child is FrameworkElement fe) fe.SetValue(TextBlock.ForegroundProperty, (Brush)FindResource("SystemAccentColorPrimaryBrush"));
    }

    private void DisclosureToggle_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.StackPanel sp)
            foreach (var child in sp.Children)
                if (child is FrameworkElement fe) fe.SetValue(TextBlock.ForegroundProperty, (Brush)FindResource("TextFillColorSecondaryBrush"));
    }

    // Fluent 2 card hover elevation (shadow2 → shadow8)
    private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border card)
            card.Effect = (System.Windows.Media.Effects.Effect)FindResource("CardShadowHover");
    }

    private void Card_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border card)
            card.Effect = (System.Windows.Media.Effects.Effect)FindResource("CardShadow");
    }

    private void AnimateFadeIn(FrameworkElement element)
    {
        element.Opacity = 0;
        element.Visibility = Visibility.Visible;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        element.BeginAnimation(OpacityProperty, fadeIn);
    }

    // ── Fluent 2 card entrance animation ─────────────────

    private void AnimateCardIn(FrameworkElement card)
    {
        card.Opacity = 0;
        card.RenderTransform = new TranslateTransform(0, 12);
        card.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var slideUp = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(300))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        card.BeginAnimation(OpacityProperty, fadeIn);
        card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    // ── Fluent 2 collapsible section expand/collapse ─────

    private void AnimateExpandSection(System.Windows.Controls.Border section)
    {
        section.Opacity = 0;
        section.RenderTransform = new ScaleTransform(1, 0) { CenterY = 0 };
        section.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var scaleUp = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        section.BeginAnimation(OpacityProperty, fadeIn);
        ((ScaleTransform)section.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
    }

    private void AnimateCollapseSection(System.Windows.Controls.Border section)
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        var scaleDown = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };

        fadeOut.Completed += (_, _) =>
        {
            section.Visibility = Visibility.Collapsed;
            section.Opacity = 1;
            section.RenderTransform = Transform.Identity;
        };

        if (section.RenderTransform is not ScaleTransform)
            section.RenderTransform = new ScaleTransform(1, 1) { CenterY = 0 };
        section.BeginAnimation(OpacityProperty, fadeOut);
        ((ScaleTransform)section.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
    }

    // ── File Drop & Browse ───────────────────────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        ResetDropZoneVisual();
        // Also reset preview card highlight in case file was dropped on it
        filePreviewCard.BorderBrush = (Brush)FindResource("ControlStrokeColorDefaultBrush");
        filePreviewCard.Background = (Brush)FindResource("ControlFillColorDefaultBrush");
        if (!isOnSendTab)
        {
            ShowInfoBar("Wrong tab", "Switch to the Send tab to drop files", InfoBarSeverity.Warning, 3000);
            return;
        }
        if (isTextMode)
        {
            ShowInfoBar("Text mode active", "Switch to File mode first before dropping files", InfoBarSeverity.Warning, 3000);
            return;
        }
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (files.Length > 0) SetFiles(files);
        }
    }

    private void SetDropBorderGradient(Color c0, Color c1, Color c2)
    {
        dropGradStop0.Color = c0;
        dropGradStop1.Color = c1;
        dropGradStop2.Color = c2;
    }

    private void DropZone_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            BrowseFile_Click(sender, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            e.Handled = true;
        }
    }

    private void DropZone_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var accent = ((SolidColorBrush)FindResource("SystemAccentColorPrimaryBrush")).Color;
        var subtle = Color.FromArgb(90, accent.R, accent.G, accent.B);
        var faint = Color.FromArgb(30, accent.R, accent.G, accent.B);
        SetDropBorderGradient(subtle, faint, subtle);
        dropZone.Background = new SolidColorBrush(Color.FromArgb(8, 128, 128, 128));
    }

    private void DropZone_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ResetDropZoneVisual();
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var accent = ((SolidColorBrush)FindResource("SystemAccentColorPrimaryBrush")).Color;
            var bright = Color.FromArgb(200, accent.R, accent.G, accent.B);
            var mid = Color.FromArgb(60, accent.R, accent.G, accent.B);
            SetDropBorderGradient(bright, mid, bright);
            dropZone.Background = new SolidColorBrush(Color.FromArgb(18, accent.R, accent.G, accent.B));
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        ResetDropZoneVisual();
    }

    private void ResetDropZoneVisual()
    {
        SetDropBorderGradient(Color.FromArgb(0x40, 0x80, 0x80, 0x80), Color.FromArgb(0x15, 0x80, 0x80, 0x80), Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        dropZone.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void PreviewCard_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            filePreviewCard.BorderBrush = (Brush)FindResource("SystemAccentColorPrimaryBrush");
            var cardAccent = ((SolidColorBrush)FindResource("SystemAccentColorPrimaryBrush")).Color;
            filePreviewCard.Background = new SolidColorBrush(Color.FromArgb(20, cardAccent.R, cardAccent.G, cardAccent.B));
        }
    }

    private void PreviewCard_DragLeave(object sender, DragEventArgs e)
    {
        filePreviewCard.BorderBrush = (Brush)FindResource("ControlStrokeColorDefaultBrush");
        filePreviewCard.Background = (Brush)FindResource("ControlFillColorDefaultBrush");
    }

    private void UpdateClearButtonVisibility()
    {
        clearFileBtn.Visibility = (isSending || isZipping) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearFile_Click(object sender, RoutedEventArgs e)
    {
        if (isSending || isZipping) return;
        zipCts?.Cancel();
        zipCts?.Dispose();
        zipCts = null;
        selectedFiles.Clear();
        CleanupTempZip();
        isZipping = false;
        filePreviewCard.Visibility = Visibility.Collapsed;
        sendProgressPanel.Visibility = Visibility.Collapsed;
        codeCard.Visibility = Visibility.Collapsed;
        sendRetryPanel.Visibility = Visibility.Collapsed;
        HideConnInfo(true);
        dropZone.Visibility = isTextMode ? Visibility.Collapsed : Visibility.Visible;
        sendBtn.IsEnabled = false;
        sendStatus.Text = "";
        sendStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        currentCode = null;
        // File is gone — unlock the Text toggle
        textModeBtn.IsHitTestVisible = true;
        textModeBtn.Opacity = 1;
    }

    private void BrowseFile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_browseDialogOpen) return;
        _browseDialogOpen = true;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "All files (*.*)|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
                SetFiles(dlg.FileNames);
        }
        finally { _browseDialogOpen = false; }
    }

    private bool _browseDialogOpen;
    private void BrowseSendFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_browseDialogOpen) return;
        _browseDialogOpen = true;
        try
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a folder to send",
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                SetFiles(new[] { dlg.SelectedPath });
        }
        finally { _browseDialogOpen = false; }
    }

    private void ShowSendFilePreview(string displayName, string detail, string? iconPath)
    {
        sendFileName.Text = displayName;
        sendFileDetail.Text = detail;
        sendFileSymbol.Symbol = GetFileSymbol(iconPath);
        var shellIcon = iconPath != null ? GetFileIcon(iconPath) : null;
        sendFileIcon.Source = shellIcon;
        sendFileIcon.Visibility = shellIcon != null ? Visibility.Visible : Visibility.Collapsed;
        sendFileSymbol.Visibility = shellIcon != null ? Visibility.Collapsed : Visibility.Visible;
        dropZone.Visibility = Visibility.Collapsed;
        AnimateCardIn(filePreviewCard);
        // Lock the Text toggle — user must press X to clear the file first
        textModeBtn.IsHitTestVisible = false;
        textModeBtn.Opacity = 0.35;
    }

    private void SetTaskbarProgress(double percent)
    {
        taskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
        taskbarInfo.ProgressValue = Math.Clamp(percent / 100.0, 0, 1);
    }
    private void SetTaskbarIndeterminate()
    {
        taskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;
    }
    private void SetTaskbarError()
    {
        taskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Error;
        taskbarInfo.ProgressValue = 1.0;
    }
    private void ClearTaskbarProgress()
    {
        taskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
        taskbarInfo.ProgressValue = 0;
    }

    private static SymbolRegular GetFileSymbol(string? path)
    {
        if (path == null) return SymbolRegular.Document24;
        if (Directory.Exists(path)) return SymbolRegular.Folder24;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" => SymbolRegular.FolderZip24,
            ".exe" or ".msi" or ".msix" => SymbolRegular.AppGeneric24,
            ".pdf" => SymbolRegular.DocumentPdf24,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" or ".tiff" => SymbolRegular.Image24,
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".flv" => SymbolRegular.Video24,
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".wma" or ".m4a" => SymbolRegular.MusicNote124,
            ".txt" or ".log" or ".md" or ".csv" => SymbolRegular.DocumentText24,
            ".doc" or ".docx" or ".rtf" or ".odt" => SymbolRegular.DocumentText24,
            ".xls" or ".xlsx" or ".ods" => SymbolRegular.DocumentTable24,
            ".ppt" or ".pptx" or ".odp" => SymbolRegular.SlideText24,
            ".cs" or ".js" or ".ts" or ".py" or ".java" or ".cpp" or ".c" or ".h" or ".go" or ".rs"
                or ".html" or ".css" or ".json" or ".xml" or ".yaml" or ".yml" => SymbolRegular.Code24,
            ".dll" or ".sys" or ".bat" or ".cmd" or ".ps1" or ".sh" => SymbolRegular.WindowConsole20,
            _ => SymbolRegular.Document24
        };
    }

    private async void SetFiles(string[] paths)
    {
        zipCts?.Cancel();
        zipCts?.Dispose();
        CleanupTempZip();
        selectedFiles.Clear();
        resumeBanner.Visibility = Visibility.Collapsed; // dismiss banner when new files are dropped

        bool hasFolder = paths.Any(Directory.Exists);

        if (hasFolder && paths.Length == 1 && Directory.Exists(paths[0]))
        {
            var folderName = Path.GetFileName(paths[0]);
            ShowSendFilePreview(folderName, "Scanning folder...", paths[0]);
            sendBtn.IsEnabled = false;

            // Check folder size before zipping
            long folderSize = 0;
            try { folderSize = Directory.EnumerateFiles(paths[0], "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); } catch { }
            if (folderSize > 50L * 1024 * 1024 * 1024)
            {
                ShowInfoBar("Folder too large", $"This folder is {FormatSize(folderSize)} — max for zipping is 50 GB. Split it into smaller parts.", InfoBarSeverity.Error, 8000);
                filePreviewCard.Visibility = Visibility.Collapsed;
                dropZone.Visibility = Visibility.Visible;
                textModeBtn.IsHitTestVisible = true;
                textModeBtn.Opacity = 1;
                return;
            }
            if (folderSize > 10L * 1024 * 1024 * 1024)
            {
                var result = MessageBox.Show(
                    $"This folder is {FormatSize(folderSize)}. Zipping may take a while and use significant disk space.\n\nContinue?",
                    "Large folder", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != System.Windows.MessageBoxResult.Yes)
                {
                    filePreviewCard.Visibility = Visibility.Collapsed;
                    dropZone.Visibility = Visibility.Visible;
                    textModeBtn.IsHitTestVisible = true;
                    textModeBtn.Opacity = 1;
                    return;
                }
            }

            // Check available space in temp folder before zipping
            try
            {
                var tempRoot = Path.GetPathRoot(Path.GetTempPath());
                if (tempRoot != null)
                {
                    var tempDrive = new DriveInfo(tempRoot);
                    if (tempDrive.AvailableFreeSpace < folderSize)
                    {
                        ShowInfoBar("Not enough temp space",
                            $"Zipping needs ~{FormatSize(folderSize)} free in {tempRoot} but only {FormatSize(tempDrive.AvailableFreeSpace)} available. Free up space or change your temp folder.",
                            InfoBarSeverity.Error, 8000);
                        filePreviewCard.Visibility = Visibility.Collapsed;
                        dropZone.Visibility = Visibility.Visible;
                        textModeBtn.IsHitTestVisible = true;
                        textModeBtn.Opacity = 1;
                        return;
                    }
                }
            }
            catch { }

            sendProgressPanel.Visibility = Visibility.Visible;
            sendProgress.IsIndeterminate = false;
            sendProgress.Value = 0;

            tempZipPath = Path.Combine(Path.GetTempPath(), $"via_{folderName}_{Guid.NewGuid():N}.zip");
            var zipPath = tempZipPath;
            var folderPath = paths[0];
            zipCts = new CancellationTokenSource();
            var ct = zipCts.Token;
            var gen = ++zipGeneration;
            var progress = new Progress<(int current, int total, string fileName)>(p =>
            {
                if (gen != zipGeneration) return;
                AnimateProgress(sendProgress, p.total > 0 ? (double)p.current / p.total * 100 : 0);
                sendFileDetail.Text = $"Zipping {p.current}/{p.total} — {p.fileName}";
            });
            isZipping = true;
            UpdateClearButtonVisibility();
            try
            {
                await Task.Run(() => ZipFolderWithProgress(folderPath, zipPath, progress, ct), ct);
            }
            catch (OperationCanceledException)
            {
                if (gen == zipGeneration) { isZipping = false; UpdateClearButtonVisibility(); CleanupTempZip(); }
                return;
            }
            catch (Exception ex)
            {
                if (gen == zipGeneration)
                {
                    isZipping = false;
                    UpdateClearButtonVisibility();
                    sendProgressPanel.Visibility = Visibility.Collapsed;
                    filePreviewCard.Visibility = Visibility.Collapsed;
                    dropZone.Visibility = isTextMode ? Visibility.Collapsed : Visibility.Visible;
                    textModeBtn.IsHitTestVisible = true;
                    textModeBtn.Opacity = 1;
                    CleanupTempZip();
                    ShowInfoBar("Zip failed", $"Could not zip folder: {ex.Message}", InfoBarSeverity.Error, 8000);
                }
                return;
            }
            if (gen != zipGeneration) return;
            isZipping = false;
            UpdateClearButtonVisibility();
            if (ct.IsCancellationRequested || tempZipPath == null) return;

            sendProgressPanel.Visibility = Visibility.Collapsed;
            selectedFiles.Add(tempZipPath);
            var info = new FileInfo(tempZipPath);
            sendFileDetail.Text = $"{FormatSize(info.Length)}  •  ZIP (from folder)";
            sendFileSymbol.Symbol = SymbolRegular.FolderZip24;
            var zipIcon = await GetFileIconAsync(tempZipPath);
            sendFileIcon.Source = zipIcon;
            sendFileIcon.Visibility = zipIcon != null ? Visibility.Visible : Visibility.Collapsed;
            sendFileSymbol.Visibility = zipIcon != null ? Visibility.Collapsed : Visibility.Visible;
            if (gen != zipGeneration) return;
            sendBtn.IsEnabled = true;
            sendStatus.Text = "";
            if (!IsActive) ShowWindowsToast("Ready to Send", $"Compression complete · {FormatSize(info.Length)}");
        }
        else if (paths.Length == 1 && !Directory.Exists(paths[0]))
        {
            var path = paths[0];
            selectedFiles.Add(path);
            var name = Path.GetFileName(path);
            var info = new FileInfo(path);
            var typeDesc = GetFileTypeDescription(path);
            ShowSendFilePreview(name, $"{FormatSize(info.Length)}  •  {typeDesc}", path);
            sendBtn.IsEnabled = true;
            sendStatus.Text = "";
        }
        else
        {
            var filePaths = new List<string>();
            int folderCount = 0;

            foreach (var p in paths)
            {
                if (Directory.Exists(p)) folderCount++;
                else if (File.Exists(p)) filePaths.Add(p);
            }

            if (folderCount > 0)
            {
                int totalItems = filePaths.Count + folderCount;
                ShowSendFilePreview($"{totalItems} items", "Scanning...", paths[0]);
                sendBtn.IsEnabled = false;

                // Check total size before zipping
                long bundleSize = 0;
                try
                {
                    foreach (var p in paths)
                    {
                        if (Directory.Exists(p))
                            bundleSize += Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                        else if (File.Exists(p))
                            bundleSize += new FileInfo(p).Length;
                    }
                }
                catch { }
                if (bundleSize > 50L * 1024 * 1024 * 1024)
                {
                    ShowInfoBar("Selection too large", $"Total size is {FormatSize(bundleSize)} — max for zipping is 50 GB.", InfoBarSeverity.Error, 8000);
                    filePreviewCard.Visibility = Visibility.Collapsed;
                    dropZone.Visibility = Visibility.Visible;
                    textModeBtn.IsHitTestVisible = true;
                    textModeBtn.Opacity = 1;
                    return;
                }
                if (bundleSize > 10L * 1024 * 1024 * 1024)
                {
                    var result = MessageBox.Show(
                        $"Total size is {FormatSize(bundleSize)}. Zipping may take a while.\n\nContinue?",
                        "Large selection", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result != System.Windows.MessageBoxResult.Yes)
                    {
                        filePreviewCard.Visibility = Visibility.Collapsed;
                        dropZone.Visibility = Visibility.Visible;
                        textModeBtn.IsHitTestVisible = true;
                        textModeBtn.Opacity = 1;
                        return;
                    }
                }

                // Check available space in temp folder before zipping
                try
                {
                    var tempRoot = Path.GetPathRoot(Path.GetTempPath());
                    if (tempRoot != null)
                    {
                        var tempDrive = new DriveInfo(tempRoot);
                        if (tempDrive.AvailableFreeSpace < bundleSize)
                        {
                            ShowInfoBar("Not enough temp space",
                                $"Zipping needs ~{FormatSize(bundleSize)} free in {tempRoot} but only {FormatSize(tempDrive.AvailableFreeSpace)} available.",
                                InfoBarSeverity.Error, 8000);
                            filePreviewCard.Visibility = Visibility.Collapsed;
                            dropZone.Visibility = Visibility.Visible;
                            textModeBtn.IsHitTestVisible = true;
                            textModeBtn.Opacity = 1;
                            return;
                        }
                    }
                }
                catch { }

                sendProgressPanel.Visibility = Visibility.Visible;
                sendProgress.IsIndeterminate = false;
                sendProgress.Value = 0;

                tempZipPath = Path.Combine(Path.GetTempPath(), $"via_bundle_{Guid.NewGuid():N}.zip");
                var zipPath = tempZipPath;
                var allPaths = paths.ToArray();
                zipCts = new CancellationTokenSource();
                var ct = zipCts.Token;
                var gen = ++zipGeneration;
                var progress = new Progress<(int current, int total, string fileName)>(p =>
                {
                    if (gen != zipGeneration) return;
                    AnimateProgress(sendProgress, p.total > 0 ? (double)p.current / p.total * 100 : 0);
                    sendFileDetail.Text = $"Zipping {p.current}/{p.total} — {p.fileName}";
                });
                isZipping = true;
                UpdateClearButtonVisibility();
                try
                {
                    await Task.Run(() => ZipMultiWithProgress(allPaths, zipPath, progress, ct), ct);
                }
                catch (OperationCanceledException)
                {
                    if (gen == zipGeneration) { isZipping = false; UpdateClearButtonVisibility(); CleanupTempZip(); }
                    return;
                }
                catch (Exception ex)
                {
                    if (gen == zipGeneration)
                    {
                        isZipping = false;
                        UpdateClearButtonVisibility();
                        sendProgressPanel.Visibility = Visibility.Collapsed;
                        filePreviewCard.Visibility = Visibility.Collapsed;
                        dropZone.Visibility = isTextMode ? Visibility.Collapsed : Visibility.Visible;
                        textModeBtn.IsHitTestVisible = true;
                        textModeBtn.Opacity = 1;
                        CleanupTempZip();
                        ShowInfoBar("Zip failed", $"Could not zip files: {ex.Message}", InfoBarSeverity.Error, 8000);
                    }
                    return;
                }
                if (gen != zipGeneration) return;
                isZipping = false;
                UpdateClearButtonVisibility();
                if (ct.IsCancellationRequested || tempZipPath == null) return;

                sendProgressPanel.Visibility = Visibility.Collapsed;
                selectedFiles.Add(tempZipPath);
                var info = new FileInfo(tempZipPath);
                sendFileDetail.Text = $"{FormatSize(info.Length)}  •  ZIP ({totalItems} items)";
                sendFileSymbol.Symbol = SymbolRegular.FolderZip24;
                var bundleIcon = await GetFileIconAsync(tempZipPath);
                sendFileIcon.Source = bundleIcon;
                sendFileIcon.Visibility = bundleIcon != null ? Visibility.Visible : Visibility.Collapsed;
                sendFileSymbol.Visibility = bundleIcon != null ? Visibility.Collapsed : Visibility.Visible;
                if (gen != zipGeneration) return;
                sendBtn.IsEnabled = true;
                sendStatus.Text = "";
                if (!IsActive) ShowWindowsToast("Ready to Send", $"Compression complete · {FormatSize(info.Length)}");
            }
            else
            {
                selectedFiles.AddRange(filePaths);
                long totalSize = filePaths.Sum(f => new FileInfo(f).Length);
                var firstName = Path.GetFileName(filePaths[0]);
                var displayName = $"{firstName} +{filePaths.Count - 1} more";
                ShowSendFilePreview(displayName, $"{FormatSize(totalSize)}  •  {filePaths.Count} files", filePaths[0]);
                sendBtn.IsEnabled = true;
                sendStatus.Text = "";
            }
        }
    }

    private void CleanupTempZip()
    {
        if (tempZipPath != null)
        {
            try { File.Delete(tempZipPath); } catch { }
            tempZipPath = null;
        }
    }

    private static void ZipFolderWithProgress(string folderPath, string zipPath, IProgress<(int current, int total, string fileName)> progress, CancellationToken ct)
    {
        // Two-pass: count first (lightweight enumeration), then zip — avoids loading all paths into memory
        int total = 0;
        foreach (var _ in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            total++;
        }
        int current = 0;
        int skipped = 0;
        var lastReport = Environment.TickCount64;
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var entryName = Path.GetRelativePath(folderPath, file);
            try
            {
                zip.CreateEntryFromFile(file, entryName, GetCompressionLevel(file));
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                // File locked or deleted mid-zip — skip and continue rather than aborting entire zip
                skipped++;
                Log($"Zip: skipped locked/missing file: {Path.GetFileName(file)}");
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
                Log($"Zip: skipped inaccessible file: {Path.GetFileName(file)}");
            }
            current++;
            var now = Environment.TickCount64;
            if (now - lastReport >= 100 || current == total)
            {
                var label = skipped > 0
                    ? $"Zipping {current}/{total} ({skipped} skipped) — {Path.GetFileName(file)}"
                    : Path.GetFileName(file);
                progress.Report((current, total, label));
                lastReport = now;
            }
        }
        if (skipped > 0)
            Log($"Zip complete: {current - skipped}/{total} files added, {skipped} skipped");
    }

    private static void ZipMultiWithProgress(string[] paths, string zipPath, IProgress<(int current, int total, string fileName)> progress, CancellationToken ct)
    {
        var fileEntries = new List<(string sourcePath, string entryName)>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                var dirName = Path.GetFileName(p);
                foreach (var file in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    fileEntries.Add((file, Path.Combine(dirName, Path.GetRelativePath(p, file))));
            }
            else if (File.Exists(p))
            {
                fileEntries.Add((p, Path.GetFileName(p)));
            }
        }

        int total = fileEntries.Count;
        int skipped = 0;
        var lastReport = Environment.TickCount64;
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        for (int i = 0; i < fileEntries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                zip.CreateEntryFromFile(fileEntries[i].sourcePath, fileEntries[i].entryName, GetCompressionLevel(fileEntries[i].sourcePath));
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                skipped++;
                Log($"Zip: skipped locked/missing file: {Path.GetFileName(fileEntries[i].sourcePath)}");
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
                Log($"Zip: skipped inaccessible file: {Path.GetFileName(fileEntries[i].sourcePath)}");
            }
            var now = Environment.TickCount64;
            if (now - lastReport >= 100 || i == fileEntries.Count - 1)
            {
                var label = skipped > 0
                    ? $"{Path.GetFileName(fileEntries[i].sourcePath)} ({skipped} skipped)"
                    : Path.GetFileName(fileEntries[i].sourcePath);
                progress.Report((i + 1, total, label));
                lastReport = now;
            }
        }
        if (skipped > 0)
            Log($"Zip complete: {total - skipped}/{total} files added, {skipped} skipped");
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private static BitmapSource? GetFileIcon(string path)
    {
        try
        {
            // Try SHGetFileInfo first — works reliably for all file types
            var shfi = new SHFILEINFO();
            // First try with the actual file (gets the real associated icon)
            var result = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_ICON | SHGFI_LARGEICON);
            if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            {
                // Fallback: use extension-only lookup (works even for temp/deleted files)
                shfi = new SHFILEINFO();
                result = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref shfi, (uint)Marshal.SizeOf(shfi),
                    SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
            }
            if (result != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
            {
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    return bmp;
                }
                finally { DestroyIcon(shfi.hIcon); }
            }

            // Last resort: ExtractAssociatedIcon
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var fallback = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }
        catch { return null; }
    }

    private static async Task<BitmapSource?> GetFileIconAsync(string path)
    {
        return await Task.Run(() => GetFileIcon(path));
    }

    private static string GetFileTypeDescription(string path)
    {
        var ext = Path.GetExtension(path).ToUpperInvariant();
        if (string.IsNullOrEmpty(ext)) return "File";
        return ext.TrimStart('.') + " file";
    }

    // ── Send ─────────────────────────────────────────────

    private async void StartSend(bool resume = false)
    {
        if (isSending) return;
        // Clear stale retry CTS so cancel correctly enters process-kill path
        sendRetryCts?.Dispose();
        sendRetryCts = null;
        if (crocPath == null || !File.Exists(crocPath))
        {
            // Engine missing — try re-extracting before giving up
            ExtractCroc();
            if (crocPath == null || !File.Exists(crocPath))
            {
                ShowInfoBar("Engine not ready", "The transfer engine could not be loaded. Check if antivirus is blocking it, or restart VIA x4.", InfoBarSeverity.Error, 10000);
                return;
            }
        }
        // Close overlays and resume banner when starting a transfer
        settingsPanel.Visibility = Visibility.Collapsed;
        historyPanel.Visibility = Visibility.Collapsed;
        resumeBanner.Visibility = Visibility.Collapsed;

        if (isTextMode)
        {
            var text = sendTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length > 1_000_000)
            {
                ShowInfoBar("Text too long", $"Text is {FormatSize(text.Length * 2)} — max is ~1 MB. Use file mode for larger content.", InfoBarSeverity.Warning, 5000);
                return;
            }
            StartSendText(text);
            return;
        }
        if (selectedFiles.Count == 0) return;

        // Validate all files still exist before starting
        var missing = selectedFiles.Where(f => !File.Exists(f) && !Directory.Exists(f)).ToList();
        if (missing.Count > 0)
        {
            ShowInfoBar("File not found", $"{Path.GetFileName(missing[0])} was moved or deleted", InfoBarSeverity.Error);
            return;
        }

        isSending = true;
        sendErrors.Clear();
        lastSendLines.Clear();
        if (!resume) { currentCode = null; sendRetryCount = 0; _sendReceiverSeen = false; }
        lastSendFiles.Clear();
        lastSendFiles.AddRange(selectedFiles);

        try { totalFileSize = selectedFiles.Sum(f => new FileInfo(f).Length); }
        catch { totalFileSize = 0; }
        if (!resume) lastSendPercent = 0;

        // ── Direct P2P setup ────
        // Run on first attempt, OR on resume if the helper was cleaned up after final failure.
        // Auto-retries keep _directP2pHelper alive (CleanupDirectP2p is only called at the
        // end of OnSendDone, which auto-retries skip by returning early).
        if (_isDirectMode && _directP2pHelper == null)
        {
            if (resume) _directP2pResumeCodeChanged = true;
            sendBtn.IsEnabled = false;
            UpdateClearButtonVisibility();
            sendCancelBtn.Visibility = Visibility.Visible;
            sendProgressPanel.Visibility = Visibility.Visible;
            sendProgress.IsIndeterminate = true;
            sendStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
            SetTaskbarIndeterminate();

            _directSetupCts?.Dispose();
            _directSetupCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            _directP2pHelper = new DirectP2pHelper(crocPath!);

            StartWaitingDots(sendStatus, "Setting up direct connection");
            string? setupErr = null;
            try
            {
                setupErr = await _directP2pHelper.SetupAsync(_directSetupCts.Token);
            }
            catch (OperationCanceledException) { setupErr = "cancelled"; }
            catch (Exception ex) { setupErr = ex.Message; }
            StopWaitingDots();

            if (!isSending) // user cancelled during setup
            {
                _directP2pHelper.Dispose();
                _directP2pHelper = null;
                _directSetupCts?.Dispose();
                _directSetupCts = null;
                return;
            }

            if (setupErr != null)
            {
                // Setup failed — fall back to normal relay, clear the helper
                _directP2pHelper.Dispose();
                _directP2pHelper = null;
                _directSetupCts?.Dispose();
                _directSetupCts = null;
                _currentRelayOverride = null;
                _directP2pResumeCodeChanged = false;
                ShowInfoBar("Direct Transfer unavailable", $"{setupErr} — falling back to relay.", InfoBarSeverity.Warning, 8000);
                Log($"Direct P2P setup failed: {setupErr}");
            }
            else
            {
                // Setup succeeded — point croc at the local relay
                _currentRelayOverride = $"localhost:{DirectP2pHelper.RelayBasePort}";
                _directP2pHelper.NetworkChanged += OnDirectP2pNetworkChanged;
                _directP2pHelper.RelayDied += OnDirectP2pRelayDied;

                // Combine all Direct P2P warnings into a single InfoBar so nothing is lost
                var warnings = new List<string>();
                if (_directP2pHelper.VpnDetected)
                    warnings.Add("VPN detected \u2014 may route through VPN tunnel instead of home network");
                if (_directP2pHelper.FirewallRuleFailed)
                    warnings.Add("Firewall rule failed \u2014 incoming connections may be blocked (admin rights may be required)");

                if (_directP2pHelper.IsBehindCgnat)
                    warnings.Add("CGNAT detected \u2014 Direct Transfer only works on the same network");
                else if (_directP2pHelper.RestrictedNetwork)
                    warnings.Add("Restricted network \u2014 guest/public WiFi blocks port forwarding, LAN only");
                else if (_directP2pHelper.UsingIpv6)
                    Log($"Direct P2P ready — IPv6 globally routable ({_directP2pHelper.SetupSummary})");
                else if (_directP2pHelper.IsLanOnly)
                    warnings.Add("Router doesn't support UPnP/NAT-PMP/PCP \u2014 share code only works on the same network");
                else
                    Log($"Direct P2P ready — {_directP2pHelper.IpSource} port-mapped ({_directP2pHelper.SetupSummary})");

                if (warnings.Count > 0)
                {
                    var severity = warnings.Any(w => w.Contains("CGNAT")) ? InfoBarSeverity.Warning : InfoBarSeverity.Warning;
                    ShowInfoBar("Direct Transfer", string.Join("\n", warnings), InfoBarSeverity.Warning, 10000);
                }
            }

            sendProgressPanel.Visibility = Visibility.Collapsed;
            sendProgress.IsIndeterminate = true;
            ClearTaskbarProgress();
        }

        sendBtn.IsEnabled = false;
        UpdateClearButtonVisibility();
        sendRetryPanel.Visibility = Visibility.Collapsed;
        sendCancelBtn.Visibility = Visibility.Visible;
        sendProgressPanel.Visibility = Visibility.Visible;
        sendProgress.IsIndeterminate = !resume;
        sendProgress.Value = resume ? lastSendPercent : 0;
        sendProgressDetail.Text = resume ? $"Resuming from {lastSendPercent:F0}%..." : "";
        sendEtaText.Text = "";
        sendSpeedText.Text = ""; sendSpeedRow.Visibility = Visibility.Collapsed;
        sendElapsedText.Text = "";
        SetTaskbarIndeterminate();
        codeCard.Visibility = Visibility.Collapsed;
        sendStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        if (resume)
            sendStatus.Text = "Resuming transfer...";
        else
            StartWaitingDots(sendStatus, "Connecting");
        StartElapsedTimer(true);
        StartConnectTimeout(true);
        ShowConnInfo(true);

        // Build argument list — individual entries prevent any path/value from injecting extra flags
        var sendPsi = new ProcessStartInfo
        {
            FileName = crocPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        sendPsi.ArgumentList.Add("--yes");
        AddCrocGlobalFlags(sendPsi.ArgumentList);
        // Auto-disable croc's compression when sending our own zip bundle —
        // we already handled compression per-file, so double-compressing wastes CPU on both sides.
        if (tempZipPath != null && !appConfig.NoCompress)
            sendPsi.ArgumentList.Add("--no-compress");
        sendPsi.ArgumentList.Add("send");
        // Send subcommand flags must come after "send"
        if (!string.IsNullOrEmpty(currentCode)) { sendPsi.ArgumentList.Add("--code"); sendPsi.ArgumentList.Add(currentCode); }
        AddCrocSendFlags(sendPsi.ArgumentList);
        foreach (var f in selectedFiles) sendPsi.ArgumentList.Add(f);
        if (TryStartWithConPty(sendPsi, null, ParseSendOutput, sendErrors, lastSendLines,
                ec => Dispatcher.BeginInvoke(() => { if (!isClosing) OnSendDone(ec); }), out int sPid))
        {
            _sendConPtyPid = sPid;
            sendProc = null;
        }
        else
        {
            sendProc = new Process { StartInfo = sendPsi, EnableRaisingEvents = true };
            var sendProcLocal = sendProc;
            sendProc.Exited += (_, _) =>
            {
                try { sendProcLocal.WaitForExit(); } catch { }
                var code = -1;
                try { code = sendProcLocal.ExitCode; } catch { }
                try { Dispatcher.BeginInvoke(() => { if (!isClosing) OnSendDone(code); }); } catch { }
            };
            try
            {
                sendProc.Start();
                ReadStreamAsync(sendProc.StandardOutput, ParseSendOutput, sendErrors, lastSendLines);
                ReadStderrAsync(sendProc, ParseSendOutput, sendErrors, lastSendLines);
            }
            catch (Exception ex)
            {
                isSending = false;
                sendBtn.IsEnabled = selectedFiles.Count > 0;
                UpdateClearButtonVisibility();
                sendCancelBtn.Visibility = Visibility.Collapsed;
                sendProgressPanel.Visibility = Visibility.Collapsed;
                StopElapsedTimer();
                StopWaitingDots();
                StopConnectTimeout();
                HideConnInfo(true);
                ClearTaskbarProgress();
                sendStatus.Text = "";
                ShowInfoBar("Error", $"Failed to start transfer: {ex.Message}", InfoBarSeverity.Error);
                sendProc = null;
            }
        }
    }

    private void ParseSendOutput(string line)
    {
        if (!isSending) return; // ignore queued output after cancel
        if (line.Contains("Code is:"))
        {
            var parts = line.Split("Code is:");
            if (parts.Length > 1)
            {
                currentCode = parts[1].Trim();
                // In Direct P2P mode the receiver needs to know the relay address,
                // so we embed it in the code as "d:ip:port:croccode".
                _shareCode = (_isDirectMode && _directP2pHelper != null)
                    ? $"d:{EncodeRelayForCode(_directP2pHelper.PublicIp, DirectP2pHelper.RelayBasePort, currentCode)}:{currentCode}"
                    : currentCode;
                codeText.Text = _shareCode;
                // Shrink font for long codes (e.g. Direct Transfer) so they fit
                codeText.FontSize = _shareCode.Length > 25 ? 14 : 20;
                codeCardLabel.Text = (_isDirectMode && _directP2pHelper != null)
                    ? "Share this Direct Transfer code with the receiver"
                    : "Share this code with the receiver";
                AnimateCardIn(codeCard);
                UpdateDirectP2pStatusCard();
                sendConnected = true;
                StopConnectTimeout();
                StopWaitingDots();
                sendStatus.Text = "Waiting for receiver...";
                UpdateConnStatus(true, "Code ready — waiting for receiver");
                StartWaitingDots(sendStatus, "Waiting for receiver");

                // On Direct P2P resume, the share code changes (new relay address) — warn sender
                if (_directP2pResumeCodeChanged)
                {
                    _directP2pResumeCodeChanged = false;
                    ShowInfoBar("Code updated", "Your Direct Transfer address changed — share this new code with the receiver.", InfoBarSeverity.Warning, 12000);
                }

                // Persist code + files for resume across restarts
                appConfig.LastSendCode = currentCode;
                appConfig.LastSendFilePaths = new List<string>(selectedFiles);
                SaveSettings();
            }
        }
        else if (line.Contains("Sending"))
        {
            _sendReceiverSeen = true;
            StopWaitingDots();
            sendStatus.Text = "Negotiating encryption...";
            UpdateConnStatus(true, "Negotiating encryption");
        }

        var match = ProgressRegex.Match(line);
        if (match.Success)
        {
            StopWaitingDots();
            sendStatus.Text = "Transferring...";
            UpdateConnStatus(true, "Transferring");
            var pct = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var speed = match.Groups[2].Value + " " + match.Groups[3].Value + "/s";
            lastSendPercent = pct;
            sendProgress.IsIndeterminate = false;
            AnimateProgress(sendProgress, pct);
            if (totalFileSize > 0)
            {
                var transferred = FormatSize((long)(totalFileSize * pct / 100));
                var total = FormatSize(totalFileSize);
                sendProgressDetail.Text = $"{transferred} / {total}";
            }
            else
            {
                sendProgressDetail.Text = $"{pct:F0}%";
            }
            sendSpeedText.Text = $"↑  {speed}";
            sendEtaText.Text = FormatEta(pct, sendStartTime);
            sendSpeedRow.Visibility = Visibility.Visible;
            SetTaskbarProgress(pct);
        }
    }

    private void OnSendDone(int exitCode)
    {
        StopWaitingDots();
        StopElapsedTimer();
        StopConnectTimeout();
        HideConnInfo(true);
        string errSummary;
        lock (sendErrors) errSummary = sendErrors.Count > 0 ? string.Join("; ", sendErrors) : "";
        if (string.IsNullOrEmpty(errSummary))
            lock (lastSendLines) errSummary = lastSendLines.LastOrDefault(l => !ProgressRegex.IsMatch(l)) ?? "";
        Log($"Send completed: exitCode={exitCode}{(string.IsNullOrEmpty(errSummary) ? "" : $" err=[{errSummary}]")}");

        // ConPTY startup crash (STATUS_DLL_INIT_FAILED) — Go runtime failed to init under PTY.
        // _conPtyDisabled is now true; retry immediately with standard Process (no progress bar).
        if (unchecked((uint)exitCode) == 0xC0000142u)
        {
            sendProgressPanel.Visibility = Visibility.Collapsed;
            sendCancelBtn.Visibility = Visibility.Collapsed;
            isSending = false;
            _sendConPtyPid = 0; sendProc?.Dispose(); sendProc = null;
            sendStatus.Text = "";
            Log("ConPTY incompatible on this system — retrying with standard process");
            _ = Task.Delay(300).ContinueWith(_ =>
                Dispatcher.BeginInvoke(() => { if (!isClosing) StartSend(false); }),
                TaskScheduler.Default);
            return;
        }

        // Auto-retry on relay/connection dropout
        bool skipSendErrorHandling = false;
        bool codeExpired = false; // true when relay dropped idle channel before any receiver connected
        if (exitCode != 0 && sendStatus.Text != "Transfer cancelled")
        {
            if (_sendConnTimedOut)
            {
                _sendConnTimedOut = false;
                skipSendErrorHandling = true; // InfoBar already shown by timeout handler
                retryRelayOverride = null;
                var tName = selectedFiles.Count == 1 ? Path.GetFileName(selectedFiles[0]) : $"{selectedFiles.Count} files";
                AddHistoryRecord(tName, "Sent", FormatSize(totalFileSize), "Failed");
            }
            else
            {
            bool retryable;
            lock (sendErrors) { lock (lastSendLines) { retryable = IsRetryableError(sendErrors, lastSendLines); } }

            // Relay dropped the idle channel — code is now dead, receiver's copy won't work either
            // Only consider code expired if the relay dropped the idle channel before
            // any receiver connected. If _sendReceiverSeen is true, a receiver DID connect
            // (PAKE/transfer just failed), so the code was used — not idle-expired.
            codeExpired = sendConnected && !_sendReceiverSeen && lastSendPercent == 0 && retryable;

            if (!codeExpired && sendRetryCount < MaxAutoRetries && !string.IsNullOrEmpty(currentCode) && retryable)
            {
                sendRetryCount++;
                var delay = (int)Math.Pow(2, sendRetryCount) * 1000;
                isSending = false;
                _sendConPtyPid = 0; sendProc?.Dispose(); sendProc = null;
                var relayNote = retryRelayOverride != null ? " (fallback relay)" : "";
                sendStatus.Text = $"Connection lost. Retrying ({sendRetryCount}/{MaxAutoRetries}){relayNote}...";
                sendStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
                sendProgressPanel.Visibility = Visibility.Visible;
                sendProgress.IsIndeterminate = true;
                sendProgressDetail.Text = "";
                sendEtaText.Text = "";
                sendSpeedText.Text = ""; sendSpeedRow.Visibility = Visibility.Collapsed;
                Log($"Send auto-retry {sendRetryCount}/{MaxAutoRetries} in {delay}ms");

                sendRetryCts?.Dispose();
                sendRetryCts = new CancellationTokenSource();
                var token = sendRetryCts.Token;
                _ = Task.Delay(delay, token).ContinueWith(_ =>
                {
                    if (!token.IsCancellationRequested)
                        Dispatcher.BeginInvoke(() => { if (!isClosing) StartSend(true); }); // resume=true preserves retryCount and code
                }, TaskScheduler.Default);
                return;
            }
            // Primary retries exhausted — try fallback relay once if configured
            if (!codeExpired && retryable && !string.IsNullOrEmpty(appConfig.FallbackRelayServer) && retryRelayOverride == null)
            {
                retryRelayOverride = appConfig.FallbackRelayServer;
                sendRetryCount = 0;
                isSending = false;
                _sendConPtyPid = 0; sendProc?.Dispose(); sendProc = null;
                sendStatus.Text = "Trying fallback relay...";
                sendStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
                sendProgressPanel.Visibility = Visibility.Visible;
                sendProgress.IsIndeterminate = true;
                sendProgressDetail.Text = "";
                sendEtaText.Text = "";
                sendSpeedText.Text = ""; sendSpeedRow.Visibility = Visibility.Collapsed;
                Log($"Send switching to fallback relay: {retryRelayOverride}");
                sendRetryCts?.Dispose();
                sendRetryCts = new CancellationTokenSource();
                var fbToken = sendRetryCts.Token;
                _ = Task.Delay(2000, fbToken).ContinueWith(_ =>
                {
                    if (!fbToken.IsCancellationRequested)
                        Dispatcher.BeginInvoke(() => { if (!isClosing) StartSend(false); });
                }, TaskScheduler.Default);
                return;
            }
            } // closes else
        }

        sendProgressPanel.Visibility = Visibility.Collapsed;
        sendProgressDetail.Text = "";
        sendEtaText.Text = "";
        sendSpeedText.Text = ""; sendSpeedRow.Visibility = Visibility.Collapsed;
        sendElapsedText.Text = "";
        sendCancelBtn.Visibility = Visibility.Collapsed;
        codeCard.Visibility = Visibility.Collapsed;
        isSending = false;
        UpdateClearButtonVisibility();
        sendBtn.IsEnabled = selectedFiles.Count > 0;

        if (exitCode == 0)
        {
            sendBtn.IsEnabled = false; // prevent re-send while reset timer clears selectedFiles
            var sentName = selectedFiles.Count == 1 ? Path.GetFileName(selectedFiles[0]) : $"{selectedFiles.Count} files";
            AddHistoryRecord(sentName, "Sent", FormatSize(totalFileSize), "Success");

            // Clear persisted resume info on success
            appConfig.LastSendCode = "";
            appConfig.LastSendFilePaths.Clear();
            SaveSettings();

            sendRetryCts?.Dispose(); sendRetryCts = null;
            retryRelayOverride = null; // reset fallback relay on success
            // Show green completion state
            sendProgress.Value = 100;
            sendProgress.IsIndeterminate = false;
            sendProgress.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            sendProgressDetail.Text = "Complete";
            sendEtaText.Text = "";
            sendSpeedText.Text = ""; sendSpeedRow.Visibility = Visibility.Collapsed;
            sendElapsedText.Text = "";
            sendProgressPanel.Visibility = Visibility.Visible;

            sendStatus.Text = "";
            ClearTaskbarProgress();
            ShowInfoBar("File sent", "Transfer completed successfully", InfoBarSeverity.Success);
            PlayCompletionSound();
            var sentIconPath = selectedFiles.Count > 0 ? GetCachedFileIconPath(selectedFiles[0]) : null;
            ShowWindowsToast("File Sent", $"{sentName} · {FormatSize(totalFileSize)}", fileIconPath: sentIconPath);
            CleanupTempZip();
            codeCard.Visibility = Visibility.Collapsed;
            sendResetTimer?.Stop();
            sendResetTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            sendResetTimer.Tick += (_, _) =>
            {
                sendResetTimer?.Stop();
                sendResetTimer = null;
                if (isSending || isZipping) return; // new transfer or zip in progress — don't reset
                sendProgressPanel.Visibility = Visibility.Collapsed;
                HideConnInfo(true);
                sendProgress.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
                // Only reset file UI if still in file mode — don't disrupt text mode
                if (!isTextMode)
                {
                    selectedFiles.Clear();
                    CleanupTempZip();
                    filePreviewCard.Visibility = Visibility.Collapsed;
                    dropZone.Visibility = Visibility.Visible;
                    textModeBtn.IsHitTestVisible = true;
                    textModeBtn.Opacity = 1;
                    sendBtn.IsEnabled = false;
                    sendStatus.Text = "";
                }
                else
                {
                    selectedFiles.Clear();
                    CleanupTempZip();
                }
            };
            sendResetTimer.Start();
        }
        else if (exitCode != 0 && sendStatus.Text != "Transfer cancelled" && !skipSendErrorHandling)
        {
            ClearTaskbarProgress();
            retryRelayOverride = null; // reset fallback relay on failure
            sendRetryCount = 0; // reset so next manual Resume attempt gets a fresh retry budget
            var sentName = selectedFiles.Count == 1 ? Path.GetFileName(selectedFiles[0]) : $"{selectedFiles.Count} files";
            AddHistoryRecord(sentName, "Sent", FormatSize(totalFileSize), "Failed");
            if (codeExpired)
            {
                // The relay closed the idle channel — the code the receiver has is no longer valid
                codeCard.Visibility = Visibility.Collapsed;
                sendStatus.Text = "";
                ShowInfoBar("Code expired", "No receiver connected in time. Click Send to get a new code and share it again.", InfoBarSeverity.Warning, 10000);
            }
            else
            {
                string rawErr;
                lock (sendErrors) rawErr = sendErrors.Count > 0 ? string.Join("; ", sendErrors) : "";
                if (string.IsNullOrEmpty(rawErr))
                    lock (lastSendLines) rawErr = lastSendLines.LastOrDefault(l => !ProgressRegex.IsMatch(l)) ?? lastSendLines.LastOrDefault() ?? "";

                var errLower = rawErr.ToLowerInvariant();
                string friendlyMsg;
                if (errLower.Contains("connection refused") || errLower.Contains("actively refused"))
                    friendlyMsg = "Connection was blocked. Windows Firewall may be blocking the transfer engine.";
                else if (errLower.Contains("no such host") || errLower.Contains("could not resolve"))
                    friendlyMsg = "Could not reach the relay server. Check your internet connection.";
                else if (errLower.Contains("being used by another process"))
                    friendlyMsg = "A file is locked by another application. Close any programs using it and try again.";
                else if (errLower.Contains("hash mismatch") || errLower.Contains("integrity") ||
                         errLower.Contains("checksum") || errLower.Contains("corrupted"))
                    friendlyMsg = "A file was modified during transfer. Close any applications editing the files and try again.";
                else if (errLower.Contains("cannot find the file") || errLower.Contains("no such file") ||
                         errLower.Contains("cannot find the path") || errLower.Contains("system cannot find"))
                    friendlyMsg = "A file was moved or deleted. Re-add the files and try again.";
                else if (string.IsNullOrEmpty(rawErr) || errLower.Contains("exit code"))
                    friendlyMsg = _directP2pHelper != null
                        ? "Connection to receiver was lost. Click Resume to try again — you'll get a new code to share with the receiver."
                        : "Connection to receiver was lost. Click Resume to try again with the same code, or Send to generate a new code.";
                else
                    friendlyMsg = _directP2pHelper != null
                        ? $"{rawErr}. Click Resume to try again — you'll get a new code to share."
                        : $"{rawErr}. Click Resume to try again.";

                sendStatus.Text = "";
                ShowInfoBar("Transfer failed", friendlyMsg, InfoBarSeverity.Error, 10000);
                Log($"Send failed: raw=[{rawErr}] friendly=[{friendlyMsg}]");
                if (lastSendFiles.Count > 0)
                    AnimateFadeIn(sendRetryPanel);
            }
        }

        _sendConPtyPid = 0; sendProc?.Dispose(); sendProc = null;
        CleanupDirectP2p();
    }

    // ── Direct P2P cleanup ────────────────────────────────

    private void CleanupDirectP2p()
    {
        if (_directP2pHelper != null)
        {
            _directP2pHelper.NetworkChanged -= OnDirectP2pNetworkChanged;
            _directP2pHelper.RelayDied -= OnDirectP2pRelayDied;
            _directP2pHelper.Dispose();
            _directP2pHelper = null;
        }
        _directSetupCts?.Dispose();
        _directSetupCts = null;
        _clipboardClearTimer?.Stop();
        _clipboardClearTimer = null;
        _currentRelayOverride = null;
        _shareCode = null;
        codeCardLabel.Text = "Share this code with the receiver";
        directP2pStatusToggle.Visibility = Visibility.Collapsed;
        directP2pStatusCard.Visibility = Visibility.Collapsed;
        sendPublicIpLabel.Visibility = Visibility.Collapsed;
        sendPublicIpText.Visibility = Visibility.Collapsed;
    }

    // ── Direct P2P — relay address encryption (AES-256-GCM) ─────────────────
    // The relay address (ip:port) is encrypted with AES-256-GCM using the croc
    // code as key. This provides genuine cryptographic IP privacy — the address
    // is unrecoverable without the croc code — and tamper protection via the
    // GCM authentication tag.

    /// <summary>
    /// Encrypts the relay address (ip:port) using AES-256-GCM keyed by the croc code.
    /// This provides genuine IP privacy — the address is unreadable without the croc code —
    /// AND tamper protection — an attacker cannot swap in a different relay address
    /// without the GCM authentication tag failing.
    /// </summary>
    private static string EncodeRelayForCode(string ip, int port, string crocCode)
    {
        // Use bracket notation for IPv6 addresses to avoid ambiguity when splitting on ':'
        var host = ip.Contains(':') ? $"[{ip}]" : ip;
        var plaintext = System.Text.Encoding.UTF8.GetBytes($"{host}:{port}");
        var key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(crocCode));
        // Deterministic nonce derived from key — safe because each croc code is unique per transfer
        var nonce = SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(crocCode + "\0VIA-NONCE"))[..12];

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Encode as base64url: ciphertext + tag
        var combined = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(combined, 0);
        tag.CopyTo(combined, ciphertext.Length);
        return Convert.ToBase64String(combined)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// Decrypts the relay address using the croc code.
    /// Returns null if decryption fails (wrong code, tampered data, or malformed input).
    /// </summary>
    private static string? TryDecodeRelayFromCode(string encoded, string crocCode)
    {
        try
        {
            var b64 = encoded.Replace('-', '+').Replace('_', '/');
            b64 += (b64.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            var combined = Convert.FromBase64String(b64);
            if (combined.Length <= 16) return null; // need ciphertext + 16-byte tag

            var key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(crocCode));
            var nonce = SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(crocCode + "\0VIA-NONCE"))[..12];

            var ciphertext = combined[..^16];
            var tag = combined[^16..];
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return System.Text.Encoding.UTF8.GetString(plaintext);
        }
        catch { return null; }
    }

    // ── Direct P2P — network change handler ─────────────────────────────────

    private void OnDirectP2pNetworkChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!isSending) return; // transfer already ended
            Log("Network changed during Direct P2P — share code is now invalid, aborting send");

            // Kill the send process — the port mappings and public IP are now stale
            KillSendProc();
            // KillSendProc triggers OnSendDone which resets UI state

            ShowInfoBar("Network changed — transfer aborted",
                "Your network address changed. The share code is no longer valid. Please send again to generate a new code.",
                Wpf.Ui.Controls.InfoBarSeverity.Error, 10000);
        });
    }

    // ── Direct P2P — relay death handler ────────────────────────────────────

    private void OnDirectP2pRelayDied(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!isSending) return; // transfer already ended
            Log("Local relay process died — aborting send");

            // Kill the croc send process — it can't work without the relay
            KillSendProc();
            // KillSendProc triggers OnSendDone which resets UI state

            ShowInfoBar("Direct Transfer relay stopped",
                "The local relay process exited unexpectedly. The transfer has been cancelled. Try sending again.",
                Wpf.Ui.Controls.InfoBarSeverity.Error, 10000);
        });
    }

    // ── IP masking for logs ─────────────────────────────────────────────────

    /// <summary>Masks an IP address for log privacy. IPv4: "192.168.*.*:9009", IPv6: "[2001:db8:*]:9009".</summary>
    private static string MaskIp(string relayOrIp)
    {
        try
        {
            // IPv6 bracket notation: [addr]:port
            if (relayOrIp.StartsWith('['))
            {
                var closeBracket = relayOrIp.IndexOf(']');
                if (closeBracket > 1)
                {
                    var ipv6 = relayOrIp[1..closeBracket];
                    var port = closeBracket < relayOrIp.Length - 1 ? relayOrIp[(closeBracket + 1)..] : "";
                    // Show first two groups, mask the rest
                    var groups = ipv6.Split(':');
                    if (groups.Length >= 2)
                        return $"[{groups[0]}:{groups[1]}:*]{port}";
                }
                return "[*]";
            }
            // IPv4: host:port
            var colon = relayOrIp.LastIndexOf(':');
            var ip = colon > 0 ? relayOrIp[..colon] : relayOrIp;
            var portSuffix = colon > 0 ? relayOrIp[colon..] : "";
            var parts = ip.Split('.');
            if (parts.Length == 4)
                return $"{parts[0]}.{parts[1]}.*.*{portSuffix}";
        }
        catch { }
        return "***";
    }

    // ── Direct P2P status checklist ─────────────────────────────────────────

    private void DirectP2pStatusToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (directP2pStatusCard.Visibility == Visibility.Visible)
        {
            AnimateCollapseSection(directP2pStatusCard);
            AnimateChevron(directP2pChevron, false);
        }
        else
        {
            AnimateExpandSection(directP2pStatusCard);
            AnimateChevron(directP2pChevron, true);
        }
    }

    private void UpdateDirectP2pStatusCard()
    {
        if (!_isDirectMode || _directP2pHelper == null)
        {
            directP2pStatusToggle.Visibility = Visibility.Collapsed;
            directP2pStatusCard.Visibility = Visibility.Collapsed;
            return;
        }

        var h = _directP2pHelper;
        var lines = new System.Text.StringBuilder();

        // 1. Relay
        lines.AppendLine("\u2705  Local relay running (port 9009)");

        // 2. Port mapping / IPv6
        if (h.UsingIpv6)
        {
            lines.AppendLine("\u2705  IPv6 globally routable (no port mapping needed)");
        }
        else if (h.IsLanOnly)
        {
            if (h.IsBehindCgnat)
                lines.AppendLine("\u274C  CGNAT detected \u2014 internet connections blocked by ISP");
            else if (h.RestrictedNetwork)
                lines.AppendLine("\u26A0\uFE0F  Restricted network \u2014 guest/public WiFi blocks port mapping");
            else
                lines.AppendLine("\u26A0\uFE0F  Router ports not mapped \u2014 LAN only");
        }
        else
            lines.AppendLine($"\u2705  Router ports mapped ({h.IpSource})");

        // 3. Public IP
        if (!string.IsNullOrEmpty(h.PublicIp))
        {
            if (h.IsBehindCgnat)
                lines.AppendLine($"\u274C  External IP is not routable ({h.IpSource})");
            else if (h.UsingIpv6)
                lines.AppendLine($"\u2705  IPv6 address resolved");
            else if (h.IsLanOnly)
                lines.AppendLine($"\u2705  LAN IP resolved ({h.IpSource})");
            else
                lines.AppendLine($"\u2705  Public IP resolved ({h.IpSource})");
        }

        // 4. Firewall
        if (h.FirewallRuleFailed)
            lines.AppendLine("\u26A0\uFE0F  Firewall rule failed \u2014 may need admin rights");
        else
            lines.AppendLine("\u2705  Firewall rule added");

        // 5. VPN
        if (h.VpnDetected)
            lines.AppendLine("\u26A0\uFE0F  VPN detected \u2014 may route through VPN tunnel");
        else
            lines.AppendLine("\u2705  No VPN detected");

        // 6. Encryption
        lines.AppendLine("\u2705  End-to-end encrypted (PAKE + AES-256-GCM)");

        // 7. IP privacy
        lines.Append("\u2705  Sender IP encrypted in share code (AES-256-GCM)");

        directP2pChecklist.Text = lines.ToString();
        directP2pStatusToggle.Visibility = Visibility.Visible;
        AnimateChevron(directP2pChevron, false);
        directP2pStatusCard.Visibility = Visibility.Collapsed;
    }

    // ── Clipboard clear timer ────────────────────────────────────────────────

    private void StartClipboardClearTimer()
    {
        _clipboardClearTimer?.Stop();
        _clipboardClearTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(90)
        };
        _clipboardClearTimer.Tick += (_, _) =>
        {
            _clipboardClearTimer?.Stop();
            // Only clear if the clipboard still holds our code
            try
            {
                if (Clipboard.ContainsText() &&
                    Clipboard.GetText() == (_shareCode ?? currentCode))
                    Clipboard.Clear();
            }
            catch { }
        };
        _clipboardClearTimer.Start();
    }

    private void Send_Click(object sender, RoutedEventArgs e) => StartSend(false);

    private void StartSendText(string text, bool isRetry = false)
    {
        // Clear stale retry CTS so cancel correctly enters process-kill path
        sendRetryCts?.Dispose();
        sendRetryCts = null;
        isSending = true;
        sendErrors.Clear();
        lastSendLines.Clear();
        if (!isRetry) { currentCode = null; sendRetryCount = 0; }
        _lastSendText = text;
        sendBtn.IsEnabled = false;
        sendTextBox.IsReadOnly = true;
        fileModeBtn.IsHitTestVisible = false;
        fileModeBtn.Opacity = 0.35;
        sendRetryPanel.Visibility = Visibility.Collapsed;
        sendCancelBtn.Visibility = Visibility.Visible;
        sendProgressPanel.Visibility = Visibility.Visible;
        sendProgress.IsIndeterminate = true;
        sendProgressDetail.Text = "";
        sendEtaText.Text = "";
        sendSpeedText.Text = ""; sendSpeedRow.Visibility = Visibility.Collapsed;
        sendElapsedText.Text = "";
        SetTaskbarIndeterminate();
        codeCard.Visibility = Visibility.Collapsed;
        sendStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        StartWaitingDots(sendStatus, "Connecting");
        StartElapsedTimer(true);
        StartConnectTimeout(true);
        ShowConnInfo(true);

        var textPsi = new ProcessStartInfo
        {
            FileName = crocPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        textPsi.ArgumentList.Add("--yes");
        AddCrocGlobalFlags(textPsi.ArgumentList);
        textPsi.ArgumentList.Add("send");
        // Send subcommand flags after "send"
        if (!string.IsNullOrEmpty(currentCode)) { textPsi.ArgumentList.Add("--code"); textPsi.ArgumentList.Add(currentCode); }
        textPsi.ArgumentList.Add("--text");
        textPsi.ArgumentList.Add(text);
        AddCrocSendFlags(textPsi.ArgumentList);
        sendProc = new Process { StartInfo = textPsi, EnableRaisingEvents = true };

        var textSendProcLocal = sendProc;
        sendProc.Exited += (_, _) =>
        {
            try { textSendProcLocal.WaitForExit(); } catch { }
            var ec = -1;
            try { ec = textSendProcLocal.ExitCode; } catch { }
            try { Dispatcher.BeginInvoke(() => { if (!isClosing) OnTextSendDone(ec); }); } catch { }
        };

        try
        {
            sendProc.Start();
            ReadStreamAsync(sendProc.StandardOutput, ParseSendOutput, sendErrors, lastSendLines);
            ReadStderrAsync(sendProc, ParseSendOutput, sendErrors, lastSendLines);
        }
        catch (Exception ex)
        {
            isSending = false;
            sendTextBox.IsReadOnly = false;
            fileModeBtn.IsHitTestVisible = true;
            fileModeBtn.Opacity = 1;
            sendBtn.IsEnabled = isTextMode || selectedFiles.Count > 0;
            sendCancelBtn.Visibility = Visibility.Collapsed;
            sendProgressPanel.Visibility = Visibility.Collapsed;
            ShowInfoBar("Error", ex.Message, InfoBarSeverity.Error);
            sendProc = null;
        }
    }

    private void OnTextSendDone(int exitCode)
    {
        StopWaitingDots();
        StopElapsedTimer();
        StopConnectTimeout();
        HideConnInfo(true);

        // Auto-retry on transient failure (same pattern as file sends)
        if (exitCode != 0 && sendStatus.Text != "Transfer cancelled" && !_sendConnTimedOut)
        {
            bool retryable;
            lock (sendErrors) { lock (lastSendLines) { retryable = IsRetryableError(sendErrors, lastSendLines); } }

            if (sendRetryCount < MaxAutoRetries && retryable && !string.IsNullOrEmpty(_lastSendText))
            {
                sendRetryCount++;
                var delay = (int)Math.Pow(2, sendRetryCount) * 1000;
                isSending = false;
                sendProc?.Dispose(); sendProc = null;
                sendStatus.Text = $"Connection lost. Retrying ({sendRetryCount}/{MaxAutoRetries})...";
                sendStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
                sendProgressPanel.Visibility = Visibility.Visible;
                sendProgress.IsIndeterminate = true;
                sendProgressDetail.Text = "";
                sendEtaText.Text = "";
                sendSpeedText.Text = ""; sendSpeedRow.Visibility = Visibility.Collapsed;
                Log($"Text send auto-retry {sendRetryCount}/{MaxAutoRetries} in {delay}ms");

                sendRetryCts?.Dispose();
                sendRetryCts = new CancellationTokenSource();
                var token = sendRetryCts.Token;
                var textToRetry = _lastSendText;
                _ = Task.Delay(delay, token).ContinueWith(_ =>
                {
                    if (!token.IsCancellationRequested)
                        Dispatcher.BeginInvoke(() => { if (!isClosing) StartSendText(textToRetry, isRetry: true); });
                }, TaskScheduler.Default);
                return;
            }
            // Primary retries exhausted — try fallback relay once if configured
            if (retryable && !string.IsNullOrEmpty(appConfig.FallbackRelayServer) && retryRelayOverride == null
                && !string.IsNullOrEmpty(_lastSendText))
            {
                retryRelayOverride = appConfig.FallbackRelayServer;
                sendRetryCount = 0;
                isSending = false;
                sendProc?.Dispose(); sendProc = null;
                sendStatus.Text = "Trying fallback relay...";
                sendStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
                sendProgressPanel.Visibility = Visibility.Visible;
                sendProgress.IsIndeterminate = true;
                sendProgressDetail.Text = "";
                sendEtaText.Text = "";
                sendSpeedText.Text = ""; sendSpeedRow.Visibility = Visibility.Collapsed;
                Log($"Text send switching to fallback relay: {retryRelayOverride}");
                sendRetryCts?.Dispose();
                sendRetryCts = new CancellationTokenSource();
                var fbToken = sendRetryCts.Token;
                var fbText = _lastSendText;
                _ = Task.Delay(2000, fbToken).ContinueWith(_ =>
                {
                    if (!fbToken.IsCancellationRequested)
                        Dispatcher.BeginInvoke(() => { if (!isClosing) StartSendText(fbText, isRetry: true); });
                }, TaskScheduler.Default);
                return;
            }
        }

        sendCancelBtn.Visibility = Visibility.Collapsed;
        sendProgressPanel.Visibility = Visibility.Collapsed;
        isSending = false;
        sendTextBox.IsReadOnly = false;
        fileModeBtn.IsHitTestVisible = true;
        fileModeBtn.Opacity = 1;
        sendBtn.IsEnabled = isTextMode || selectedFiles.Count > 0;

        ClearTaskbarProgress();

        if (exitCode == 0)
        {
            sendRetryCts?.Dispose(); sendRetryCts = null;
            sendStatus.Text = "";
            codeCard.Visibility = Visibility.Collapsed;
            ShowInfoBar("Text sent!", "The text was delivered successfully.", InfoBarSeverity.Success, 5000);
            AddHistoryRecord("(text)", "Sent", "—", "Success");
            ShowWindowsToast("Text Sent", "Your message was delivered successfully.");
        }
        else
        {
            if (_sendConnTimedOut)
            {
                _sendConnTimedOut = false;
                retryRelayOverride = null;
                sendStatus.Text = "";
                codeCard.Visibility = Visibility.Collapsed;
                AddHistoryRecord("(text)", "Sent", "—", "Failed");
                sendProc = null;
                return; // InfoBar already shown by timeout handler
            }
            codeCard.Visibility = Visibility.Collapsed;
            string rawErr;
            lock (sendErrors) rawErr = sendErrors.Count > 0 ? string.Join("; ", sendErrors) : null!;
            if (string.IsNullOrEmpty(rawErr))
                lock (lastSendLines) rawErr = lastSendLines.LastOrDefault(l => !ProgressRegex.IsMatch(l)) ?? lastSendLines.LastOrDefault() ?? $"Exit code {exitCode}";

            // Map raw croc errors to friendly messages (same patterns as file send)
            var errLower = rawErr.ToLowerInvariant();
            string friendlyMsg;
            if (errLower.Contains("connection refused") || errLower.Contains("actively refused"))
                friendlyMsg = "Connection was blocked. Windows Firewall may be blocking the transfer engine.";
            else if (errLower.Contains("no such host") || errLower.Contains("could not resolve"))
                friendlyMsg = "Could not reach the relay server. Check your internet connection.";
            else if (errLower.Contains("wrong passphrase"))
                friendlyMsg = "The passphrase doesn't match. Both sides must have the same passphrase set in Settings → Security → Passphrase.";
            else if (string.IsNullOrEmpty(rawErr) || errLower.Contains("exit code"))
                friendlyMsg = "Connection to receiver was lost. Try sending the text again.";
            else
                friendlyMsg = $"{rawErr}. Try sending again.";

            sendStatus.Text = "";
            sendRetryCount = 0;
            AddHistoryRecord("(text)", "Sent", "—", "Failed");
            Log($"Text send failed: raw=[{rawErr}] friendly=[{friendlyMsg}]");
            ShowInfoBar("Text send failed", friendlyMsg, InfoBarSeverity.Error, 8000);
        }
        sendProc = null;
    }

    private void SendResume_Click(object sender, RoutedEventArgs e)
    {
        if (lastSendFiles.Count == 0)
        {
            ShowInfoBar("Nothing to resume", "No previous transfer to resume. Start a new transfer.", InfoBarSeverity.Warning, 3000);
            sendRetryPanel.Visibility = Visibility.Collapsed;
            return;
        }
        // Validate files still exist before resuming
        var missing = lastSendFiles.Where(f => !File.Exists(f) && !Directory.Exists(f)).ToList();
        if (missing.Count > 0)
        {
            ShowInfoBar("Cannot resume", $"{Path.GetFileName(missing[0])} was moved or deleted. Start a new transfer instead.", InfoBarSeverity.Error);
            sendRetryPanel.Visibility = Visibility.Collapsed;
            return;
        }
        selectedFiles.Clear();
        selectedFiles.AddRange(lastSendFiles);
        StartSend(true);
    }

    private void SendStartOver_Click(object sender, RoutedEventArgs e)
    {
        sendRetryPanel.Visibility = Visibility.Collapsed;
        sendStatus.Text = "";
    }

    // ── Unfinished transfer detection on startup ──────────────────

    private void CheckUnfinishedTransfer()
    {
        // Only show if we have a saved code and file paths from a previous session
        if (string.IsNullOrEmpty(appConfig.LastSendCode) || appConfig.LastSendFilePaths.Count == 0)
            return;

        // Check if the saved paths were temp zips (which CleanupStaleTempFiles already deleted)
        var allTempZips = appConfig.LastSendFilePaths.All(f =>
            f.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
            f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (allTempZips)
        {
            // Temp zips were cleaned up — resume is impossible, clear stale data silently
            appConfig.LastSendCode = "";
            appConfig.LastSendFilePaths.Clear();
            SaveSettings();
            Log("Cleared stale resume state: temp zip was cleaned up");
            return;
        }

        // Check if the original files still exist
        var missing = appConfig.LastSendFilePaths.Where(f => !File.Exists(f) && !Directory.Exists(f)).ToList();
        if (missing.Count == appConfig.LastSendFilePaths.Count)
        {
            // All files gone — resume impossible, clear silently
            appConfig.LastSendCode = "";
            appConfig.LastSendFilePaths.Clear();
            SaveSettings();
            Log("Cleared stale resume state: all files missing");
            return;
        }

        // We have valid files and a code — show the banner
        var validFiles = appConfig.LastSendFilePaths.Where(f => File.Exists(f) || Directory.Exists(f)).ToList();
        string detail;
        if (validFiles.Count == 1)
        {
            var name = Path.GetFileName(validFiles[0]);
            try
            {
                var size = File.Exists(validFiles[0]) ? FormatSize(new FileInfo(validFiles[0]).Length) : "folder";
                detail = $"{name} ({size}) · Code: {appConfig.LastSendCode}";
            }
            catch { detail = $"{name} · Code: {appConfig.LastSendCode}"; }
        }
        else
        {
            detail = $"{validFiles.Count} files · Code: {appConfig.LastSendCode}";
        }

        if (missing.Count > 0)
            detail += $" · {missing.Count} file(s) missing";

        resumeBannerDetail.Text = detail;
        AnimateCardIn(resumeBanner);
        Log($"Showing resume banner: code={appConfig.LastSendCode}, files={validFiles.Count}");
    }

    private void ResumeBanner_Resume_Click(object sender, RoutedEventArgs e)
    {
        resumeBanner.Visibility = Visibility.Collapsed;

        if (string.IsNullOrEmpty(appConfig.LastSendCode) || appConfig.LastSendFilePaths.Count == 0)
        {
            ShowInfoBar("Nothing to resume", "No previous transfer data found.", InfoBarSeverity.Warning, 3000);
            return;
        }

        // Validate files still exist
        var valid = appConfig.LastSendFilePaths.Where(f => File.Exists(f) || Directory.Exists(f)).ToList();
        if (valid.Count == 0)
        {
            ShowInfoBar("Cannot resume", "All files were moved or deleted. Start a new transfer.", InfoBarSeverity.Error);
            appConfig.LastSendCode = "";
            appConfig.LastSendFilePaths.Clear();
            SaveSettings();
            return;
        }

        // If some files are missing, warn but proceed with what we have
        var missing = appConfig.LastSendFilePaths.Except(valid).ToList();
        if (missing.Count > 0)
        {
            ShowInfoBar("Partial resume", $"{missing.Count} file(s) were moved or deleted. Resuming with {valid.Count} remaining.", InfoBarSeverity.Warning, 5000);
        }

        // Restore state and start send with resume
        selectedFiles.Clear();
        selectedFiles.AddRange(valid);
        lastSendFiles.Clear();
        lastSendFiles.AddRange(valid);
        currentCode = appConfig.LastSendCode;

        // Show the file preview
        if (valid.Count == 1)
        {
            var name = Path.GetFileName(valid[0]);
            try
            {
                if (File.Exists(valid[0]))
                {
                    var info = new FileInfo(valid[0]);
                    var typeDesc = GetFileTypeDescription(valid[0]);
                    ShowSendFilePreview(name, $"{FormatSize(info.Length)}  •  {typeDesc}", valid[0]);
                }
                else
                {
                    ShowSendFilePreview(name, "Folder", valid[0]);
                }
            }
            catch { ShowSendFilePreview(name, "", valid[0]); }
        }
        else
        {
            var firstName = Path.GetFileName(valid[0]);
            long totalSize = 0;
            try { totalSize = valid.Where(File.Exists).Sum(f => new FileInfo(f).Length); } catch { }
            ShowSendFilePreview($"{firstName} +{valid.Count - 1} more",
                $"{FormatSize(totalSize)}  •  {valid.Count} files", valid[0]);
        }

        StartSend(true);
    }

    private void ResumeBanner_Dismiss_Click(object sender, RoutedEventArgs e)
    {
        resumeBanner.Visibility = Visibility.Collapsed;
        appConfig.LastSendCode = "";
        appConfig.LastSendFilePaths.Clear();
        SaveSettings();
        Log("User dismissed resume banner — cleared saved transfer state");
    }

    private void SendCancel_Click(object sender, RoutedEventArgs e)
    {
        // Cancel Direct P2P setup that is currently running (no croc process yet)
        if (_directSetupCts != null && !_directSetupCts.IsCancellationRequested)
        {
            _directSetupCts.Cancel();
            // isSending=false tells the awaiting StartSend to abort and call CleanupDirectP2p()
            isSending = false;
            StopWaitingDots();
            sendCancelBtn.Visibility = Visibility.Collapsed;
            sendProgressPanel.Visibility = Visibility.Collapsed;
            sendStatus.Text = "Transfer cancelled";
            sendStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
            UpdateClearButtonVisibility();
            sendBtn.IsEnabled = selectedFiles.Count > 0;
            HideConnInfo(true);
            ClearTaskbarProgress();
            return;
        }

        // Cancel pending auto-retry
        if (sendRetryCts != null)
        {
            sendRetryCts.Cancel();
            sendRetryCts.Dispose();
            sendRetryCts = null;
            retryRelayOverride = null;
            sendCancelBtn.Visibility = Visibility.Collapsed;
            sendProgressPanel.Visibility = Visibility.Collapsed;
            codeCard.Visibility = Visibility.Collapsed;
            sendStatus.Text = "Transfer cancelled";
            sendStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
            isSending = false;
            if (isTextMode) { sendTextBox.IsReadOnly = false; fileModeBtn.IsHitTestVisible = true; fileModeBtn.Opacity = 1; }
            UpdateClearButtonVisibility();
            sendBtn.IsEnabled = isTextMode || selectedFiles.Count > 0;
            HideConnInfo(true);
            ClearTaskbarProgress();
            CleanupDirectP2p();
            return;
        }

        if ((sendProc == null || sendProc.HasExited) && _sendConPtyPid == 0)
        {
            // Process already exited — OnSendDone may not fire, so clean up here
            sendCancelBtn.Visibility = Visibility.Collapsed;
            sendProgressPanel.Visibility = Visibility.Collapsed;
            codeCard.Visibility = Visibility.Collapsed;
            isSending = false;
            if (isTextMode) { sendTextBox.IsReadOnly = false; fileModeBtn.IsHitTestVisible = true; fileModeBtn.Opacity = 1; }
            UpdateClearButtonVisibility();
            sendBtn.IsEnabled = isTextMode ? !string.IsNullOrWhiteSpace(sendTextBox.Text) : selectedFiles.Count > 0;
            HideConnInfo(true);
            ClearTaskbarProgress();
            CleanupDirectP2p();
            return;
        }

        if (lastSendPercent > 50)
        {
            var result = MessageBox.Show(
                $"Transfer is {lastSendPercent:F0}% complete. Cancel anyway?",
                "Cancel Transfer", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes) return;
        }

        StopWaitingDots();
        ClearTaskbarProgress();
        sendStatus.Text = "Transfer cancelled";
        sendStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        codeCard.Visibility = Visibility.Collapsed;
        retryRelayOverride = null;
        KillSendProc();
        // KillSendProc triggers OnSendDone which will set isSending=false and show clearFileBtn
    }

    // ── Copy Code ────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _copyResetTimer;

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (currentCode == null) return;
        Clipboard.SetText(_shareCode ?? currentCode);
        if (_isDirectMode && _directP2pHelper != null)
            StartClipboardClearTimer();
        copyIcon.Symbol = SymbolRegular.Checkmark24;
        copyText.Text = "Copied!";
        _copyResetTimer?.Stop();
        _copyResetTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _copyResetTimer.Tick += (_, _) =>
        {
            copyIcon.Symbol = SymbolRegular.Copy24;
            copyText.Text = "Copy";
            _copyResetTimer?.Stop();
            _copyResetTimer = null;
        };
        _copyResetTimer.Start();
    }

    // ── Browse Folder ────────────────────────────────────

    private void SaveFolder_LostFocus(object sender, RoutedEventArgs e)
    {
        var path = saveFolderBox.Text.Trim();
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            saveFolder = path;
            SaveSettings();
        }
        else
        {
            // Revert to current save folder if typed path is invalid or blank
            saveFolderBox.Text = saveFolder ?? GetDefaultSaveFolder();
            if (!string.IsNullOrEmpty(path))
                ShowInfoBar("Invalid path", "That folder doesn't exist", InfoBarSeverity.Warning, 3000);
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = saveFolder ?? ""
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            saveFolder = dialog.SelectedPath;
            saveFolderBox.Text = saveFolder;
            SaveSettings();
        }
    }

    // ── Receive ──────────────────────────────────────────

    private static readonly Regex ValidCodeRegex = new(@"^[\w\-]+$", RegexOptions.Compiled);

    private void StartRecv(string code, bool resume = false)
    {
        if (isReceiving) return;
        if (string.IsNullOrEmpty(code)) return;

        // Parse Direct P2P codes: "d:AES_ENCRYPTED_RELAY:croccode"
        // The relay address (ip:port) is AES-256-GCM encrypted using the croc code as key.
        // Re-parse whenever _currentRelayOverride is null — this covers first attempt AND
        // manual Resume (where OnRecvDone cleared _currentRelayOverride).
        // Auto-retries skip this because _currentRelayOverride is still set (they return
        // early from OnRecvDone before the clear) AND lastRecvCode has no d: prefix.
        if (_currentRelayOverride == null && code.StartsWith("d:", StringComparison.OrdinalIgnoreCase))
        {
            var dparts = code.Split(':', 3);
            if (dparts.Length == 3 &&
                !string.IsNullOrEmpty(dparts[1]) &&
                !string.IsNullOrEmpty(dparts[2]))
            {
                var relay = TryDecodeRelayFromCode(dparts[1], dparts[2]);
                if (relay != null)
                {
                    // Parse host:port — supports both IPv4 "1.2.3.4:9009" and IPv6 "[::1]:9009"
                    string? relayHost = null;
                    int relayPort = 0;
                    bool relayOk = false;
                    if (relay.StartsWith('['))
                    {
                        // IPv6 bracket notation: [addr]:port
                        var closeBracket = relay.IndexOf(']');
                        if (closeBracket > 1 && closeBracket < relay.Length - 2 && relay[closeBracket + 1] == ':')
                        {
                            relayHost = relay[1..closeBracket];
                            relayOk = System.Net.IPAddress.TryParse(relayHost, out _) &&
                                      int.TryParse(relay[(closeBracket + 2)..], out relayPort) &&
                                      relayPort is >= 1024 and <= 65535;
                        }
                    }
                    else
                    {
                        // IPv4: host:port
                        var rparts = relay.Split(':');
                        if (rparts.Length == 2)
                        {
                            relayHost = rparts[0];
                            relayOk = System.Net.IPAddress.TryParse(relayHost, out _) &&
                                      int.TryParse(rparts[1], out relayPort) &&
                                      relayPort is >= 1024 and <= 65535;
                        }
                    }
                    if (relayOk && relayHost != null)
                    {
                        _currentRelayOverride = relay;
                        code = dparts[2]; // the real croc code
                        Log($"Direct P2P receive: relay={MaskIp(_currentRelayOverride)} code=***");

                        // Measure connection quality to sender's relay
                        var probeHost = relayHost;
                        var probePort = relayPort;
                        _ = Task.Run(async () =>
                        {
                            var latency = await DirectP2pHelper.ProbeRelayLatencyAsync(probeHost, probePort);
                            _directP2pLatencyMs = latency;
                            var label = DirectP2pHelper.LatencyLabel(latency);
                            Log($"Direct P2P latency: {(latency >= 0 ? $"{latency}ms ({label})" : "unreachable")}");
                            // Update connection info on UI thread so latency shows immediately
                            Dispatcher.BeginInvoke(() => { if (isReceiving) ShowConnInfo(false); });
                        });
                    }
                    else
                    {
                        ShowInfoBar("Invalid code", "The Direct Transfer code contains an invalid address. Ask the sender for a new code.", InfoBarSeverity.Error, 8000);
                        return;
                    }
                }
                else
                {
                    ShowInfoBar("Invalid code", "Could not decrypt the Direct Transfer code. Check for typos — the code must be copied exactly as shown by the sender.", InfoBarSeverity.Error, 8000);
                    return;
                }
            }
            else
            {
                ShowInfoBar("Invalid code", "This Direct Transfer code appears incomplete. Make sure you copied the entire code.", InfoBarSeverity.Error, 8000);
                return;
            }
        }

        // Clear stale retry CTS so cancel correctly enters process-kill path
        recvRetryCts?.Dispose();
        recvRetryCts = null;
        if (crocPath == null || !File.Exists(crocPath))
        {
            ExtractCroc();
            if (crocPath == null || !File.Exists(crocPath))
            {
                ShowInfoBar("Engine not ready", "The transfer engine could not be loaded. Check if antivirus is blocking it, or restart VIA x4.", InfoBarSeverity.Error, 10000);
                _currentRelayOverride = null;
                return;
            }
        }

        // Sanitize: croc codes are alphanumeric words separated by hyphens
        if (code.Length > 100 || !ValidCodeRegex.IsMatch(code))
        {
            ShowInfoBar("Invalid code", "Transfer codes can only contain letters, numbers, and hyphens", InfoBarSeverity.Warning, 5000);
            _currentRelayOverride = null;
            return;
        }
        // Close overlays when starting a transfer
        settingsPanel.Visibility = Visibility.Collapsed;
        historyPanel.Visibility = Visibility.Collapsed;

        // Validate save folder exists, fall back to Downloads
        if (saveFolder != null && !Directory.Exists(saveFolder))
        {
            saveFolder = GetDefaultSaveFolder();
            saveFolderBox.Text = saveFolder;
            SaveSettings();
            ShowInfoBar("Save folder reset", "Previous save folder no longer exists. Using Downloads.", InfoBarSeverity.Warning, 5000);
        }

        // Check disk space on save drive
        try
        {
            var drivePath = Path.GetPathRoot(saveFolder ?? GetDefaultSaveFolder());
            if (drivePath != null)
            {
                var drive = new DriveInfo(drivePath);
                if (drive.AvailableFreeSpace < 100 * 1024 * 1024) // less than 100 MB
                {
                    ShowInfoBar("Low disk space", $"Only {FormatSize(drive.AvailableFreeSpace)} free on {drive.Name}. The transfer may fail.", InfoBarSeverity.Warning, 8000);
                }
            }
        }
        catch { }

        recvResetTimer?.Stop();
        recvResetTimer = null;
        isReceiving = true;
        ++recvGeneration;
        recvErrors.Clear();
        lastRecvLines.Clear();
        lastRecvCode = code;
        if (!resume) { recvRetryCount = 0; totalRecvBytes = 0; }

        if (!resume) lastRecvPercent = 0;
        recvBtn.IsEnabled = false;
        recvCodeBox.IsEnabled = false;
        recvRetryPanel.Visibility = Visibility.Collapsed;
        recvCancelBtn.Visibility = Visibility.Visible;
        recvProgressPanel.Visibility = Visibility.Visible;
        recvProgress.IsIndeterminate = !resume;
        recvProgress.Value = resume ? lastRecvPercent : 0;
        recvProgressDetail.Text = resume ? $"Resuming from {lastRecvPercent:F0}%..." : "";
        recvEtaText.Text = "";
        recvSpeedText.Text = ""; recvSpeedRow.Visibility = Visibility.Collapsed;
        recvElapsedText.Text = "";
        SetTaskbarIndeterminate();
        recvFilePreviewCard.Visibility = Visibility.Collapsed;
        lastRecvFileName = null;
        isReceivingText = false;
        receivedText = null;
        recvTextCard.Visibility = Visibility.Collapsed;
        recvEmptyState.Visibility = Visibility.Collapsed;
        recvActivityPanel.Visibility = Visibility.Visible;
        if (resume)
        {
            recvStatus.Text = "Resuming transfer...";
        }
        else
        {
            StartWaitingDots(recvStatus, "Connecting");
        }
        recvStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        StartElapsedTimer(false);
        StartConnectTimeout(false);
        ShowConnInfo(false);

        var recvPsi = new ProcessStartInfo
        {
            FileName = crocPath!,
            WorkingDirectory = saveFolder ?? GetDefaultSaveFolder(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        recvPsi.ArgumentList.Add("--yes");
        if (appConfig.OverwriteExisting) recvPsi.ArgumentList.Add("--overwrite");
        AddCrocGlobalFlags(recvPsi.ArgumentList);
        recvPsi.ArgumentList.Add(code);
        var recvWorkDir = saveFolder ?? GetDefaultSaveFolder();
        if (TryStartWithConPty(recvPsi, recvWorkDir, line => ParseRecvOutput(line),
                recvErrors, lastRecvLines,
                ec => Dispatcher.BeginInvoke(() => { if (!isClosing) OnRecvDone(ec); }), out int rPid))
        {
            _recvConPtyPid = rPid;
            recvProc = null;
        }
        else
        {
            recvProc = new Process { StartInfo = recvPsi, EnableRaisingEvents = true };
            var recvProcLocal = recvProc;
            recvProc.Exited += (_, _) =>
            {
                try { recvProcLocal.WaitForExit(); } catch { }
                var exitCode = -1;
                try { exitCode = recvProcLocal.ExitCode; } catch { }
                try { Dispatcher.BeginInvoke(() => { if (!isClosing) OnRecvDone(exitCode); }); } catch { }
            };
            try
            {
                recvProc.Start();
                ReadStreamAsync(recvProc.StandardOutput, line => ParseRecvOutput(line), recvErrors, lastRecvLines);
                ReadStderrAsync(recvProc, line => ParseRecvOutput(line, fromStderr: true), recvErrors, lastRecvLines);
            }
            catch (Exception ex)
            {
                isReceiving = false;
                recvBtn.IsEnabled = true;
                recvCodeBox.IsEnabled = true;
                recvCancelBtn.Visibility = Visibility.Collapsed;
                recvProgressPanel.Visibility = Visibility.Collapsed;
                StopElapsedTimer();
                StopWaitingDots();
                StopConnectTimeout();
                HideConnInfo(false);
                ClearTaskbarProgress();
                recvActivityPanel.Visibility = Visibility.Collapsed;
                recvEmptyState.Visibility = Visibility.Visible;
                ShowInfoBar("Error", $"Failed to start transfer: {ex.Message}", InfoBarSeverity.Error);
                recvProc = null;
            }
        }
    }

    private static readonly Regex RecvFileRegex = new(@"Receiving '(.+?)'\s*(?:\(([^)]+)\))?", RegexOptions.Compiled);

    private void ParseRecvOutput(string line, bool fromStderr = false)
    {
        if (!isReceiving) return; // ignore queued output after cancel
        if (line.Contains("Receiving text"))
        {
            recvConnected = true;
            StopConnectTimeout();
            StopWaitingDots();
            isReceivingText = true;
            recvStatus.Text = "Receiving text...";
            UpdateConnStatus(false, "Receiving text");
            return;
        }

        // After "Receiving text", subsequent stdout lines are the actual text content
        if (!fromStderr && isReceivingText && !line.Contains("Receiving") && !ProgressRegex.IsMatch(line))
        {
            receivedText = receivedText == null ? line : receivedText + "\n" + line;
            return;
        }

        if (line.Contains("Receiving"))
        {
            recvConnected = true;
            StopConnectTimeout();
            StopWaitingDots();
            recvStatus.Text = "Negotiating encryption...";
            UpdateConnStatus(false, "Negotiating encryption");
            var fileMatch = RecvFileRegex.Match(line);
            if (fileMatch.Success)
            {
                // Path.GetFileName prevents a crafted sender from using path traversal in the name
                lastRecvFileName = Path.GetFileName(fileMatch.Groups[1].Value.Trim());
                if (fileMatch.Groups[2].Success)
                    totalRecvBytes = ParseSizeBytes(fileMatch.Groups[2].Value.Trim());
                if (totalRecvBytes > 0)
                    StartRecvPoll();

                // Show incoming file preview immediately
                recvFileName.Text = lastRecvFileName;
                recvFileSymbol.Symbol = GetFileSymbol(lastRecvFileName);
                recvFileDetail.Text = totalRecvBytes > 0 ? $"Incoming  \u2022  {FormatSize(totalRecvBytes)}" : "Incoming...";
                recvFileIcon.Source = null;
                recvFileIcon.Visibility = Visibility.Collapsed;
                recvFileActions.Visibility = Visibility.Collapsed;
                AnimateCardIn(recvFilePreviewCard);
            }
        }

        var match = ProgressRegex.Match(line);
        if (match.Success)
        {
            StopRecvPoll(); // ConPTY is providing progress — stop file-size polling
            StopWaitingDots();
            recvStatus.Text = lastRecvFileName != null ? $"Receiving {lastRecvFileName}" : "Transferring...";
            UpdateConnStatus(false, "Receiving");
            var pct = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var speed = match.Groups[2].Value + " " + match.Groups[3].Value + "/s";
            lastRecvPercent = pct;
            recvProgress.IsIndeterminate = false;
            AnimateProgress(recvProgress, pct);
            recvProgressDetail.Text = totalRecvBytes > 0
                ? $"{FormatSize((long)(totalRecvBytes * pct / 100))} / {FormatSize(totalRecvBytes)}"
                : $"{pct:F0}%";
            recvSpeedText.Text = $"↓  {speed}";
            recvEtaText.Text = FormatEta(pct, recvStartTime);
            recvSpeedRow.Visibility = Visibility.Visible;
            SetTaskbarProgress(pct);
        }
    }

    private async void OnRecvDone(int exitCode)
    {
        // A timeout retry is pending — do minimal cleanup and let the scheduled retry handle it.
        // Without this guard, we'd clear _currentRelayOverride and the retry would connect
        // to the default relay instead of the sender's Direct P2P relay.
        if (_pendingRecvTimeoutRetry)
        {
            _pendingRecvTimeoutRetry = false;
            _recvConPtyPid = 0; recvProc?.Dispose(); recvProc = null;
            return;
        }

        StopWaitingDots();
        StopElapsedTimer();
        StopRecvPoll();
        StopConnectTimeout();
        HideConnInfo(false);
        Log($"Receive completed: exitCode={exitCode}, file={lastRecvFileName ?? "(text)"}");

        // ConPTY startup crash (STATUS_DLL_INIT_FAILED) — retry with standard Process.
        if (unchecked((uint)exitCode) == 0xC0000142u && !string.IsNullOrEmpty(lastRecvCode))
        {
            recvProgressPanel.Visibility = Visibility.Collapsed;
            recvCancelBtn.Visibility = Visibility.Collapsed;
            isReceiving = false;
            _recvConPtyPid = 0; recvProc?.Dispose(); recvProc = null;
            recvStatus.Text = "";
            Log("ConPTY incompatible on this system — retrying receive with standard process");
            _ = Task.Delay(300).ContinueWith(_ =>
                Dispatcher.BeginInvoke(() => { if (!isClosing) StartRecv(lastRecvCode!, false); }),
                TaskScheduler.Default);
            return;
        }

        // Auto-retry on relay/connection dropout
        bool skipRecvErrorHandling = false;
        if (exitCode != 0 && recvStatus.Text != "Transfer cancelled")
        {
            if (_recvConnTimedOut)
            {
                _recvConnTimedOut = false;
                skipRecvErrorHandling = true; // InfoBar already shown by timeout handler
                retryRelayOverride = null;
                AddHistoryRecord(lastRecvFileName ?? "Unknown file", "Received", "", "Failed");
            }
            else
            {
            bool retryable;
            lock (recvErrors) { lock (lastRecvLines) { retryable = IsRetryableError(recvErrors, lastRecvLines); } }

            if (recvRetryCount < MaxRecvAutoRetries && !string.IsNullOrEmpty(lastRecvCode) && retryable)
            {
                recvRetryCount++;
                // Cap delay at 10s — receiver shouldn't wait 32s between attempts
                var delay = Math.Min((int)Math.Pow(2, recvRetryCount) * 1000, 10000);
                isReceiving = false;
                _recvConPtyPid = 0; recvProc?.Dispose(); recvProc = null;
                var relayNote = retryRelayOverride != null ? " (fallback relay)" : "";
                recvStatus.Text = $"Connecting to sender. Retry {recvRetryCount}/{MaxRecvAutoRetries}{relayNote}...";
                recvStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
                recvProgressPanel.Visibility = Visibility.Visible;
                recvProgress.IsIndeterminate = true;
                recvProgressDetail.Text = "";
                recvEtaText.Text = "";
                recvSpeedText.Text = ""; recvSpeedRow.Visibility = Visibility.Collapsed;
                Log($"Recv auto-retry {recvRetryCount}/{MaxRecvAutoRetries} in {delay}ms");

                recvRetryCts?.Dispose();
                recvRetryCts = new CancellationTokenSource();
                var token = recvRetryCts.Token;
                _ = Task.Delay(delay, token).ContinueWith(_ =>
                {
                    if (!token.IsCancellationRequested)
                        Dispatcher.BeginInvoke(() => { if (!isClosing) StartRecv(lastRecvCode!, true); }); // resume=true preserves retryCount
                }, TaskScheduler.Default);
                return;
            }
            // Primary retries exhausted — try fallback relay once if configured.
            // Skip for Direct P2P: the croc code is only registered on the sender's local relay,
            // so switching to a different relay would never find it.
            if (retryable && _currentRelayOverride == null &&
                !string.IsNullOrEmpty(appConfig.FallbackRelayServer) && retryRelayOverride == null)
            {
                retryRelayOverride = appConfig.FallbackRelayServer;
                recvRetryCount = 0;
                isReceiving = false;
                _recvConPtyPid = 0; recvProc?.Dispose(); recvProc = null;
                recvStatus.Text = "Trying fallback relay...";
                recvStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
                recvProgressPanel.Visibility = Visibility.Visible;
                recvProgress.IsIndeterminate = true;
                recvProgressDetail.Text = "";
                recvEtaText.Text = "";
                recvSpeedText.Text = ""; recvSpeedRow.Visibility = Visibility.Collapsed;
                Log($"Recv switching to fallback relay: {retryRelayOverride}");
                recvRetryCts?.Dispose();
                recvRetryCts = new CancellationTokenSource();
                var fbToken = recvRetryCts.Token;
                _ = Task.Delay(2000, fbToken).ContinueWith(_ =>
                {
                    if (!fbToken.IsCancellationRequested)
                        Dispatcher.BeginInvoke(() => { if (!isClosing) StartRecv(lastRecvCode!, false); });
                }, TaskScheduler.Default);
                return;
            }
            } // closes else
        }

        recvProgressPanel.Visibility = Visibility.Collapsed;
        recvProgressDetail.Text = "";
        recvEtaText.Text = "";
        recvSpeedText.Text = ""; recvSpeedRow.Visibility = Visibility.Collapsed;
        recvElapsedText.Text = "";
        recvCancelBtn.Visibility = Visibility.Collapsed;
        isReceiving = false;
        recvBtn.IsEnabled = true;
        recvCodeBox.IsEnabled = true;

        // User explicitly cancelled — restore the empty state and exit early
        if (exitCode != 0 && recvStatus.Text == "Transfer cancelled")
        {
            recvFilePreviewCard.Visibility = Visibility.Collapsed;
            recvTextCard.Visibility = Visibility.Collapsed;
            recvActivityPanel.Visibility = Visibility.Collapsed;
            recvEmptyState.Visibility = Visibility.Visible;
            ClearTaskbarProgress();
            _recvConPtyPid = 0; recvProc?.Dispose(); recvProc = null;
            return;
        }

        if (exitCode == 0)
        {
            // Show green completion progress bar
            recvProgress.Value = 100;
            recvProgress.IsIndeterminate = false;
            recvProgress.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            recvProgressDetail.Text = "Complete";
            recvEtaText.Text = "";
            recvSpeedText.Text = ""; recvSpeedRow.Visibility = Visibility.Collapsed;
            recvElapsedText.Text = "";
            recvProgressPanel.Visibility = Visibility.Visible;
            recvActivityPanel.Visibility = Visibility.Visible;
            ClearTaskbarProgress();

            recvResetTimer?.Stop();
            recvResetTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            recvResetTimer.Tick += (_, _) =>
            {
                if (isReceiving) { recvResetTimer?.Stop(); recvResetTimer = null; return; } // new receive started
                recvProgressPanel.Visibility = Visibility.Collapsed;
                HideConnInfo(false);
                recvProgress.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
                recvResetTimer?.Stop();
                recvResetTimer = null;
            };
            recvResetTimer.Start();

            if (isReceivingText && receivedText != null)
            {
                recvRetryCts?.Dispose(); recvRetryCts = null;
                retryRelayOverride = null;
                AddHistoryRecord("(text)", "Received", "—", "Success");
                recvStatus.Text = "";
                PlayCompletionSound();
                ShowWindowsToast("Text Received", "Open VIA x4 to read the message.");

                _fullReceivedText = receivedText;
                if (receivedText.Length > MaxRecvTextDisplayLength)
                {
                    recvTextContent.Text = receivedText[..MaxRecvTextDisplayLength] + $"\n\n[Truncated — {FormatSize(receivedText.Length * 2)} total]";
                    showFullTextBtn.Visibility = Visibility.Visible;
                    showFullTextLabel.Text = $"Show full text ({FormatSize(receivedText.Length * 2)})";
                }
                else
                {
                    recvTextContent.Text = receivedText;
                    showFullTextBtn.Visibility = Visibility.Collapsed;
                }
                AnimateCardIn(recvTextCard);
                ShowInfoBar("Text received", "You can highlight or copy the text below", InfoBarSeverity.Success);
            }
            else
            {
                var recvSize = "";
                if (lastRecvFileName != null)
                {
                    var recvFilePath = Path.Combine(saveFolder ?? GetDefaultSaveFolder(), lastRecvFileName);
                    if (File.Exists(recvFilePath)) recvSize = FormatSize(new FileInfo(recvFilePath).Length);
                }
                var fullRecvPath = lastRecvFileName != null
                    ? Path.Combine(saveFolder ?? GetDefaultSaveFolder(), lastRecvFileName)
                    : "";
                recvRetryCts?.Dispose(); recvRetryCts = null;
                retryRelayOverride = null; // reset fallback relay on success
                await ScanAndFinalizeReceivedFileAsync(fullRecvPath, recvSize, recvGeneration);
            }
        }
        else if (exitCode != 0 && recvStatus.Text != "Transfer cancelled" && !skipRecvErrorHandling)
        {
            ClearTaskbarProgress();
            retryRelayOverride = null; // reset fallback relay on failure
            recvRetryCount = 0; // reset so next manual Resume attempt gets a fresh retry budget
            AddHistoryRecord(lastRecvFileName ?? "Unknown file", "Received", "", "Failed");
            string rawErr;
            lock (recvErrors) rawErr = recvErrors.Count > 0 ? string.Join("; ", recvErrors) : "";
            if (string.IsNullOrEmpty(rawErr))
                lock (lastRecvLines) rawErr = lastRecvLines.LastOrDefault(l => !ProgressRegex.IsMatch(l)) ?? lastRecvLines.LastOrDefault() ?? "";

            // Build a user-friendly error message with guidance
            var errLower = rawErr.ToLowerInvariant();
            bool isDirectP2pRecv = _currentRelayOverride != null &&
                                   !_currentRelayOverride.StartsWith("localhost:", StringComparison.Ordinal);
            string friendlyMsg;
            if (errLower.Contains("invalid code") || errLower.Contains("not found"))
                friendlyMsg = isDirectP2pRecv
                    ? "The sender may have cancelled or restarted. Ask them to click Resume — they'll get a new code to share with you."
                    : "The code was not found. Check for typos, or ask the sender for a new code — codes expire after a few minutes.";
            else if (errLower.Contains("wrong passphrase"))
                friendlyMsg = "The passphrase doesn't match. Both sides must have the same passphrase set in Settings \u2192 Security \u2192 Passphrase.";
            else if (errLower.Contains("connection refused") || errLower.Contains("actively refused"))
                friendlyMsg = isDirectP2pRecv
                    ? "The sender's relay refused the connection. They may have closed VIA x4 or their firewall is blocking it. Ask them to click Resume for a new code."
                    : "Connection was blocked. Windows Firewall or your network may be blocking the transfer engine. Check Firewall settings.";
            else if (errLower.Contains("no such host") || errLower.Contains("could not resolve"))
                friendlyMsg = "Could not reach the relay server. Check your internet connection, or try a different relay in Settings.";
            else if (errLower.Contains("disk full") || errLower.Contains("no space left") ||
                     errLower.Contains("not enough space") || errLower.Contains("not enough disk space"))
                friendlyMsg = "Not enough disk space to save the file. Free up space and try again.";
            else if (errLower.Contains("permission denied") || errLower.Contains("access is denied"))
                friendlyMsg = "Permission denied writing to the save folder. Check folder permissions or change the save location in Settings.";
            else if (errLower.Contains("being used by another process"))
                friendlyMsg = "The file is locked by another application. Close any programs using it and try again.";
            else if (errLower.Contains("hash mismatch") || errLower.Contains("integrity") ||
                     errLower.Contains("checksum") || errLower.Contains("corrupted"))
                friendlyMsg = "The file was modified during transfer or data was corrupted in transit. Ask the sender to try again.";
            else if (errLower.Contains("cannot find the file") || errLower.Contains("no such file") ||
                     errLower.Contains("cannot find the path") || errLower.Contains("system cannot find"))
                friendlyMsg = "A file was moved or deleted during the transfer. Start a new transfer.";
            else if (errLower.Contains("filename or extension is too long"))
                friendlyMsg = "The file path is too long for Windows. Save to a shorter folder path (e.g., C:\\Downloads).";
            else if (errLower.Contains("network path") || errLower.Contains("network name is no longer available"))
                friendlyMsg = "The network drive disconnected. Reconnect the drive and try again, or save to a local folder.";
            else if (errLower.Contains("virus") || errLower.Contains("quarantine") || errLower.Contains("threat") ||
                     errLower.Contains("blocked by") || errLower.Contains("security software"))
                friendlyMsg = "Your antivirus may have blocked the file during transfer. Check your antivirus quarantine or temporarily disable real-time protection, then try again.";
            else if (string.IsNullOrEmpty(rawErr) || errLower.Contains("exit code"))
                friendlyMsg = isDirectP2pRecv
                    ? "The sender went offline or closed VIA x4. Ask them to click Resume — they'll get a new code to share with you."
                    : "Connection to sender was lost. Ask the sender to click Send again, then re-enter the new code.";
            else
                friendlyMsg = isDirectP2pRecv
                    ? $"{rawErr}. The sender may have gone offline. Ask them to click Resume for a new code."
                    : $"{rawErr}. Try clicking Resume, or ask the sender for a new code.";

            recvStatus.Text = "";
            ShowInfoBar("Transfer failed", friendlyMsg, InfoBarSeverity.Error, 10000);
            Log($"Recv failed: raw=[{rawErr}] friendly=[{friendlyMsg}]");
            if (lastRecvCode != null)
                AnimateFadeIn(recvRetryPanel);
        }

        _recvConPtyPid = 0; recvProc?.Dispose(); recvProc = null;
        _currentRelayOverride = null; // clear any Direct P2P relay override after final recv exit
        _directP2pLatencyMs = -1;
    }

    private async Task ScanAndFinalizeReceivedFileAsync(string fullRecvPath, string recvSize, int gen)
    {
        // ── Optional Windows Defender scan ───────────────
        // Skipped entirely if Defender is not installed, file is missing, or scan errors.
        // The file is ALWAYS kept and shown when this method completes normally.
        bool threatDetected = false;
        var mpcmd = @"C:\Program Files\Windows Defender\MpCmdRun.exe";
        if (appConfig.ScanReceivedFiles && !string.IsNullOrEmpty(fullRecvPath) && File.Exists(fullRecvPath) && File.Exists(mpcmd))
        {
            recvStatus.Text = "Scanning for threats...";
            try
            {
                var psi = new ProcessStartInfo(mpcmd)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-Scan");
                psi.ArgumentList.Add("-ScanType");
                psi.ArgumentList.Add("3");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(fullRecvPath);
                using var scanProc = Process.Start(psi)!;
                bool exited = await Task.Run(() => scanProc.WaitForExit(30_000));
                if (!exited)
                {
                    // Scan timed out — kill to avoid orphan process, treat as clean
                    try { scanProc.Kill(); } catch { }
                }
                else
                {
                    threatDetected = scanProc.ExitCode == 2; // 2 = Defender found a threat
                }
            }
            catch { /* Defender unavailable or scan failed — treat as clean */ }
        }
        if (gen != recvGeneration) return;
        var fileName = lastRecvFileName; // capture before any message-pumping call can overwrite the field

        recvStatus.Text = "";

        if (threatDetected)
        {
            bool fileStillExists = File.Exists(fullRecvPath);
            if (!fileStillExists)
            {
                // Defender's real-time protection already quarantined it
                AddHistoryRecord(fileName ?? "Unknown file", "Received", recvSize, "Quarantined");
                ShowInfoBar("File quarantined by Defender",
                    $"'{fileName}' was flagged and removed by Windows Defender. Open Windows Security \u2192 Protection history to review.",
                    InfoBarSeverity.Error, 20000);
                ShowWindowsToast("⚠ Threat Quarantined",
                    $"'{fileName ?? "Unknown file"}' was removed by Windows Defender", highPriority: true);
                return;
            }

            // File is still on disk — let the user decide
            var result = MessageBox.Show(
                $"Windows Defender flagged '{fileName}' as a potential threat.\n\nIf you trust the sender, you can keep the file. Otherwise it will be deleted.\n\nKeep the file?",
                "Defender Warning \u2014 VIA x4",
                System.Windows.MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);

            if (result != System.Windows.MessageBoxResult.Yes)
            {
                try { File.Delete(fullRecvPath); } catch { }
                AddHistoryRecord(fileName ?? "Unknown file", "Received", recvSize, "Blocked");
                ShowInfoBar("File deleted", "The flagged file was removed from your device.", InfoBarSeverity.Warning, 8000);
                return;
            }

            // User chose to keep — treat exactly like a normal successful receive from here on
            threatDetected = false;
            AddHistoryRecord(fileName ?? "Unknown file", "Received", recvSize, "Success", fullRecvPath);
            ShowInfoBar("File received", $"Saved to {saveFolder}", InfoBarSeverity.Success);
        }
        else
        {
            AddHistoryRecord(fileName ?? "Unknown file", "Received", recvSize, "Success", fullRecvPath);
            ShowInfoBar("File received", $"Saved to {saveFolder}", InfoBarSeverity.Success);
        }

        PlayCompletionSound();
        var toastFolder = Path.GetFileName(saveFolder ?? GetDefaultSaveFolder());
        var recvIconPath = !string.IsNullOrEmpty(fullRecvPath) ? GetCachedFileIconPath(fullRecvPath) : null;
        ShowWindowsToast("File Received",
            !string.IsNullOrEmpty(recvSize)
                ? $"{fileName} · {recvSize} · Saved to {toastFolder}"
                : $"{fileName} · Saved to {toastFolder}",
            fileIconPath: recvIconPath);

        if (fileName != null)
        {
            var recvPath = Path.Combine(saveFolder ?? GetDefaultSaveFolder(), fileName);
            recvFileName.Text = fileName;
            recvFileSymbol.Symbol = GetFileSymbol(recvPath);
            if (File.Exists(recvPath))
            {
                var info = new FileInfo(recvPath);
                recvFileDetail.Text = $"{FormatSize(info.Length)}  \u2022  {GetFileTypeDescription(recvPath)}";
                var shellIcon = await GetFileIconAsync(recvPath);
                if (gen != recvGeneration) return;
                recvFileIcon.Source = shellIcon;
                recvFileIcon.Visibility = shellIcon != null ? Visibility.Visible : Visibility.Collapsed;
                recvFileSymbol.Visibility = shellIcon != null ? Visibility.Collapsed : Visibility.Visible;
            }
            else
            {
                recvFileDetail.Text = "Saved to " + (saveFolder ?? "Desktop");
                recvFileIcon.Source = null;
                recvFileIcon.Visibility = Visibility.Collapsed;
                recvFileSymbol.Visibility = Visibility.Visible;
            }
            recvFileActions.Visibility = Visibility.Visible;
            AnimateCardIn(recvFilePreviewCard);
        }
    }

    private void OpenReceivedFile_Click(object sender, RoutedEventArgs e)
    {
        if (lastRecvFileName == null) return;
        var filePath = Path.Combine(saveFolder ?? GetDefaultSaveFolder(), lastRecvFileName);
        if (File.Exists(filePath))
        {
            try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
            catch (Exception ex) { ShowInfoBar("Cannot open", ex.Message, InfoBarSeverity.Warning, 5000); }
        }
        else
        {
            ShowInfoBar("File not found", "The file may have been moved or deleted", InfoBarSeverity.Warning, 5000);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = saveFolder ?? GetDefaultSaveFolder();
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
        }
        catch (Exception ex) { ShowInfoBar("Cannot open folder", ex.Message, InfoBarSeverity.Warning, 4000); }
    }

    private void CopyReceivedFilePath_Click(object sender, RoutedEventArgs e)
    {
        if (lastRecvFileName == null) return;
        var filePath = Path.Combine(saveFolder ?? GetDefaultSaveFolder(), lastRecvFileName);
        Clipboard.SetText(filePath);
        ShowInfoBar("Copied", "File path copied to clipboard", InfoBarSeverity.Success, 3000);
    }

    private void CopyReceivedText_Click(object sender, RoutedEventArgs e)
    {
        var textToCopy = _fullReceivedText ?? recvTextContent.Text;
        if (!string.IsNullOrEmpty(textToCopy))
        {
            Clipboard.SetText(textToCopy);
            ShowInfoBar("Copied", "Text copied to clipboard", InfoBarSeverity.Success, 3000);
        }
    }

    private void ShowFullText_Click(object sender, RoutedEventArgs e)
    {
        if (_fullReceivedText != null)
        {
            recvTextContent.Text = _fullReceivedText;
            showFullTextBtn.Visibility = Visibility.Collapsed;
        }
    }

    private async void Recv_Click(object sender, RoutedEventArgs e)
    {
        // Strip all whitespace — chat apps may wrap long codes across lines
        var code = new string(recvCodeBox.Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (string.IsNullOrEmpty(code))
        {
            ShowInfoBar("No code", "Enter the transfer code from the sender", InfoBarSeverity.Warning, 3000);
            recvCodeBox.Focus();
            return;
        }

        // Direct P2P code: decrypt relay address and probe before launching croc
        if (code.StartsWith("d:", StringComparison.OrdinalIgnoreCase))
        {
            var dparts = code.Split(':', 3);
            if (dparts.Length != 3 || string.IsNullOrEmpty(dparts[1]) || string.IsNullOrEmpty(dparts[2]))
            {
                ShowInfoBar("Invalid code", "This Direct Transfer code appears incomplete. Make sure you copied the entire code.", InfoBarSeverity.Error, 8000);
                return;
            }

            var relay = TryDecodeRelayFromCode(dparts[1], dparts[2]);
            if (relay == null)
            {
                ShowInfoBar("Invalid code", "Could not decrypt the Direct Transfer code. Check for typos — the code must be copied exactly as shown by the sender.", InfoBarSeverity.Error, 8000);
                return;
            }

            // Parse host:port — supports both IPv4 "1.2.3.4:9009" and IPv6 "[::1]:9009"
            string? probeHost = null;
            int probePort = 0;
            bool probeOk = false;
            if (relay.StartsWith('['))
            {
                var cb = relay.IndexOf(']');
                if (cb > 1 && cb < relay.Length - 2 && relay[cb + 1] == ':')
                {
                    probeHost = relay[1..cb];
                    probeOk = System.Net.IPAddress.TryParse(probeHost, out _) &&
                              int.TryParse(relay[(cb + 2)..], out probePort) &&
                              probePort is >= 1024 and <= 65535;
                }
            }
            else
            {
                var rparts = relay.Split(':');
                if (rparts.Length == 2)
                {
                    probeHost = rparts[0];
                    probeOk = System.Net.IPAddress.TryParse(probeHost, out _) &&
                              int.TryParse(rparts[1], out probePort) &&
                              probePort is >= 1024 and <= 65535;
                }
            }
            if (!probeOk || probeHost == null)
            {
                ShowInfoBar("Invalid code", "The Direct Transfer code contains an invalid address. Ask the sender for a new code.", InfoBarSeverity.Error, 8000);
                return;
            }

            recvBtn.IsEnabled = false;
            recvCodeBox.IsEnabled = false;
            bool reachable = await ProbeRelayAsync(probeHost, probePort, 4000);
            recvCodeBox.IsEnabled = true;
            if (!reachable)
            {
                recvBtn.IsEnabled = true;
                ShowInfoBar("Sender unreachable",
                    "Cannot connect to the sender. Make sure the sender has VIA x4 open with the Direct Transfer code visible on screen.",
                    InfoBarSeverity.Error, 10000);
                return;
            }
        }

        recvTimeoutRetryCount = 0; // fresh user-initiated receive — reset timeout retry counter
        _pendingRecvTimeoutRetry = false;
        _originalRecvCode = code; // preserve full "d:xxx:code" for manual Resume after all retries exhaust
        StartRecv(code, false);
    }

    private static async Task<bool> ProbeRelayAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    private void RecvResume_Click(object sender, RoutedEventArgs e)
    {
        if (lastRecvCode != null)
        {
            recvTimeoutRetryCount = 0; // manual resume — reset timeout retry counter
            // Use the original full code (d:xxx:code) if available — lastRecvCode is the stripped
            // croc code and _currentRelayOverride was cleared by OnRecvDone, so Resume with just
            // lastRecvCode would silently connect to the default relay instead of the sender's P2P relay.
            var codeForResume = _originalRecvCode ?? lastRecvCode;
            StartRecv(codeForResume, lastRecvPercent > 0);
        }
    }

    private void CleanupPartialRecvFile()
    {
        if (lastRecvFileName == null) return;
        var partial = Path.Combine(saveFolder ?? GetDefaultSaveFolder(), lastRecvFileName);
        try
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
                Log($"Cleaned up partial file: {partial}");
            }
        }
        catch (Exception ex) { LogError("CleanupPartialRecvFile", ex); }
    }

    private void RecvStartOver_Click(object sender, RoutedEventArgs e)
    {
        CleanupPartialRecvFile();
        _originalRecvCode = null;
        recvRetryPanel.Visibility = Visibility.Collapsed;
        recvActivityPanel.Visibility = Visibility.Collapsed;
        recvEmptyState.Visibility = Visibility.Visible;
        recvStatus.Text = "";
        recvCodeBox.Text = "";
    }

    private void RecvCancel_Click(object sender, RoutedEventArgs e)
    {
        // Cancel pending auto-retry
        if (recvRetryCts != null)
        {
            recvRetryCts.Cancel();
            recvRetryCts.Dispose();
            recvRetryCts = null;
            retryRelayOverride = null;
            _currentRelayOverride = null;
            _originalRecvCode = null;
            _pendingRecvTimeoutRetry = false;
            recvCancelBtn.Visibility = Visibility.Collapsed;
            recvProgressPanel.Visibility = Visibility.Collapsed;
            recvFilePreviewCard.Visibility = Visibility.Collapsed;
            recvTextCard.Visibility = Visibility.Collapsed;
            recvActivityPanel.Visibility = Visibility.Collapsed;
            recvEmptyState.Visibility = Visibility.Visible;
            recvStatus.Text = "Transfer cancelled";
            recvStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
            isReceiving = false;
            recvBtn.IsEnabled = true;
            recvCodeBox.IsEnabled = true;
            StopRecvPoll();
            HideConnInfo(false);
            ClearTaskbarProgress();
            return;
        }

        if ((recvProc == null || recvProc.HasExited) && _recvConPtyPid == 0)
        {
            recvCancelBtn.Visibility = Visibility.Collapsed;
            recvProgressPanel.Visibility = Visibility.Collapsed;
            recvFilePreviewCard.Visibility = Visibility.Collapsed;
            recvTextCard.Visibility = Visibility.Collapsed;
            recvActivityPanel.Visibility = Visibility.Collapsed;
            recvEmptyState.Visibility = Visibility.Visible;
            isReceiving = false;
            recvBtn.IsEnabled = true;
            recvCodeBox.IsEnabled = true;
            _currentRelayOverride = null;
            _originalRecvCode = null;
            _pendingRecvTimeoutRetry = false;
            StopRecvPoll();
            HideConnInfo(false);
            ClearTaskbarProgress();
            return;
        }

        if (lastRecvPercent > 50)
        {
            var result = MessageBox.Show(
                $"Transfer is {lastRecvPercent:F0}% complete. Cancel anyway?",
                "Cancel Transfer", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes) return;
        }

        StopWaitingDots();
        StopRecvPoll();
        ClearTaskbarProgress();
        recvStatus.Text = "Transfer cancelled";
        recvStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        retryRelayOverride = null;
        KillRecvProc();
        // Clean up partial file — croc writes directly to the final filename
        CleanupPartialRecvFile();
    }

    // ── Keyboard Shortcuts ──────────────────────────────

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Allow Enter from the receive code box or send text box even though they're TextBoxes
        bool isInRecvCode = e.OriginalSource is System.Windows.Controls.TextBox tb
            && ReferenceEquals(tb, recvCodeBox);
        bool isInSendText = e.OriginalSource is System.Windows.Controls.TextBox tb2
            && ReferenceEquals(tb2, sendTextBox);

        // Don't intercept keyboard when user is typing in a text field (except special cases above)
        if (e.OriginalSource is System.Windows.Controls.TextBox && !isInRecvCode && !isInSendText) return;
        // Also skip if settings or history overlays are open
        if (settingsPanel.Visibility == Visibility.Visible && e.Key != Key.Escape) return;
        if (historyPanel.Visibility == Visibility.Visible && e.Key != Key.Escape) return;

        if (e.Key == Key.Enter)
        {
            // Ctrl+Enter sends text in text mode
            if (isInSendText && Keyboard.Modifiers == ModifierKeys.Control && isTextMode && !isSending)
            {
                StartSend(false);
                e.Handled = true;
                return;
            }
            // Plain Enter in text box should insert newline, not send
            if (isInSendText) return;

            if (isOnSendTab && sendBtn.IsEnabled && !isSending)
                StartSend(false);
            else if (!isOnSendTab && recvBtn.IsEnabled && !isReceiving)
                Recv_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (settingsPanel.Visibility == Visibility.Visible)
            {
                AnimatePanelClose(settingsPanel);
                settingsIcon.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
            }
            else if (historyPanel.Visibility == Visibility.Visible)
            {
                AnimatePanelClose(historyPanel);
                historyIcon.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
            }
            else if (isSending) SendCancel_Click(this, new RoutedEventArgs());
            else if (isReceiving) RecvCancel_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (isSending || isReceiving) return; // Don't switch tabs during transfer
            // Don't hijack Ctrl+V when in text mode — let the text box handle it
            if (isOnSendTab && isTextMode) return;
            if (isOnSendTab)
                RecvTab_Click(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            if (!isOnSendTab) // Only paste if we're now on receive tab
            {
                if (Clipboard.ContainsText())
                    recvCodeBox.Text = Clipboard.GetText().Trim();
                recvCodeBox.Focus();
                recvCodeBox.CaretIndex = recvCodeBox.Text.Length;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (!isOnSendTab || isSending || isTextMode) return;
            BrowseFile_Click(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            e.Handled = true;
        }
    }

    // ── Transfer History UI ────────────────────────────────

    private void History_Click(object sender, RoutedEventArgs e)
    {
        // Cancel any in-flight panel animations to prevent race conditions
        SnapPanelState(historyPanel);
        SnapPanelState(settingsPanel);

        if (historyPanel.Visibility == Visibility.Visible)
        {
            AnimatePanelClose(historyPanel);
            historyIcon.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
            return;
        }
        bool wasSwitching = settingsPanel.Visibility == Visibility.Visible;
        if (wasSwitching)
        {
            settingsPanel.Visibility = Visibility.Collapsed;
            settingsIcon.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
        }
        historySearchBox.Text = "";
        RenderHistoryList();
        if (wasSwitching)
            historyPanel.Visibility = Visibility.Visible; // instant swap, no animation
        else
            AnimatePanelOpen(historyPanel);
        historyIcon.Foreground = (Brush)FindResource("SystemAccentColorPrimaryBrush");
    }

    private void CloseHistory_Click(object sender, RoutedEventArgs e)
    {
        AnimatePanelClose(historyPanel);
        historyIcon.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
    }

    private void HistorySearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RenderHistoryList();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        appConfig.History.Clear();
        SaveSettings();
        RenderHistoryList();
    }

    private void RenderHistoryList()
    {
        historyList.Items.Clear();
        var searchText = historySearchBox?.Text?.Trim() ?? "";
        var records = appConfig.History.AsEnumerable();
        if (!string.IsNullOrEmpty(searchText))
        {
            records = records.Where(r =>
                (r.FileName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true) ||
                (r.Direction?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true) ||
                (r.Status?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true) ||
                (r.Date?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true));
        }
        var filteredList = records.ToList();
        historyEmpty.Visibility = filteredList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        historyEmpty.Text = filteredList.Count == 0 && !string.IsNullOrEmpty(searchText)
            ? "No matching transfers"
            : "No transfers yet";
        clearHistoryBtn.Visibility = appConfig.History.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var record in filteredList)
        {
            var icon = record.Direction == "Sent" ? SymbolRegular.ArrowUpload24 : SymbolRegular.ArrowDownload24;
            var statusColor = record.Status == "Success"
                ? (Brush)FindResource("TextFillColorPrimaryBrush")
                : (Brush)FindResource("SystemFillColorCriticalBrush");

            var grid = new System.Windows.Controls.Grid
            {
                ColumnDefinitions =
                {
                    new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) },
                    new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }
                }
            };

            var symbolIcon = new SymbolIcon { Symbol = icon, FontSize = 16, Margin = new Thickness(0, 0, 12, 0) };
            System.Windows.Controls.Grid.SetColumn(symbolIcon, 0);

            var infoStack = new System.Windows.Controls.StackPanel();
            infoStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = record.FileName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = statusColor,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var detailParts = new List<string> { record.Direction };
            if (!string.IsNullOrEmpty(record.Size)) detailParts.Add(record.Size);
            detailParts.Add(record.Date);
            infoStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = string.Join("  \u2022  ", detailParts),
                FontSize = 12,
                Foreground = (Brush)FindResource("TextFillColorTertiaryBrush")
            });
            System.Windows.Controls.Grid.SetColumn(infoStack, 1);

            if (record.Status == "Failed")
            {
                var failBadge = new System.Windows.Controls.TextBlock
                {
                    Text = "Failed",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("SystemFillColorCriticalBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                System.Windows.Controls.Grid.SetColumn(failBadge, 2);
                grid.Children.Add(failBadge);
            }
            else if (record.Direction == "Received" && !string.IsNullOrEmpty(record.FilePath))
            {
                // Open-in-folder button for received files — path is persisted so works after restart
                var openBtn = new Wpf.Ui.Controls.Button
                {
                    Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                    Padding = new Thickness(6, 4, 6, 4),
                    ToolTip = "Open containing folder",
                    Content = new SymbolIcon { Symbol = SymbolRegular.FolderOpen24, FontSize = 14 },
                    VerticalAlignment = VerticalAlignment.Center
                };
                var capturedPath = record.FilePath;
                openBtn.Click += (_, _) =>
                {
                    try
                    {
                        var folder = Path.GetDirectoryName(capturedPath) ?? capturedPath;
                        if (File.Exists(capturedPath))
                            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { $"/select,{capturedPath}" } });
                        else if (Directory.Exists(folder))
                            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
                        else
                            ShowInfoBar("Folder not found", "The save location no longer exists", InfoBarSeverity.Warning, 4000);
                    }
                    catch { }
                };
                System.Windows.Controls.Grid.SetColumn(openBtn, 2);
                grid.Children.Add(openBtn);
            }

            grid.Children.Add(symbolIcon);
            grid.Children.Add(infoStack);

            var row = new System.Windows.Controls.Border
            {
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 4),
                CornerRadius = new CornerRadius(4),
                Background = (Brush)FindResource("ControlFillColorDefaultBrush"),
                Child = grid
            };
            System.Windows.Automation.AutomationProperties.SetName(row, $"{record.Direction} {record.FileName}, {record.Status}, {record.Date}");
            var defaultBg = (Brush)FindResource("ControlFillColorDefaultBrush");
            var hoverBg = (Brush)FindResource("ControlFillColorSecondaryBrush");
            row.MouseEnter += (_, _) => row.Background = hoverBg;
            row.MouseLeave += (_, _) => row.Background = defaultBg;

            historyList.Items.Add(row);
        }
    }

    // ── Settings UI ──────────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Cancel any in-flight panel animations to prevent race conditions
        SnapPanelState(settingsPanel);
        SnapPanelState(historyPanel);

        if (settingsPanel.Visibility == Visibility.Visible)
        {
            AnimatePanelClose(settingsPanel);
            settingsIcon.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
            return;
        }
        bool wasSwitching = historyPanel.Visibility == Visibility.Visible;
        if (wasSwitching)
        {
            historyPanel.Visibility = Visibility.Collapsed;
            historyIcon.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
        }
        LoadSettingsUI();
        UpdateStorageFootprint();
        if (wasSwitching)
            settingsPanel.Visibility = Visibility.Visible; // instant swap, no animation
        else
            AnimatePanelOpen(settingsPanel);
        settingsIcon.Foreground = (Brush)FindResource("SystemAccentColorPrimaryBrush");
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        AnimatePanelClose(settingsPanel);
        settingsIcon.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
    }

    private void LoadSettingsUI()
    {
        _settingsLoading = true;
        try
        {
            // Throttle
            string throttle = appConfig.ThrottleUpload ?? "";
            int throttleIdx = 0;
            for (int i = 0; i < throttleCombo.Items.Count; i++)
            {
                if (((System.Windows.Controls.ComboBoxItem)throttleCombo.Items[i]).Tag?.ToString() == throttle)
                { throttleIdx = i; break; }
            }
            throttleCombo.SelectedIndex = throttleIdx;


            // Relay
            relayBox.Text = appConfig.RelayServer ?? "";

            // Fallback relay
            fallbackRelayBox.Text = appConfig.FallbackRelayServer ?? "";

            // No compress
            noCompressCheck.IsChecked = appConfig.NoCompress;

            // AV scan
            avScanCheck.IsChecked = appConfig.ScanReceivedFiles;

            // Force local
            forceLocalCheck.IsChecked = appConfig.ForceLocal;

            // Overwrite
            overwriteCheck.IsChecked = appConfig.OverwriteExisting;

            // SOCKS5
            socks5Box.Text = appConfig.Socks5Proxy ?? "";

            // Passphrase
            passphraseBox.Text = appConfig.Passphrase ?? "";

            // Curve
            string curve = appConfig.EncryptionCurve ?? "p256";
            int curveIdx = 0;
            for (int i = 0; i < curveCombo.Items.Count; i++)
            {
                if (((System.Windows.Controls.ComboBoxItem)curveCombo.Items[i]).Tag?.ToString() == curve)
                { curveIdx = i; break; }
            }
            curveCombo.SelectedIndex = curveIdx;

            // Hash
            string hash = appConfig.HashAlgorithm ?? "xxhash";
            int hashIdx = 0;
            for (int i = 0; i < hashCombo.Items.Count; i++)
            {
                if (((System.Windows.Controls.ComboBoxItem)hashCombo.Items[i]).Tag?.ToString() == hash)
                { hashIdx = i; break; }
            }
            hashCombo.SelectedIndex = hashIdx;

            // Clean on exit
            cleanOnExitCheck.IsChecked = appConfig.CleanOnExit;
        }
        finally { _settingsLoading = false; }
    }

    private void Throttle_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingsLoading || throttleCombo.SelectedItem == null) return;
        appConfig.ThrottleUpload = ((System.Windows.Controls.ComboBoxItem)throttleCombo.SelectedItem).Tag?.ToString() ?? "";
        SaveSettings();
    }


    private static readonly Regex HostPortRegex = new(@"^[\w\.\-]+(:\d{1,5})?$", RegexOptions.Compiled);

    private static bool IsValidHostPort(string value)
    {
        if (!HostPortRegex.IsMatch(value)) return false;
        var colonIdx = value.LastIndexOf(':');
        if (colonIdx >= 0 && colonIdx < value.Length - 1)
        {
            if (int.TryParse(value[(colonIdx + 1)..], out var port))
                return port >= 1 && port <= 65535;
            return false;
        }
        return true;
    }

    private void Relay_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = relayBox.Text.Trim();
        if (!string.IsNullOrEmpty(value) && !IsValidHostPort(value))
        {
            ShowInfoBar("Invalid relay", "Enter a valid address like relay.example.com or relay.example.com:9009", InfoBarSeverity.Warning, 5000);
            relayBox.Text = appConfig.RelayServer ?? "";
            return;
        }
        appConfig.RelayServer = value;
        SaveSettings();
    }

    private void NoCompress_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        appConfig.NoCompress = noCompressCheck.IsChecked == true;
        SaveSettings();
    }

    private void AvScan_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        appConfig.ScanReceivedFiles = avScanCheck.IsChecked == true;
        SaveSettings();
    }

    private void ForceLocal_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        appConfig.ForceLocal = forceLocalCheck.IsChecked == true;
        SaveSettings();
    }

    private void CleanOnExit_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        appConfig.CleanOnExit = cleanOnExitCheck.IsChecked == true;
        SaveSettings();
    }

    private void UpdateStorageFootprint()
    {
        long engineSize = 0, configSize = 0, logSize = 0;
        try
        {
            var engineFile = EnginePath;
            if (File.Exists(engineFile)) engineSize = new FileInfo(engineFile).Length;
        }
        catch { }
        try
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VIA x4");
            if (Directory.Exists(configDir))
            {
                foreach (var f in Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Name == "via.log" || fi.Extension == ".log")
                            logSize += fi.Length;
                        else
                            configSize += fi.Length;
                    }
                    catch { }
                }
            }
        }
        catch { }

        var total = engineSize + configSize + logSize;
        storageTotalText.Text = FormatSize(total);

        var parts = new List<string>();
        if (engineSize > 0) parts.Add($"Engine {FormatSize(engineSize)} (permanent)");
        if (configSize > 0) parts.Add($"Config {FormatSize(configSize)}");
        if (logSize > 0) parts.Add($"Logs {FormatSize(logSize)}");
        storageDetailText.Text = parts.Count > 0 ? string.Join(" · ", parts) : "No app data found";
    }

    private void FallbackRelay_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = fallbackRelayBox.Text.Trim();
        if (!string.IsNullOrEmpty(value) && !IsValidHostPort(value))
        {
            ShowInfoBar("Invalid fallback relay", "Enter a valid address like relay.example.com or relay.example.com:9009", InfoBarSeverity.Warning, 5000);
            fallbackRelayBox.Text = appConfig.FallbackRelayServer ?? "";
            return;
        }
        appConfig.FallbackRelayServer = value;
        SaveSettings();
    }

    private void Socks5_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = socks5Box.Text.Trim();
        if (!string.IsNullOrEmpty(value) && (!IsValidHostPort(value) || !value.Contains(':')))
        {
            ShowInfoBar("Invalid proxy", "SOCKS5 requires host and port, e.g. 127.0.0.1:9050", InfoBarSeverity.Warning, 5000);
            socks5Box.Text = appConfig.Socks5Proxy ?? "";
            return;
        }
        appConfig.Socks5Proxy = value;
        SaveSettings();
    }

    private void Curve_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (curveCombo.SelectedItem == null) return;
        var tag = ((System.Windows.Controls.ComboBoxItem)curveCombo.SelectedItem).Tag?.ToString() ?? "p256";
        UpdateCurveDescription(tag);
        if (_settingsLoading) return;
        appConfig.EncryptionCurve = tag;
        SaveSettings();
    }

    private void UpdateCurveDescription(string curve)
    {
        curveDescription.Text = curve switch
        {
            "p256" => "Fast, widely trusted \u2014 standard for TLS and most apps",
            "p521" => "Largest key size, highest security \u2014 slightly slower handshake",
            "p384" => "Middle ground between P-256 and P-521 \u2014 used by government systems",
            "siec" => "Supersingular isogeny curve \u2014 experimental, quantum-resistant research",
            _ => ""
        };
    }

    private void Overwrite_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        appConfig.OverwriteExisting = overwriteCheck.IsChecked == true;
        SaveSettings();
    }

    private void Passphrase_LostFocus(object sender, RoutedEventArgs e)
    {
        appConfig.Passphrase = passphraseBox.Text.Trim();
        SaveSettings();
    }

    private void Hash_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (hashCombo.SelectedItem == null) return;
        var tag = ((System.Windows.Controls.ComboBoxItem)hashCombo.SelectedItem).Tag?.ToString() ?? "xxhash";
        UpdateHashDescription(tag);
        if (_settingsLoading) return;
        appConfig.HashAlgorithm = tag;
        SaveSettings();
    }

    private void UpdateHashDescription(string hash)
    {
        hashDescription.Text = hash switch
        {
            "xxhash" => "Non-cryptographic, extremely fast \u2014 best for transfer verification",
            "imohash" => "Samples file chunks instead of reading fully \u2014 fast for large files, less thorough",
            "md5" => "Cryptographic hash, slower but widely recognized \u2014 full file verification",
            _ => ""
        };
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        appConfig.ThrottleUpload = "";
        appConfig.RelayServer = "";
        appConfig.FallbackRelayServer = "";
        appConfig.NoCompress = false;
        appConfig.ScanReceivedFiles = true;
        appConfig.ForceLocal = false;
        appConfig.OverwriteExisting = true;
        appConfig.Socks5Proxy = "";
        appConfig.HashAlgorithm = "xxhash";
        appConfig.EncryptionCurve = "p256";
        appConfig.Passphrase = "";
        appConfig.CleanOnExit = false;
        SaveSettings();
        LoadSettingsUI();
        ShowInfoBar("Settings reset", "All settings restored to defaults", InfoBarSeverity.Success, 3000);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { LogDir } });
        }
        catch (Exception ex) { ShowInfoBar("Cannot open folder", ex.Message, InfoBarSeverity.Warning, 4000); }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"VIA x4 v{CurrentVersion}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"CLR: {Environment.Version}");
            sb.AppendLine($"CPU arch: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"croc: {crocPath ?? "not loaded"}");
            sb.AppendLine($"Save folder: {saveFolder ?? GetDefaultSaveFolder()}");
            sb.AppendLine($"Relay: {(string.IsNullOrEmpty(appConfig.RelayServer) ? "default" : appConfig.RelayServer)}");
            sb.AppendLine($"Fallback relay: {(string.IsNullOrEmpty(appConfig.FallbackRelayServer) ? "none" : appConfig.FallbackRelayServer)}");
            sb.AppendLine($"SOCKS5: {(string.IsNullOrEmpty(appConfig.Socks5Proxy) ? "none" : appConfig.Socks5Proxy)}");
            sb.AppendLine($"Curve: {appConfig.EncryptionCurve}");
            sb.AppendLine($"No-compress: {appConfig.NoCompress}, Force-local: {appConfig.ForceLocal}, AV-scan: {appConfig.ScanReceivedFiles}");
            Clipboard.SetText(sb.ToString());
            ShowInfoBar("Copied", "Diagnostic info copied to clipboard.", InfoBarSeverity.Success, 3000);
        }
        catch (Exception ex) { ShowInfoBar("Copy failed", ex.Message, InfoBarSeverity.Warning, 4000); }
    }

    private async void ClearAppData_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will delete all settings, transfer history, logs, and cached files.\n\nContinue?",
            "Clear App Data", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        // Cancel any active zip, retries, and kill active transfers
        sendRetryCts?.Cancel(); sendRetryCts?.Dispose(); sendRetryCts = null;
        recvRetryCts?.Cancel(); recvRetryCts?.Dispose(); recvRetryCts = null;
        sendResetTimer?.Stop(); sendResetTimer = null;
        recvResetTimer?.Stop(); recvResetTimer = null;
        HideConnInfo(true);
        HideConnInfo(false);
        zipCts?.Cancel();
        zipCts?.Dispose();
        zipCts = null;
        isZipping = false;
        UpdateClearButtonVisibility();
        KillSendProc(); KillRecvProc();
        sendProc = null;
        recvProc = null;

        // Reset transfer state flags and timers
        isSending = false;
        isReceiving = false;
        StopWaitingDots();
        StopElapsedTimer();
        StopConnectTimeout();

        // Reset send UI
        sendBtn.IsEnabled = false;
        sendCancelBtn.Visibility = Visibility.Collapsed;
        sendProgressPanel.Visibility = Visibility.Collapsed;
        sendRetryPanel.Visibility = Visibility.Collapsed;
        resumeBanner.Visibility = Visibility.Collapsed;
        codeCard.Visibility = Visibility.Collapsed;
        sendStatus.Text = "";
        filePreviewCard.Visibility = Visibility.Collapsed;
        dropZone.Visibility = isTextMode ? Visibility.Collapsed : Visibility.Visible;
        fileModeBtn.IsHitTestVisible = true;
        fileModeBtn.Opacity = 1;
        textModeBtn.IsHitTestVisible = true;
        textModeBtn.Opacity = 1;
        if (isTextMode) sendTextBox.IsReadOnly = false;
        retryRelayOverride = null;

        // Reset receive UI
        recvBtn.IsEnabled = true;
        recvCodeBox.IsEnabled = true;
        recvCancelBtn.Visibility = Visibility.Collapsed;
        recvProgressPanel.Visibility = Visibility.Collapsed;
        recvRetryPanel.Visibility = Visibility.Collapsed;
        recvFilePreviewCard.Visibility = Visibility.Collapsed;
        recvTextCard.Visibility = Visibility.Collapsed;
        recvActivityPanel.Visibility = Visibility.Collapsed;
        recvEmptyState.Visibility = Visibility.Visible;
        recvStatus.Text = "";
        recvCodeBox.Text = "";

        // Give processes a moment to release file locks without blocking UI
        await Task.Delay(500);

        bool fullSuccess = true;
        try
        {
            var appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VIA x4");
            if (Directory.Exists(appDir))
            {
                // Delete everything except the engine binary (VIA.exe) which may be locked
                foreach (var file in Directory.GetFiles(appDir))
                {
                    if (Path.GetFileName(file).Equals("VIA.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); } catch { fullSuccess = false; }
                }
                foreach (var dir in Directory.GetDirectories(appDir))
                {
                    try { Directory.Delete(dir, true); } catch { fullSuccess = false; }
                }
            }
        }
        catch (Exception ex)
        {
            fullSuccess = false;
            LogError("ClearAppData", ex);
        }

        // Clean up stale VIA temp zips from system temp folder
        try
        {
            var tempDir = Path.GetTempPath();
            foreach (var f in Directory.GetFiles(tempDir, "via_*.zip"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }

        // Reset in-memory state to fresh defaults
        appConfig = new AppConfig();
        saveFolder = GetDefaultSaveFolder();
        saveFolderBox.Text = saveFolder;
        selectedFiles.Clear();
        CleanupTempZip();
        LoadSettingsUI();

        // Verify engine is still available (in case something deleted it)
        if (crocPath == null || !File.Exists(crocPath))
            ExtractCroc();

        // Close the settings panel
        settingsPanel.Visibility = Visibility.Collapsed;

        if (fullSuccess)
            ShowInfoBar("Data cleared", "All app data has been reset", InfoBarSeverity.Success, 5000);
        else
            ShowInfoBar("Partial cleanup", "Some files couldn't be deleted (they may be in use). They'll be cleaned up next launch.", InfoBarSeverity.Warning, 5000);
    }

    // ── Cleanup ──────────────────────────────────────────

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!isClosing && (isSending || isReceiving))
        {
            var result = MessageBox.Show(
                "A transfer is still in progress. Close anyway?\n\nThe transfer will be cancelled.",
                "VIA x4", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        isClosing = true;
        Log("VIA x4 shutting down");
        sendRetryCts?.Cancel(); sendRetryCts?.Dispose();
        recvRetryCts?.Cancel(); recvRetryCts?.Dispose();
        zipCts?.Cancel();
        zipCts?.Dispose();
        zipCts = null;
        _directSetupCts?.Cancel();
        _directP2pHelper?.Dispose();
        _directP2pHelper = null;
        StopWaitingDots();
        StopElapsedTimer();
        StopConnectTimeout();
        SystemThemeWatcher.UnWatch(this);
        SaveSettings();
        KillProcessTree(sendProc);
        KillProcessTree(recvProc);
        CleanupTempZip();

        // Clean-on-exit: delete user data but NEVER the engine — it's the core of the program
        if (appConfig.CleanOnExit)
        {
            Log("Clean-on-exit: deleting settings, history, and logs");

            // Clean stale temp files
            try
            {
                var tempDir = Path.GetTempPath();
                foreach (var f in Directory.EnumerateFiles(tempDir, "via_*.zip"))
                    try { File.Delete(f); } catch { }
            }
            catch { }

            // Delete config, history, and logs from AppData
            // Engine in LocalAppData is intentionally preserved — it's required for the app to function
            try
            {
                var appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VIA x4");
                if (Directory.Exists(appDataDir)) Directory.Delete(appDataDir, true);
            }
            catch { }
        }
    }

    private static void KillProcessTree(Process? proc)
    {
        if (proc == null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(true);
                proc.WaitForExit(3000);
            }
        }
        catch { }
    }

    private static bool IsRetryableError(List<string> errors, List<string> lines)
    {
        var allText = string.Join(" ", errors.Concat(lines)).ToLowerInvariant();
        // Hard failures — retrying these will never succeed
        if (allText.Contains("invalid code") || allText.Contains("wrong passphrase") ||
            allText.Contains("permission denied") || allText.Contains("access is denied") ||
            // Disk full — Windows says "not enough space on the disk", Unix says "no space left"
            allText.Contains("disk full") || allText.Contains("no space left") ||
            allText.Contains("not enough space") || allText.Contains("not enough disk space") ||
            // File modified during transfer — hash won't match on retry either
            allText.Contains("hash mismatch") || allText.Contains("checksum") || allText.Contains("corrupted") ||
            // File locked or deleted mid-transfer — retrying won't help
            allText.Contains("being used by another process") ||
            allText.Contains("cannot find the file") || allText.Contains("no such file") ||
            allText.Contains("cannot find the path") || allText.Contains("system cannot find"))
            return false;
        // Everything else is retryable — including empty/unknown errors and DNS failures.
        // DNS failures (no such host, could not resolve) are now retryable because DNS
        // can be transiently unavailable (WiFi reconnect, DNS cache flush).
        // Most croc failures are transient relay/WebSocket/network issues that
        // resolve on retry. Defaulting to retryable is safer than defaulting to
        // hard-fail, because a wasted retry costs seconds while a false hard-fail
        // forces the user to start over with a new code.
        return true;
    }
}
