<#
.SYNOPSIS
    Verifies, on a real Windows machine, the native/OS calls that NetworkWatchdog v3.9.0 relies on and that
    could not be compiled or run where the code was written.

.DESCRIPTION
    Uses the SAME P/Invoke declarations as the service/tray code, so a PASS here means those declarations work
    on this machine. Run it in an elevated PowerShell (Run as administrator) while NetworkWatchdogService is
    running. Nothing is changed on the system; temporary files and a short-lived helper process are cleaned up.

      TEST 1  Pipe-server identity   (TrayApp: GetNamedPipeServerProcessId)
      TEST 2  Restart Manager        (service: file-lock discovery of related processes)
      TEST 3  MiniDumpWriteDump      (service: pre-kill memory dump)
      TEST 4  Dependent services     (service: WMI Win32_DependentService association)

    A report is written to your Desktop: NativeInterop-TestReport.txt  (send it back for review).

.PARAMETER TargetExePath
    Optional. Full path of a program you want TEST 2 to also check for lockers
    (e.g. the LightingService.exe path), in addition to the built-in synthetic test.

.EXAMPLE
    Set-ExecutionPolicy -Scope Process Bypass -Force
    .\Test-NativeInterop.ps1
    .\Test-NativeInterop.ps1 -TargetExePath "C:\Program Files (x86)\LightingService\LightingService.exe"
#>
[CmdletBinding()]
param([string]$TargetExePath = "")

$ErrorActionPreference = 'Continue'
$report = New-Object System.Collections.Generic.List[string]
$results = [ordered]@{}

function Log([string]$text) { Write-Host $text; $report.Add($text) }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Log "NetworkWatchdog native interop test  -  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Log "Machine: $env:COMPUTERNAME   OS: $([Environment]::OSVersion.VersionString)   PowerShell: $($PSVersionTable.PSVersion)   64-bit process: $([Environment]::Is64BitProcess)"
Log "Elevated: $isAdmin"
if (-not $isAdmin) { Log "WARNING: not elevated. Tests 2-4 may fail for permission reasons; please re-run as Administrator." }
Log ""

# ----------------------------------------------------------------------------------------------
# C# helpers (C# 5 syntax so they also compile in Windows PowerShell 5.1)
# ----------------------------------------------------------------------------------------------
$source = @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class PipeNative
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeServerProcessId(IntPtr hNamedPipe, out uint serverProcessId);
}

public static class RmNative
{
    const int CCH_RM_SESSION_KEY = 32;
    const int CCH_RM_MAX_APP_NAME = 255;
    const int CCH_RM_MAX_SVC_NAME = 63;

    enum RM_APP_TYPE { RmUnknownApp = 0, RmMainWindow = 1, RmOtherWindow = 2, RmService = 3, RmExplorer = 4, RmConsole = 5, RmCritical = 1000 }

    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);
    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);
    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

    // Returns "pid|application name" lines, or a single "ERROR|..." line.
    public static string[] GetLockers(string path)
    {
        List<string> result = new List<string>();
        uint handle = 0;
        bool started = false;
        try
        {
            StringBuilder key = new StringBuilder(CCH_RM_SESSION_KEY + 1);
            int res = RmStartSession(out handle, 0, key);
            if (res != 0) return new string[] { "ERROR|RmStartSession returned " + res };
            started = true;

            res = RmRegisterResources(handle, 1, new string[] { path }, 0, null, 0, null);
            if (res != 0) return new string[] { "ERROR|RmRegisterResources returned " + res };

            uint needed = 0, count = 0, reasons = 0;
            res = RmGetList(handle, out needed, ref count, null, ref reasons);
            if (res != 0 && res != 234) return new string[] { "ERROR|RmGetList(sizing) returned " + res };
            if (needed == 0) return result.ToArray();

            RM_PROCESS_INFO[] info = new RM_PROCESS_INFO[needed];
            count = needed;
            res = RmGetList(handle, out needed, ref count, info, ref reasons);
            if (res != 0) return new string[] { "ERROR|RmGetList returned " + res };

            for (int i = 0; i < count; i++)
                result.Add(info[i].Process.dwProcessId + "|" + info[i].strAppName);
        }
        catch (Exception ex)
        {
            return new string[] { "ERROR|" + ex.GetType().Name + ": " + ex.Message };
        }
        finally
        {
            if (started) { try { RmEndSession(handle); } catch (Exception) { } }
        }
        return result.ToArray();
    }
}

public static class DumpNative
{
    [DllImport("DbgHelp.dll", SetLastError = true)]
    static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, SafeHandle hFile, uint dumpType,
        IntPtr expParam, IntPtr userStreamParam, IntPtr callbackParam);

    // Same flag sets as EvidenceCollector.cs "Standard" and "Full".
    public const uint Standard = 0x4 | 0x20 | 0x40 | 0x100 | 0x800 | 0x1000;
    public const uint Full     = 0x2 | 0x4 | 0x20 | 0x800 | 0x1000;

    public static string Write(int pid, string path, uint flags)
    {
        using (Process p = Process.GetProcessById(pid))
        using (FileStream fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            bool ok = MiniDumpWriteDump(p.Handle, (uint)pid, fs.SafeFileHandle, flags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (!ok) return "ERROR|MiniDumpWriteDump failed, Win32 error " + Marshal.GetLastWin32Error();
            fs.Flush();
            return "OK|" + fs.Length;
        }
    }
}
"@
try { Add-Type -TypeDefinition $source -Language CSharp -ErrorAction Stop }
catch { Log "FATAL: could not compile the test helpers: $($_.Exception.Message)"; $report | Set-Content "$env:USERPROFILE\Desktop\NativeInterop-TestReport.txt"; exit 1 }

# ----------------------------------------------------------------------------------------------
# TEST 1 - pipe server identity
# ----------------------------------------------------------------------------------------------
Log "=== TEST 1: pipe-server identity (GetNamedPipeServerProcessId) ==="
try {
    $svcProc = Get-Process -Name NetworkWatchdogService -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $svcProc) {
        Log "  NetworkWatchdogService is NOT running - start it and re-run. (Get-Service NetworkWatchdogService)"
        $results['1 Pipe identity'] = 'SKIPPED (service not running)'
    } else {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'NetworkWatchdogTelemetry', [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(3000)
        [uint32]$serverPid = 0
        $ok = [PipeNative]::GetNamedPipeServerProcessId($pipe.SafePipeHandle.DangerousGetHandle(), [ref]$serverPid)
        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $pipe.Dispose()

        if (-not $ok) {
            Log "  API call FAILED (Win32 error $err)."
            $results['1 Pipe identity'] = "FAIL (API error $err)"
        } else {
            $owner = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
            Log "  Pipe server PID reported by Windows : $serverPid"
            Log "  Process name of that PID            : $($owner.ProcessName)"
            Log "  Actual NetworkWatchdogService PID   : $($svcProc.Id)"
            if ($serverPid -eq $svcProc.Id -and $owner.ProcessName -eq 'NetworkWatchdogService') {
                $results['1 Pipe identity'] = 'PASS'
            } else {
                $results['1 Pipe identity'] = 'FAIL (reported PID/name does not match the service - the tray would stay grey)'
            }
        }
    }
} catch {
    Log "  Exception: $($_.Exception.Message)"
    $results['1 Pipe identity'] = "FAIL (exception: $($_.Exception.Message))"
}
Log "  -> $($results['1 Pipe identity'])"
Log ""

# ----------------------------------------------------------------------------------------------
# TEST 2 - Restart Manager (synthetic lock + optional real target)
# ----------------------------------------------------------------------------------------------
Log "=== TEST 2: Restart Manager (file-lock discovery) ==="
$lockFile = Join-Path $env:TEMP ("nw-rm-test-{0}.tmp" -f ([guid]::NewGuid().ToString('N')))
$holder = $null
try {
    Set-Content -Path $lockFile -Value 'lock test'
    # A SEPARATE process holds the file open exclusively for 25 seconds.
    $holderCmd = "`$fs=[System.IO.File]::Open('$lockFile','Open','ReadWrite','None'); Start-Sleep -Seconds 25; `$fs.Close()"
    $holder = Start-Process -FilePath (Get-Command powershell.exe).Source -ArgumentList @('-NoProfile','-WindowStyle','Hidden','-Command',$holderCmd) -PassThru
    Start-Sleep -Seconds 3

    $lockers = [RmNative]::GetLockers($lockFile)
    Log "  Lock holder (helper) PID : $($holder.Id)"
    foreach ($l in $lockers) { Log "  Restart Manager reports  : $l" }
    if ($lockers.Count -gt 0 -and $lockers[0].StartsWith('ERROR|')) {
        $results['2 Restart Manager'] = "FAIL ($($lockers[0]))"
    } elseif ($lockers | Where-Object { $_.StartsWith("$($holder.Id)|") }) {
        $results['2 Restart Manager'] = 'PASS'
    } else {
        $results['2 Restart Manager'] = 'FAIL (the locking process was not reported)'
    }
} catch {
    Log "  Exception: $($_.Exception.Message)"
    $results['2 Restart Manager'] = "FAIL (exception: $($_.Exception.Message))"
} finally {
    if ($holder -and -not $holder.HasExited) { Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
    Remove-Item $lockFile -Force -ErrorAction SilentlyContinue
}
Log "  -> $($results['2 Restart Manager'])"

if ($TargetExePath) {
    Log "  Optional real target: $TargetExePath"
    if (Test-Path $TargetExePath) {
        foreach ($l in [RmNative]::GetLockers($TargetExePath)) { Log "    locker: $l" }
    } else { Log "    (path not found)" }
}
Log ""

# ----------------------------------------------------------------------------------------------
# TEST 3 - MiniDumpWriteDump
# ----------------------------------------------------------------------------------------------
Log "=== TEST 3: MiniDumpWriteDump (memory dump) ==="
$dummy = $null
$dumpPath = Join-Path $env:TEMP ("nw-dump-test-{0}.dmp" -f ([guid]::NewGuid().ToString('N')))
try {
    $dummy = Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" -ArgumentList @('/c','ping -n 40 127.0.0.1 > nul') -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 1
    $r = [DumpNative]::Write($dummy.Id, $dumpPath, [DumpNative]::Standard)
    Log "  Standard-flags dump of PID $($dummy.Id): $r"
    if ($r.StartsWith('OK|') -and (Get-Item $dumpPath).Length -gt 0) {
        $results['3 MiniDump (Standard)'] = "PASS ($([math]::Round((Get-Item $dumpPath).Length/1KB)) KB)"
    } else {
        $results['3 MiniDump (Standard)'] = "FAIL ($r)"
    }
    Remove-Item $dumpPath -Force -ErrorAction SilentlyContinue

    $r2 = [DumpNative]::Write($dummy.Id, $dumpPath, [DumpNative]::Full)
    Log "  Full-flags dump     of PID $($dummy.Id): $r2"
    if ($r2.StartsWith('OK|') -and (Get-Item $dumpPath).Length -gt 0) {
        $results['3 MiniDump (Full)'] = "PASS ($([math]::Round((Get-Item $dumpPath).Length/1KB)) KB)"
    } else {
        $results['3 MiniDump (Full)'] = "FAIL ($r2)"
    }
} catch {
    Log "  Exception: $($_.Exception.Message)"
    $results['3 MiniDump'] = "FAIL (exception: $($_.Exception.Message))"
} finally {
    if ($dummy -and -not $dummy.HasExited) { Stop-Process -Id $dummy.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item $dumpPath -Force -ErrorAction SilentlyContinue
}
foreach ($k in @($results.Keys | Where-Object { $_ -like '3 *' })) { Log "  -> $k : $($results[$k])" }
Log ""

# ----------------------------------------------------------------------------------------------
# TEST 4 - dependent services via WMI
# ----------------------------------------------------------------------------------------------
Log "=== TEST 4: dependent services (Win32_DependentService, Role=Antecedent) ==="
try {
    $probe = 'RpcSs'
    $viaWmi = @(Get-CimInstance -Query "ASSOCIATORS OF {Win32_Service.Name='$probe'} WHERE AssocClass=Win32_DependentService Role=Antecedent" -ErrorAction Stop)
    Log "  Services returned by the WMI query for '$probe': $($viaWmi.Count)"
    if ($viaWmi.Count -eq 0) {
        $results['4 Dependent services'] = 'FAIL (query returned nothing)'
    } else {
        $sample = $viaWmi | Select-Object -First 5
        foreach ($s in $sample) { Log "    $($s.Name)  [$($s.State)]  PID $($s.ProcessId)" }
        # Direction check: every returned service must list $probe among the services it DEPENDS ON.
        $firstName = ($viaWmi | Select-Object -First 1).Name
        $requires = @((Get-Service -Name $firstName -ErrorAction Stop).ServicesDependedOn | ForEach-Object { $_.Name })
        Log "    Direction check: '$firstName' depends on: $($requires -join ', ')"
        if ($requires -contains $probe) { $results['4 Dependent services'] = 'PASS (returns services that depend ON the target)' }
        else { $results['4 Dependent services'] = "FAIL (wrong direction: '$firstName' does not depend on '$probe')" }
    }
} catch {
    Log "  Exception: $($_.Exception.Message)"
    $results['4 Dependent services'] = "FAIL (exception: $($_.Exception.Message))"
}
Log "  -> $($results['4 Dependent services'])"
Log ""

# ----------------------------------------------------------------------------------------------
# Summary
# ----------------------------------------------------------------------------------------------
Log "================ SUMMARY ================"
foreach ($k in $results.Keys) { Log ("{0,-24} {1}" -f $k, $results[$k]) }
Log "(Impersonated export and the full capture flow are tested through the dashboard - see the test guide.)"

$reportPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'NativeInterop-TestReport.txt'
$report | Set-Content -Path $reportPath -Encoding UTF8
Write-Host ""
Write-Host "Report saved to: $reportPath" -ForegroundColor Cyan
