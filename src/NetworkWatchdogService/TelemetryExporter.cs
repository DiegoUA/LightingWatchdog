using System.Text;

namespace NetworkWatchdogService;

/// <summary>
/// Append-only CSV trail of per-process connection counts (HealthTrend_v2.csv) and
/// mitigations (RestartEvents_v2.csv). Previously this class was never called and wrote under
/// the install directory (Program Files). It now lives in the same ACL-locked
/// %ProgramData%\NetworkWatchdogService folder as the other service data, is size-capped, and
/// feeds the leak-growth timeline in R&amp;D evidence bundles.
/// Every method is best-effort: telemetry export must never affect detection or restarts.
/// </summary>
public static class TelemetryExporter
{
    private const long MaxFileBytes = 10L * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _directory;

    private static string HealthTrendFile => Path.Combine(_directory ?? string.Empty, "HealthTrend_v2.csv");
    private static string RestartFile => Path.Combine(_directory ?? string.Empty, "RestartEvents_v2.csv");

    /// <summary>Called once by the service with an already-secured directory. Until then every
    /// method is a no-op (so the exporter can never write to an unsecured location).</summary>
    public static void Initialize(string secureDirectory)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(secureDirectory);
                _directory = secureDirectory;
            }
            catch { _directory = null; }
        }
    }

    public static void RecordHealthSnapshot(string serviceName, int pid, int connectionCount)
        => Append(HealthTrendFile, "Timestamp,ServiceName,PID,ActiveConnections",
                  $"{DateTime.UtcNow:O},{Sanitize(serviceName)},{pid},{connectionCount}");

    public static void RecordRestart(string serviceName, int pid, string reason)
        => Append(RestartFile, "Timestamp,ServiceName,PID,Action,Reason",
                  $"{DateTime.UtcNow:O},{Sanitize(serviceName)},{pid},RESTART,{Sanitize(reason)}");

    /// <summary>Returns the most recent snapshots for one PID as (UTC time, connections).</summary>
    public static List<(DateTime TimeUtc, int Connections)> ReadHealthSnapshots(int pid, int maxRows)
    {
        var result = new List<(DateTime, int)>();
        try
        {
            string file;
            lock (Gate) { if (_directory == null) return result; file = HealthTrendFile; }
            if (!File.Exists(file)) return result;

            var tail = new Queue<(DateTime, int)>(maxRows + 1);
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var p = line.Split(',');
                if (p.Length < 4) continue;
                if (!int.TryParse(p[2], out int rowPid) || rowPid != pid) continue;
                if (!DateTime.TryParse(p[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t)) continue;
                if (!int.TryParse(p[3], out int count)) continue;
                tail.Enqueue((t.ToUniversalTime(), count));
                if (tail.Count > maxRows) tail.Dequeue();
            }
            result.AddRange(tail);
        }
        catch { }
        return result;
    }

    private static string Sanitize(string value)
        => (value ?? string.Empty).Replace(',', '_').Replace('\r', ' ').Replace('\n', ' ');

    private static void Append(string path, string header, string record)
    {
        try
        {
            lock (Gate)
            {
                if (_directory == null) return;

                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxFileBytes)
                    File.Move(path, path + ".1", overwrite: true);

                bool newFile = !File.Exists(path);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                if (newFile) sw.WriteLine(header);
                sw.WriteLine(record);
            }
        }
        catch { /* never let telemetry export affect the service */ }
    }
}
