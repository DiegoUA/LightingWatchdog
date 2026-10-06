using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO.Compression;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkWatchdogService.Models;

namespace NetworkWatchdogService;

/// <summary>What to capture and where to put it. Filled in by the Worker.</summary>
public sealed class EvidenceRequest
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string? ServiceName { get; set; }
    public string? ExecutablePath { get; set; }
    public string Trigger { get; set; } = "manual";          // "auto-breach" | "manual"
    public int Connections { get; set; }
    public int Threshold { get; set; }
    public string DumpMode { get; set; } = "Full";           // Full | Standard | None
    public string BuildMarker { get; set; } = string.Empty;
    public string EvidenceDirectory { get; set; } = string.Empty;   // must already be ACL-secured
    public List<string> RelatedProcesses { get; set; } = new();
    public List<string> HistoryLines { get; set; } = new();
    public string ConfigSnapshotJson { get; set; } = string.Empty;
}

/// <summary>Data is collected (and the process may be killed) before packaging finishes.</summary>
public sealed class PendingEvidenceCase
{
    public string CaseId { get; set; } = string.Empty;
    public string WorkDirectory { get; set; } = string.Empty;
    public string ZipPath { get; set; } = string.Empty;
    public EvidenceCaseInfo Info { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// One-click R&amp;D evidence for a socket/handle leak, captured by the service (SYSTEM) BEFORE the
/// leaking process is terminated. Phase 1 (<see cref="CollectAsync"/>) gathers the data and is the
/// only part that delays the mitigation; phase 2 (<see cref="PackageAsync"/>) hashes and zips and may
/// run after the process has already been restarted.
///
/// Every step is isolated: a failing step is recorded as a warning in the manifest and never stops
/// the others, and nothing here may ever prevent the mitigation itself.
/// </summary>
public static class EvidenceCollector
{
    // ---- dbghelp (same declaration the TrayApp's v3.8.0 collector already used) ----
    [DllImport("DbgHelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess, uint processId, SafeHandle hFile, uint dumpType,
        IntPtr expParam, IntPtr userStreamParam, IntPtr callbackParam);

    private const uint MiniDumpWithFullMemory = 0x00000002;
    private const uint MiniDumpWithHandleData = 0x00000004;
    private const uint MiniDumpWithUnloadedModules = 0x00000020;
    private const uint MiniDumpWithIndirectlyReferencedMemory = 0x00000040;
    private const uint MiniDumpWithProcessThreadData = 0x00000100;
    private const uint MiniDumpWithFullMemoryInfo = 0x00000800;
    private const uint MiniDumpWithThreadInfo = 0x00001000;

    private const int MaxToolOutputChars = 512 * 1024;
    private const int MaxEventRows = 300;

    private static readonly JsonSerializerOptions JsonIndented = new() { WriteIndented = true };

    // ====================================================================================
    // Phase 1: collect
    // ====================================================================================
    public static async Task<PendingEvidenceCase> CollectAsync(EvidenceRequest req, ILogger logger, CancellationToken ct)
    {
        CleanupStaleWork(req.EvidenceDirectory);

        string safeName = SanitizeToken(req.ProcessName);
        string caseId = $"Case_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{safeName}_{req.ProcessId}";
        var pending = new PendingEvidenceCase
        {
            CaseId = caseId,
            WorkDirectory = Path.Combine(req.EvidenceDirectory, ".work_" + Guid.NewGuid().ToString("N")),
            ZipPath = Path.Combine(req.EvidenceDirectory, caseId + ".zip"),
            Info = new EvidenceCaseInfo
            {
                CaseId = caseId,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                ProcessName = req.ProcessName,
                Pid = req.ProcessId,
                Trigger = req.Trigger,
                DumpMode = req.DumpMode
            }
        };
        Directory.CreateDirectory(pending.WorkDirectory);

        var manifest = new Dictionary<string, object?>
        {
            ["Tool"] = "NetworkWatchdogService",
            ["ServiceBuild"] = req.BuildMarker,
            ["CaseId"] = caseId,
            ["CapturedUtc"] = pending.Info.TimestampUtc,
            ["Trigger"] = req.Trigger,
            ["DumpMode"] = req.DumpMode,
            ["Note"] = req.Trigger == "auto-breach"
                ? "Captured automatically BEFORE the process was terminated by the watchdog."
                : "Captured on demand; the process was not terminated."
        };

        await Task.Run(async () =>
        {
            Step(pending, "target-identity", () => manifest["Target"] = DescribeTarget(req));
            Step(pending, "host", () => manifest["Host"] = DescribeHost());
            Step(pending, "target-sockets", () => manifest["TargetSockets"] = WriteTargetSockets(req.ProcessId, pending.WorkDirectory));
            Step(pending, "system-socket-baseline", () => manifest["SystemSockets"] = WriteSystemBaseline(pending.WorkDirectory));
            Step(pending, "process-table", () => WriteProcessTable(pending.WorkDirectory));
            Step(pending, "modules", () => WriteModules(req.ProcessId, pending.WorkDirectory));
            Step(pending, "growth-timeline", () => manifest["Growth"] = WriteGrowth(req.ProcessId, pending.WorkDirectory));
            Step(pending, "memory-and-tcp-counters", () => WriteCounters(pending.WorkDirectory));
            Step(pending, "mitigation-history", () => File.WriteAllLines(Path.Combine(pending.WorkDirectory, "06_MitigationHistory.csv"), req.HistoryLines));
            Step(pending, "config-snapshot", () => File.WriteAllText(Path.Combine(pending.WorkDirectory, "07_ServiceConfig.json"), req.ConfigSnapshotJson));

            // When the time budget is exhausted the remaining (slower) steps are skipped, but
            // everything gathered so far is still kept and packaged.
            if (!ct.IsCancellationRequested)
                Step(pending, "event-logs", () => WriteEventLogs(req, pending.WorkDirectory));
            else
                pending.Warnings.Add("event-logs: skipped (time budget exceeded).");

            if (!ct.IsCancellationRequested)
                await StepAsync(pending, "network-config", () => WriteNetworkConfigAsync(pending.WorkDirectory, ct));
            else
                pending.Warnings.Add("network-config: skipped (time budget exceeded).");

            manifest["RelatedProcessesTerminatedWithTarget"] = req.RelatedProcesses;

            // The memory dump is the largest and slowest part, so it goes last: if the time
            // budget runs out we still keep everything collected above.
            if (string.Equals(req.DumpMode, "None", StringComparison.OrdinalIgnoreCase))
            {
                manifest["Dump"] = "disabled (DumpMode=None)";
            }
            else if (ct.IsCancellationRequested)
            {
                pending.Warnings.Add("dump: skipped (time budget exceeded before it could start).");
                manifest["Dump"] = "skipped (timeout)";
            }
            else
            {
                string dumpPath = Path.Combine(pending.WorkDirectory, "10_ProcessDump.dmp");
                var dumpTask = Task.Run(() => WriteDump(req.ProcessId, dumpPath, req.DumpMode));

                // Linked source so the waiting delay is released as soon as the dump finishes.
                using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var budget = Task.Delay(Timeout.Infinite, budgetCts.Token);
                var finished = await Task.WhenAny(dumpTask, budget);
                budgetCts.Cancel();

                if (finished == dumpTask)
                {
                    try { manifest["Dump"] = await dumpTask; }
                    catch (Exception ex) { pending.Warnings.Add($"dump: {ex.Message}"); logger.LogWarning(ex, "Evidence dump failed."); }
                }
                else
                {
                    // MiniDumpWriteDump cannot be cancelled; it is abandoned. If it was still running when the
                    // process gets killed it simply fails, and any leftover work folder is cleaned up later.
                    pending.Warnings.Add("dump: time budget exceeded; the memory dump was abandoned and is not part of this case.");
                    manifest["Dump"] = "abandoned (timeout)";
                    _ = dumpTask.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
        }, CancellationToken.None).ConfigureAwait(false);

        manifest["Warnings"] = pending.Warnings;
        File.WriteAllText(Path.Combine(pending.WorkDirectory, "00_Manifest.json"), JsonSerializer.Serialize(manifest, JsonIndented));
        return pending;
    }

    // ====================================================================================
    // Phase 2: package (may run after the process was already restarted)
    // ====================================================================================
    public static async Task<EvidenceCaseInfo?> PackageAsync(PendingEvidenceCase pending, ILogger logger)
    {
        try
        {
            return await Task.Run(() =>
            {
                File.WriteAllText(Path.Combine(pending.WorkDirectory, "README_FOR_RD.txt"), BuildReadme(pending));

                // Integrity list so the receiving R&D team can verify nothing was altered in transit.
                var sums = new StringBuilder();
                foreach (var file in Directory.GetFiles(pending.WorkDirectory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (file.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) continue;
                    using var fs = File.OpenRead(file);
                    string hash = Convert.ToHexString(SHA256.HashData(fs));
                    sums.AppendLine($"{hash}  {Path.GetFileName(file)}");
                }
                File.WriteAllText(Path.Combine(pending.WorkDirectory, "SHA256SUMS.txt"), sums.ToString());

                string partial = pending.ZipPath + ".partial";
                if (File.Exists(partial)) File.Delete(partial);
                ZipFile.CreateFromDirectory(pending.WorkDirectory, partial, CompressionLevel.Optimal, includeBaseDirectory: false);
                File.Move(partial, pending.ZipPath, overwrite: true);

                pending.Info.SizeBytes = new FileInfo(pending.ZipPath).Length;
                // Sidecar metadata so listing never has to open the (large) zip.
                File.WriteAllText(Path.ChangeExtension(pending.ZipPath, ".json"), JsonSerializer.Serialize(pending.Info));
                return (EvidenceCaseInfo?)pending.Info;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Packaging evidence case {case} failed.", pending.CaseId);
            return null;
        }
        finally
        {
            try { if (Directory.Exists(pending.WorkDirectory)) Directory.Delete(pending.WorkDirectory, recursive: true); } catch { }
        }
    }

    // ====================================================================================
    // Case management helpers
    // ====================================================================================
    public static bool IsValidCaseId(string? caseId)
        => !string.IsNullOrEmpty(caseId) && caseId.Length <= 160 && caseId.StartsWith("Case_", StringComparison.Ordinal)
           && caseId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-');

    public static string? FindCaseZip(string evidenceDir, string caseId)
    {
        if (!IsValidCaseId(caseId)) return null;
        string path = Path.Combine(evidenceDir, caseId + ".zip");
        return File.Exists(path) ? path : null;
    }

    public static List<EvidenceCaseInfo> ListCases(string evidenceDir)
    {
        var list = new List<EvidenceCaseInfo>();
        try
        {
            if (!Directory.Exists(evidenceDir)) return list;
            foreach (var zip in Directory.GetFiles(evidenceDir, "Case_*.zip"))
            {
                var info = new EvidenceCaseInfo { CaseId = Path.GetFileNameWithoutExtension(zip), SizeBytes = new FileInfo(zip).Length };
                try
                {
                    string meta = Path.ChangeExtension(zip, ".json");
                    if (File.Exists(meta))
                    {
                        var m = JsonSerializer.Deserialize<EvidenceCaseInfo>(File.ReadAllText(meta));
                        if (m != null) { m.CaseId = info.CaseId; m.SizeBytes = info.SizeBytes; info = m; }
                    }
                }
                catch { }
                list.Add(info);
            }
        }
        catch { }
        return list.OrderByDescending(c => c.TimestampUtc, StringComparer.Ordinal).ToList();
    }

    public static void ApplyRetention(string evidenceDir, int maxCases, ILogger logger)
    {
        try
        {
            var zips = Directory.GetFiles(evidenceDir, "Case_*.zip")
                                .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var old in zips.Skip(Math.Max(1, maxCases)))
            {
                try
                {
                    old.Delete();
                    string meta = Path.ChangeExtension(old.FullName, ".json");
                    if (File.Exists(meta)) File.Delete(meta);
                    logger.LogInformation("Evidence retention removed {file}.", old.Name);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Could not delete old evidence case {file}.", old.Name); }
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Evidence retention failed."); }
    }

    private static void CleanupStaleWork(string dir)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(dir, ".work_*"))
                if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddHours(-24))
                    try { Directory.Delete(d, true); } catch { }
            foreach (var p in Directory.GetFiles(dir, "*.partial"))
                if (File.GetLastWriteTimeUtc(p) < DateTime.UtcNow.AddHours(-24))
                    try { File.Delete(p); } catch { }
        }
        catch { }
    }

    // ====================================================================================
    // Individual evidence steps
    // ====================================================================================
    private static void Step(PendingEvidenceCase pending, string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { pending.Warnings.Add($"{name}: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task StepAsync(PendingEvidenceCase pending, string name, Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) { pending.Warnings.Add($"{name}: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static object DescribeTarget(EvidenceRequest req)
    {
        var info = new Dictionary<string, object?>
        {
            ["ProcessName"] = req.ProcessName,
            ["Pid"] = req.ProcessId,
            ["ServiceName"] = req.ServiceName,
            ["ConnectionsAtCapture"] = req.Connections,
            ["RestartThreshold"] = req.Threshold
        };

        try
        {
            using var p = Process.GetProcessById(req.ProcessId);
            info["StartTimeUtc"] = SafeGet(() => p.StartTime.ToUniversalTime().ToString("o"));
            info["Threads"] = SafeGet(() => p.Threads.Count);
            info["HandleCount"] = p.HandleCount;
            info["WorkingSetMB"] = p.WorkingSet64 / (1024 * 1024);
            info["PrivateMB"] = p.PrivateMemorySize64 / (1024 * 1024);
            info["NonpagedPoolKB"] = p.NonpagedSystemMemorySize64 / 1024;
            info["PagedPoolKB"] = p.PagedSystemMemorySize64 / 1024;
        }
        catch (Exception ex) { info["ProcessQueryError"] = ex.Message; }

        string? exe = req.ExecutablePath;
        if (!string.IsNullOrEmpty(exe))
        {
            info["ExecutablePath"] = exe;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe);
                info["Company"] = v.CompanyName;
                info["ProductName"] = v.ProductName;
                info["ProductVersion"] = v.ProductVersion;
                info["FileVersion"] = v.FileVersion;
                info["FileDescription"] = v.FileDescription;
            }
            catch { }
        }

        try
        {
            using var s = new ManagementObjectSearcher($"SELECT CommandLine, ParentProcessId, ExecutablePath FROM Win32_Process WHERE ProcessId = {req.ProcessId}");
            foreach (ManagementObject o in s.Get())
            {
                info["CommandLine"] = o["CommandLine"]?.ToString();
                info["ParentPid"] = o["ParentProcessId"];
                try
                {
                    var owner = o.InvokeMethod("GetOwner", null, null);
                    info["Owner"] = $"{owner?["Domain"]}\\{owner?["User"]}";
                }
                catch { }
                break;
            }
        }
        catch { }
        return info;
    }

    private static object DescribeHost()
    {
        var host = new Dictionary<string, object?>
        {
            ["MachineName"] = Environment.MachineName,
            ["OSVersion"] = Environment.OSVersion.ToString(),
            ["Is64BitOS"] = Environment.Is64BitOperatingSystem,
            ["ProcessorCount"] = Environment.ProcessorCount,
            ["UptimeHours"] = Math.Round(TimeSpan.FromMilliseconds(Environment.TickCount64).TotalHours, 1),
            ["DotNet"] = Environment.Version.ToString()
        };
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption, BuildNumber, Version, TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get())
            {
                host["OSCaption"] = o["Caption"]?.ToString();
                host["OSBuild"] = o["BuildNumber"]?.ToString();
                host["OSVersionWmi"] = o["Version"]?.ToString();
                host["TotalPhysicalMemoryMB"] = Convert.ToInt64(o["TotalVisibleMemorySize"]) / 1024;
                host["FreePhysicalMemoryMB"] = Convert.ToInt64(o["FreePhysicalMemory"]) / 1024;
                break;
            }
        }
        catch { }
        return host;
    }

    private static string TcpStateName(object? state) => Convert.ToInt32(state ?? 0) switch
    {
        1 => "Closed", 2 => "Listen", 3 => "SynSent", 4 => "SynReceived", 5 => "Established",
        6 => "FinWait1", 7 => "FinWait2", 8 => "CloseWait", 9 => "Closing", 10 => "LastAck",
        11 => "TimeWait", 12 => "DeleteTCB", 100 => "Bound", var other => $"State{other}"
    };

    private static object WriteTargetSockets(int pid, string dir)
    {
        var byState = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int total = 0, minPort = int.MaxValue, maxPort = 0;
        var sb = new StringBuilder("LocalAddress,LocalPort,RemoteAddress,RemotePort,State,CreationTime\r\n");

        using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", $"SELECT * FROM MSFT_NetTCPConnection WHERE OwningProcess = {pid}"))
        {
            foreach (ManagementObject o in s.Get())
            {
                string state = TcpStateName(o["State"]);
                byState[state] = byState.TryGetValue(state, out int c) ? c + 1 : 1;
                total++;
                int lp = Convert.ToInt32(o["LocalPort"] ?? 0);
                if (lp > 0) { minPort = Math.Min(minPort, lp); maxPort = Math.Max(maxPort, lp); }
                string created = string.Empty;
                try { created = ManagementDateTimeConverter.ToDateTime(o["CreationTime"]?.ToString()).ToUniversalTime().ToString("o"); } catch { }
                sb.Append($"{o["LocalAddress"]},{o["LocalPort"]},{o["RemoteAddress"]},{o["RemotePort"]},{state},{created}\r\n");
            }
        }
        File.WriteAllText(Path.Combine(dir, "01_TargetTcpConnections.csv"), sb.ToString());

        int udp = 0;
        var usb = new StringBuilder("LocalAddress,LocalPort,CreationTime\r\n");
        try
        {
            using var u = new ManagementObjectSearcher(@"root\StandardCimv2", $"SELECT * FROM MSFT_NetUDPEndpoint WHERE OwningProcess = {pid}");
            foreach (ManagementObject o in u.Get())
            {
                udp++;
                string created = string.Empty;
                try { created = ManagementDateTimeConverter.ToDateTime(o["CreationTime"]?.ToString()).ToUniversalTime().ToString("o"); } catch { }
                usb.Append($"{o["LocalAddress"]},{o["LocalPort"]},{created}\r\n");
            }
        }
        catch { }
        File.WriteAllText(Path.Combine(dir, "01b_TargetUdpEndpoints.csv"), usb.ToString());

        return new Dictionary<string, object?>
        {
            ["TcpTotal"] = total,
            ["TcpByState"] = byState,
            ["UdpEndpoints"] = udp,
            ["LocalPortRangeUsed"] = total > 0 ? $"{minPort}-{maxPort}" : "n/a",
            ["Interpretation"] = byState.TryGetValue("Bound", out int bound) && total > 0
                ? $"{bound} of {total} TCP rows are in the 'Bound' state (bind() without connect/listen/close)."
                : "No 'Bound' sockets found."
        };
    }

    private static object WriteSystemBaseline(string dir)
    {
        var perPid = new Dictionary<int, int>();
        var byState = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int total = 0;

        using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT OwningProcess, State FROM MSFT_NetTCPConnection"))
        {
            foreach (ManagementObject o in s.Get())
            {
                total++;
                int pid = Convert.ToInt32(o["OwningProcess"] ?? 0);
                perPid[pid] = perPid.TryGetValue(pid, out int c) ? c + 1 : 1;
                string st = TcpStateName(o["State"]);
                byState[st] = byState.TryGetValue(st, out int sc) ? sc + 1 : 1;
            }
        }

        var sb = new StringBuilder("PID,ProcessName,TcpRows\r\n");
        foreach (var kv in perPid.OrderByDescending(k => k.Value).Take(50))
        {
            string name = "?";
            try { using var p = Process.GetProcessById(kv.Key); name = p.ProcessName; } catch { }
            sb.Append($"{kv.Key},{name},{kv.Value}\r\n");
        }
        File.WriteAllText(Path.Combine(dir, "02_SystemTcpTop50ByProcess.csv"), sb.ToString());

        return new Dictionary<string, object?> { ["TcpTotal"] = total, ["TcpByState"] = byState, ["ProcessesWithSockets"] = perPid.Count };
    }

    private static void WriteProcessTable(string dir)
    {
        var rows = new List<(int Pid, string Name, string Start, int Threads, int Handles, long WsMb, long PrivMb, long NpKb, long PagedKb)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string start = SafeGet(() => p.StartTime.ToUniversalTime().ToString("o")) ?? "";
                int threads = SafeGet(() => p.Threads.Count);
                rows.Add((p.Id, p.ProcessName, start, threads, p.HandleCount,
                          p.WorkingSet64 / (1024 * 1024), p.PrivateMemorySize64 / (1024 * 1024),
                          p.NonpagedSystemMemorySize64 / 1024, p.PagedSystemMemorySize64 / 1024));
            }
            catch { }
            finally { p.Dispose(); }
        }

        var sb = new StringBuilder("PID,ProcessName,StartTimeUtc,Threads,Handles,WorkingSetMB,PrivateMB,NonpagedPoolKB,PagedPoolKB\r\n");
        foreach (var r in rows.OrderByDescending(r => r.Handles))
            sb.Append($"{r.Pid},{r.Name},{r.Start},{r.Threads},{r.Handles},{r.WsMb},{r.PrivMb},{r.NpKb},{r.PagedKb}\r\n");
        File.WriteAllText(Path.Combine(dir, "03_ProcessTable_ByHandles.csv"), sb.ToString());
    }

    private static void WriteModules(int pid, string dir)
    {
        var sb = new StringBuilder("Module,Path,Company,FileVersion,SizeKB\r\n");
        using var p = Process.GetProcessById(pid);
        int n = 0;
        foreach (ProcessModule m in p.Modules)
        {
            if (++n > 2000) break;
            string company = "", version = "";
            try { var v = m.FileVersionInfo; company = v.CompanyName ?? ""; version = v.FileVersion ?? ""; } catch { }
            sb.Append($"{m.ModuleName},{m.FileName?.Replace(',', ';')},{company.Replace(',', ';')},{version.Replace(',', ';')},{m.ModuleMemorySize / 1024}\r\n");
        }
        File.WriteAllText(Path.Combine(dir, "04_LoadedModules.csv"), sb.ToString());
    }

    private static object WriteGrowth(int pid, string dir)
    {
        var rows = TelemetryExporter.ReadHealthSnapshots(pid, 1000);
        var sb = new StringBuilder("TimestampUtc,Connections\r\n");
        foreach (var r in rows) sb.Append($"{r.TimeUtc:o},{r.Connections}\r\n");
        File.WriteAllText(Path.Combine(dir, "05_ConnectionGrowthTimeline.csv"), sb.ToString());

        if (rows.Count < 2) return new Dictionary<string, object?> { ["Samples"] = rows.Count, ["Note"] = "Not enough samples to compute a growth rate." };

        var first = rows[0]; var last = rows[^1];
        double minutes = (last.TimeUtc - first.TimeUtc).TotalMinutes;
        return new Dictionary<string, object?>
        {
            ["Samples"] = rows.Count,
            ["FirstSampleUtc"] = first.TimeUtc.ToString("o"),
            ["LastSampleUtc"] = last.TimeUtc.ToString("o"),
            ["ConnectionsAtFirstSample"] = first.Connections,
            ["ConnectionsAtLastSample"] = last.Connections,
            ["ObservedMinutes"] = Math.Round(minutes, 1),
            ["AverageGrowthPerMinute"] = minutes > 0 ? Math.Round((last.Connections - first.Connections) / minutes, 1) : null
        };
    }

    private static void WriteCounters(string dir)
    {
        var sb = new StringBuilder();
        void Dump(string title, string wql, string[] props)
        {
            sb.AppendLine($"== {title} ==");
            try
            {
                using var s = new ManagementObjectSearcher(wql);
                foreach (ManagementObject o in s.Get())
                    foreach (var pr in props) sb.AppendLine($"{pr} = {o[pr]}");
            }
            catch (Exception ex) { sb.AppendLine($"(unavailable: {ex.Message})"); }
            sb.AppendLine();
        }

        // Kernel pool pressure is what ultimately breaks when sockets leak (see the legacy
        // PowerShell diagnostics, whose pool metric read a WMI property that does not exist).
        Dump("Memory / kernel pools", "SELECT * FROM Win32_PerfFormattedData_PerfOS_Memory",
             new[] { "AvailableMBytes", "PoolNonpagedBytes", "PoolNonpagedAllocs", "PoolPagedBytes", "PoolPagedAllocs", "CommittedBytes" });
        Dump("TCPv4", "SELECT * FROM Win32_PerfFormattedData_Tcpip_TCPv4",
             new[] { "ConnectionsEstablished", "ConnectionsActive", "ConnectionsPassive", "ConnectionFailures", "ConnectionsReset" });
        Dump("UDPv4", "SELECT * FROM Win32_PerfFormattedData_Tcpip_UDPv4",
             new[] { "DatagramsReceivedPerSec", "DatagramsSentPerSec", "DatagramsReceivedErrors", "DatagramsNoPortPerSec" });
        File.WriteAllText(Path.Combine(dir, "08_KernelAndTcpCounters.txt"), sb.ToString());
    }

    private static void WriteEventLogs(EvidenceRequest req, string dir)
    {
        var sb = new StringBuilder("TimeUtc,Log,Provider,EventId,Level,Message\r\n");

        void Query(string log, string xpath, int max)
        {
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath));
                int n = 0;
                for (EventRecord? e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
                {
                    using (e)
                    {
                        string msg = "";
                        try { msg = (e.FormatDescription() ?? "").Replace("\r", " ").Replace("\n", " ").Replace('"', '\''); } catch { }
                        if (msg.Length > 500) msg = msg.Substring(0, 500);
                        sb.Append($"{e.TimeCreated?.ToUniversalTime():o},{log},{e.ProviderName},{e.Id},{e.LevelDisplayName},\"{msg}\"\r\n");
                    }
                    if (++n >= max) break;
                }
            }
            catch (Exception ex) { sb.Append($",{log},(query failed: {ex.Message.Replace(',', ';')}),,,\r\n"); }
        }

        // Errors/warnings in the last 2 hours, plus TCP/IP port-exhaustion events (Tcpip 4227/4231)
        // and Service Control Manager events for the last 24 hours.
        const string recentProblems = "*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= 7200000]]]";
        Query("System", recentProblems, MaxEventRows);
        Query("Application", recentProblems, MaxEventRows);
        Query("System", "*[System[Provider[@Name='Tcpip' or @Name='Microsoft-Windows-TCPIP'] and TimeCreated[timediff(@SystemTime) <= 86400000]]]", 200);
        Query("System", "*[System[Provider[@Name='Service Control Manager'] and TimeCreated[timediff(@SystemTime) <= 86400000]]]", 200);

        File.WriteAllText(Path.Combine(dir, "09_EventLogs.csv"), sb.ToString());
    }

    private static async Task WriteNetworkConfigAsync(string dir, CancellationToken ct)
    {
        var sb = new StringBuilder();
        // Fixed commands and arguments only: no caller-supplied text ever reaches a process.
        var tools = new (string Exe, string Args)[]
        {
            ("netsh.exe", "int ipv4 show dynamicport tcp"),
            ("netsh.exe", "int ipv4 show dynamicport udp"),
            ("netsh.exe", "int ipv4 show excludedportrange protocol=tcp"),
            ("netsh.exe", "int tcp show global"),
            ("netsh.exe", "winsock show catalog"),
            ("netstat.exe", "-s -p tcp"),
            ("ipconfig.exe", "/all")
        };
        foreach (var (exe, args) in tools)
        {
            ct.ThrowIfCancellationRequested();
            sb.AppendLine($"===== {exe} {args} =====");
            sb.AppendLine(await RunToolAsync(exe, args, 20, ct).ConfigureAwait(false));
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(dir, "11_NetworkConfiguration.txt"), sb.ToString());
    }

    private static async Task<string> RunToolAsync(string exe, string args, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, exe),
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "(could not start)";

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try { await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return "(timed out)";
            }

            string text = (await stdout) + (await stderr);
            return text.Length > MaxToolOutputChars ? text.Substring(0, MaxToolOutputChars) + "\r\n(truncated)" : text;
        }
        catch (Exception ex) { return $"(failed: {ex.Message})"; }
    }

    private static object WriteDump(int pid, string path, string mode)
    {
        uint flags = string.Equals(mode, "Standard", StringComparison.OrdinalIgnoreCase)
            ? MiniDumpWithHandleData | MiniDumpWithUnloadedModules | MiniDumpWithIndirectlyReferencedMemory |
              MiniDumpWithProcessThreadData | MiniDumpWithFullMemoryInfo | MiniDumpWithThreadInfo
            : MiniDumpWithFullMemory | MiniDumpWithHandleData | MiniDumpWithUnloadedModules |
              MiniDumpWithFullMemoryInfo | MiniDumpWithThreadInfo;

        using var proc = Process.GetProcessById(pid);
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        bool ok = MiniDumpWriteDump(proc.Handle, (uint)pid, fs.SafeFileHandle, flags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            fs.Close();
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException($"MiniDumpWriteDump failed (Win32 error {err}).");
        }
        fs.Flush();
        return new Dictionary<string, object?> { ["Mode"] = mode, ["Flags"] = $"0x{flags:X}", ["SizeMB"] = fs.Length / (1024 * 1024) };
    }

    private static string BuildReadme(PendingEvidenceCase pending) => $@"NetworkWatchdogService - R&D evidence case {pending.CaseId}
================================================================

WHAT THIS IS
  Automatic evidence for a TCP/UDP socket leak, collected by the NetworkWatchdogService
  Windows service. For an automatic case it was captured BEFORE the leaking process was
  terminated, so the dump and socket tables show the leak in progress.

FILES
  00_Manifest.json                  Who/what/when: target process, versions, host, thresholds,
                                    socket-state histogram, growth rate, warnings for any step that failed.
  01_TargetTcpConnections.csv       Every TCP row owned by the leaking process (address, port, state).
                                    State 'Bound' = bind() with no connect/listen/close (typical leak signature).
  01b_TargetUdpEndpoints.csv        UDP endpoints owned by the process.
  02_SystemTcpTop50ByProcess.csv    Baseline: which processes hold the most TCP rows system-wide.
  03_ProcessTable_ByHandles.csv     All processes with handle/thread/memory/pool usage.
  04_LoadedModules.csv              DLLs loaded in the target (name, path, vendor, version).
  05_ConnectionGrowthTimeline.csv   Connection count over time (watchdog samples) for the target.
  06_MitigationHistory.csv          Previous automatic restarts recorded by the watchdog.
  07_ServiceConfig.json             Watchdog thresholds/policy in force at capture time.
  08_KernelAndTcpCounters.txt       Non-paged/paged pool and TCP/UDP counters.
  09_EventLogs.csv                  Recent System/Application errors, Tcpip (port exhaustion) and SCM events.
  10_ProcessDump.dmp                Process memory dump (if enabled; see DumpMode in the manifest).
  11_NetworkConfiguration.txt       Dynamic port range, excluded ports, TCP globals, Winsock catalog, TCP statistics, adapters.
  SHA256SUMS.txt                    SHA-256 of every file, to verify integrity after transfer.

ANALYSIS HINTS (WinDbg)
  Open 10_ProcessDump.dmp, then for example:  !analyze -v   and   !handle 0 f
  A very large number of File handles whose names are \Device\Afd are Winsock endpoints:
  those are the leaked sockets. Compare their count with 01_TargetTcpConnections.csv.

PRIVACY
  The dump contains the target process's memory and may contain credentials or personal data.
  Command lines, adapter addresses and event messages are included as-is. Share only with the
  vendor/engineers you intend to.
";

    // ====================================================================================
    // small helpers
    // ====================================================================================
    private static string SanitizeToken(string value)
    {
        var s = new string((value ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        if (s.Length == 0) s = "process";
        return s.Length > 40 ? s.Substring(0, 40) : s;
    }

    private static T SafeGet<T>(Func<T> f)
    {
        try { return f(); } catch { return default!; }
    }
}
