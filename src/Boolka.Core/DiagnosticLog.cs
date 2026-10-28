using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Boolka;

internal sealed class DiagnosticLog : IDisposable
{
    internal const int MaxFileBytes = 1024 * 1024;
    internal const int MaxFiles = 8;
    private readonly object gate = new();
    private readonly DiagnosticRedactor redactor = new();
    private readonly string folder;
    private readonly string session = Guid.NewGuid().ToString("N");
    // Версию продукта берём у запускаемого UI, а не у вынесенной общей библиотеки.
    private readonly string version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? typeof(AppLog).Assembly.GetName().Version?.ToString() ?? "unknown";
    private FileStream? stream;
    private string? currentFile;
    private int part;
    private bool disposed;
    public bool PreviousCrash { get; private set; }
    private string CrashMarker => Path.Combine(folder, "last-crash.json");

    internal DiagnosticLog(string folder)
    {
        this.folder = Path.GetFullPath(folder);
        BackupFiles.Check(this.folder);
        Directory.CreateDirectory(this.folder);
        BackupFiles.Check(CrashMarker);
        PreviousCrash = File.Exists(CrashMarker);
        Write("info", "app.start", environment: true);
    }

    private sealed record ErrorDetail(string Type, string HResult, string Message, string Stack);

    private ErrorDetail[] Errors(Exception? exception)
    {
        if (exception is null) return [];
        var pending = new Queue<Exception>();
        var result = new List<ErrorDetail>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Enqueue(exception);
        while (pending.TryDequeue(out var error) && result.Count < 6)
        {
            if (!seen.Add(error)) continue;
            var message = error.Message;
            var stack = new StackTrace(error, false).ToString(); // Без имён файлов исходников и путей компьютера разработчика.
            result.Add(new(error.GetType().FullName ?? "Exception", $"0x{error.HResult:X8}",
                redactor.Filter(message[..Math.Min(message.Length, 2048)]), redactor.Filter(stack[..Math.Min(stack.Length, 8192)])));
            if (error is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.Take(6)) pending.Enqueue(inner);
            else if (error.InnerException is { } inner) pending.Enqueue(inner);
        }
        return result.ToArray();
    }

    internal void Write(string level, string eventName, Exception? exception = null, bool environment = false)
    {
        var record = new
        {
            utc = DateTimeOffset.UtcNow, level, @event = redactor.Filter(eventName[..Math.Min(eventName.Length, 256)]), session,
            version, environment = environment ? new { os = RuntimeInformation.OSDescription, runtime = Environment.Version.ToString(), architecture = RuntimeInformation.ProcessArchitecture.ToString() } : null,
            errors = Errors(exception)
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
        if (bytes.Length > 128 * 1024) return;
        lock (gate)
        {
            if (disposed) return;
            if (stream is null || stream.Length + bytes.Length > MaxFileBytes) Rotate();
            stream!.Write(bytes);
            stream.Flush(level != "info");
        }
    }

    private void Rotate()
    {
        var previous = stream;
        stream = null;
        previous?.Dispose();
        currentFile = Path.Combine(folder, $"boolka-{DateTime.UtcNow:yyyyMMddTHHmmss}-{session}-{part++:D3}.log");
        BackupFiles.Check(currentFile);
        stream = new FileStream(currentFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        foreach (var old in LogFiles().Where(x => x.FullName != currentFile).Skip(MaxFiles - 1))
        {
            try { BackupFiles.Check(old.FullName); File.Delete(old.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private IEnumerable<FileInfo> LogFiles() => new DirectoryInfo(folder).EnumerateFiles("boolka-*.log")
        .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Name, "^boolka-[0-9]{8}T[0-9]{6}-[a-f0-9]{32}-[0-9]{3,}\\.log$"))
        .OrderByDescending(x => x.FullName == currentFile).ThenByDescending(x => x.LastWriteTimeUtc).ThenByDescending(x => x.Name);

    internal void MarkCrash()
    {
        lock (gate)
        {
            BackupFiles.Check(CrashMarker);
            using var file = new FileStream(CrashMarker, FileMode.Create, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, new { utc = DateTimeOffset.UtcNow, session, version });
            file.Flush(true);
        }
    }

    internal void AcknowledgeCrash()
    {
        lock (gate)
        {
            BackupFiles.Check(CrashMarker);
            if (File.Exists(CrashMarker)) File.Delete(CrashMarker);
            PreviousCrash = false;
        }
    }

    internal void Export(string destination)
    {
        var contents = new List<(string Name, string Text)>();
        lock (gate)
        {
            stream?.Flush(true);
            foreach (var file in LogFiles().Take(MaxFiles))
            {
                BackupFiles.Check(file.FullName);
                using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (source.Length > MaxFileBytes) continue;
                using var buffer = new MemoryStream();
                var remaining = source.Length;
                var chunk = new byte[64 * 1024];
                while (remaining > 0)
                {
                    var read = source.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                    if (read == 0) break;
                    buffer.Write(chunk, 0, read);
                    remaining -= read;
                }
                contents.Add((file.Name, redactor.Filter(Encoding.UTF8.GetString(buffer.ToArray()))));
            }
        }
        var full = Path.GetFullPath(destination);
        if (!Path.GetExtension(full).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new IOException("Выберите файл ZIP для журнала.");
        BackupFiles.Check(full);
        var temporary = Path.Combine(Path.GetDirectoryName(full)!, ".boolka-report-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    void Add(string name, string text)
                    {
                        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                        writer.Write(text);
                    }
                    Add("README.txt", "boolka diagnostic report\nReview the text files before sharing. No game files, SavedVariables, settings files, or memory dumps are included.\nLogs contain sanitized error messages, stack traces, app version, OS and runtime information. No automatic upload occurs.\n");
                    Add("environment.txt", redactor.Filter($"boolka {version}\nOS: {RuntimeInformation.OSDescription}\n.NET: {Environment.Version}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\nLog files: {contents.Count}\n"));
                    foreach (var entry in contents) Add("logs/" + entry.Name, entry.Text);
                }
                output.Flush(true);
            }
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; stream?.Dispose(); stream = null; }
    }
}
