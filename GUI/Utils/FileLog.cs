using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace GUI.Utils;

/// <summary>
/// Mirrors everything that reaches the in-app console tab into a plain text file.
/// <para>
/// The viewer is a WinExe, so it has no stdout to redirect: the console tab is the only place log
/// output ever appears, and it dies with the process. This sink is the machine readable copy.
/// </para>
/// <para>
/// Writes are flushed on every line so the messages immediately before a crash are on disk, and are
/// serialized behind a lock because the render thread, the UI thread and decode threads all log.
/// Set the <c>VRF_LOG_FILE</c> environment variable to <c>0</c> to opt out, or to a directory path
/// to write the logs somewhere else.
/// </para>
/// </summary>
internal static class FileLog
{
    private const string EnvironmentVariable = "VRF_LOG_FILE";
    private const string FolderName = "Source2Viewer";
    private const string LogsFolderName = "logs";

    /// <summary>How many previous session logs are kept around before the oldest ones are deleted.</summary>
    private const int MaxSessionFiles = 10;

    /// <summary>Hard cap for a single session, so a message loop that spams cannot fill the disk.</summary>
    private const long MaxSessionBytes = 64 * 1024 * 1024;

    /// <summary>How many records are held before <see cref="Install"/> runs, so early messages are not lost.</summary>
    private const int MaxPendingRecords = 1024;

    private readonly record struct Record(DateTime Time, Log.Category Category, string Component, string Message);

    private static readonly Lock Sync = new();
    private static readonly List<Record> Pending = [];

    private static StreamWriter? Writer;
    private static TextWriter? OutTee;
    private static TextWriter? ErrorTee;
    private static bool Disabled;
    private static bool Installed;
    private static bool ConsoleRedirected;
    private static bool CapReached;
    private static long BytesWritten;
    private static long RecordsWritten;

    /// <summary>Gets the full path of the log file for this session, or null when logging to a file is off.</summary>
    public static string? FilePath { get; private set; }

    /// <summary>
    /// Opens the log file for this session and drains anything that was logged before now.
    /// Safe to call more than once; only the first call does anything.
    /// </summary>
    public static void Install()
    {
        lock (Sync)
        {
            if (Installed)
            {
                return;
            }

            Installed = true;

            var folder = ResolveFolder();

            if (folder == null)
            {
                Disabled = true;
                Pending.Clear();
                return;
            }

            try
            {
                Directory.CreateDirectory(folder);
                PruneOldLogs(folder);

                var name = string.Create(CultureInfo.InvariantCulture, $"Source2Viewer-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
                var path = Path.Combine(folder, name);

                // FileShare.ReadWrite lets the user tail the file while the viewer is running, and lets a
                // second instance prune it. AutoFlush hands every line to the OS, which is what makes the
                // file useful after a hard crash.
                Writer = new StreamWriter(
                    new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };

                FilePath = path;

                WriteLineLocked($"# Source 2 Viewer log started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} (process {Environment.ProcessId})");
                WriteLineLocked($"# {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}) on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
                WriteLineLocked($"# command line: {Environment.CommandLine}");
            }
            catch (Exception e)
            {
                Disabled = true;
                Writer = null;
                FilePath = null;
                Pending.Clear();

                // Nothing better to do here, the file log is the thing that failed.
                System.Diagnostics.Debug.WriteLine($"Failed to open the log file: {e}");
                return;
            }

            foreach (var record in Pending)
            {
                WriteRecordLocked(record);
            }

            Pending.Clear();
        }
    }

    /// <summary>
    /// Records a message. Called for every <see cref="Log"/> call, on whatever thread made it.
    /// Before <see cref="Install"/> runs the record is buffered instead of dropped.
    /// </summary>
    public static void Write(Log.Category category, string component, string message)
    {
        lock (Sync)
        {
            if (Disabled)
            {
                return;
            }

            var record = new Record(DateTime.Now, category, component, message);

            if (Writer == null)
            {
                if (Pending.Count < MaxPendingRecords)
                {
                    Pending.Add(record);
                }

                return;
            }

            WriteRecordLocked(record);
        }
    }

    /// <summary>
    /// Records a message in the file without showing it in the console tab. Used for process level
    /// diagnostics that the UI never displayed before this sink existed.
    /// </summary>
    public static void WriteDiagnostic(Log.Category category, string component, string message)
        => Write(category, component, message);

    /// <summary>
    /// Tees <see cref="Console"/> output into the file. Plenty of the library and renderer code writes
    /// with <see cref="Console.WriteLine(string)"/> rather than through <see cref="Log"/>, and the console
    /// tab redirects those into itself; this wraps whatever it installed so the file sees them too.
    /// Must run after the console tab has taken over the streams.
    /// </summary>
    public static void AttachConsoleRedirect()
    {
        lock (Sync)
        {
            if (ConsoleRedirected || Disabled || Writer == null)
            {
                return;
            }

            ConsoleRedirected = true;
        }

        // Owned by Console for the rest of the process; kept here so the references do not look stray.
        OutTee = new TeeTextWriter(Console.Out, Log.Category.INFO);
        ErrorTee = new TeeTextWriter(Console.Error, Log.Category.ERROR);

        Console.SetOut(OutTee);
        Console.SetError(ErrorTee);
    }

    /// <summary>Flushes and closes the log file. An empty log is deleted rather than kept.</summary>
    public static void Shutdown()
    {
        lock (Sync)
        {
            if (Writer == null)
            {
                return;
            }

            var path = FilePath;
            var empty = RecordsWritten == 0;

            try
            {
                WriteLineLocked($"# Log closed {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                Writer.Dispose();
            }
            catch (Exception)
            {
                //
            }

            Writer = null;
            Disabled = true;

            if (empty && path != null)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    //
                }
            }
        }
    }

    private static string? ResolveFolder()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);

        if (configured != null)
        {
            configured = configured.Trim();

            if (configured.Length == 0
                || configured.Equals("0", StringComparison.Ordinal)
                || configured.Equals("off", StringComparison.OrdinalIgnoreCase)
                || configured.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return configured;
        }

        // Same root the settings file uses: per user, writable without elevation, and not wiped by a
        // temp folder cleaner the way %TEMP% is.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            FolderName,
            LogsFolderName);
    }

    private static void PruneOldLogs(string folder)
    {
        try
        {
            var existing = Directory.GetFiles(folder, "Source2Viewer-*.log")
                .OrderByDescending(static path => path, StringComparer.Ordinal)
                .Skip(MaxSessionFiles - 1);

            foreach (var old in existing)
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception)
                {
                    // Most likely open by another running instance.
                }
            }
        }
        catch (Exception)
        {
            //
        }
    }

    private static void WriteRecordLocked(Record record)
    {
        var level = record.Category switch
        {
            Log.Category.DEBUG => "DEBUG",
            Log.Category.INFO => "INFO ",
            Log.Category.WARN => "WARN ",
            Log.Category.ERROR => "ERROR",
            _ => "?????",
        };

        WriteLineLocked(string.Create(CultureInfo.InvariantCulture, $"{record.Time:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{record.Component}] {record.Message}"));
        RecordsWritten++;
    }

    private static void WriteLineLocked(string line)
    {
        if (Writer == null || CapReached)
        {
            return;
        }

        try
        {
            Writer.WriteLine(line);

            BytesWritten += line.Length + Environment.NewLine.Length;

            if (BytesWritten > MaxSessionBytes)
            {
                CapReached = true;
                Writer.WriteLine($"# Log stopped: this session exceeded {MaxSessionBytes / (1024 * 1024)} MB.");
            }
        }
        catch (Exception)
        {
            // A disk that stopped taking writes must not take the viewer down with it.
            CapReached = true;
        }
    }

    /// <summary>
    /// Passes console output through to whatever the console tab installed and copies each line to the
    /// file. Only <see cref="WriteLine(string)"/> is copied, because that is the only call the console
    /// tab itself acts on.
    /// </summary>
    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter Inner;
        private readonly Log.Category Category;

        public TeeTextWriter(TextWriter inner, Log.Category category)
        {
            Inner = inner;
            Category = category;
        }

        public override Encoding Encoding => Inner.Encoding;

        public override IFormatProvider FormatProvider => Inner.FormatProvider;

        public override void Write(char value) => Inner.Write(value);

        public override void Write(string? value) => Inner.Write(value);

        public override void WriteLine(string? value)
        {
            Inner.WriteLine(value);

            if (value != null)
            {
                FileLog.Write(Category, "Console", value);
            }
        }

        public override void Flush() => Inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
