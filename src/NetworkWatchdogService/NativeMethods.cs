using System.Diagnostics;

namespace NetworkWatchdogService;

public static class NativeMethods
{
    public static int GetTotalTcpConnectionCount(int targetPid)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"(Get-NetTCPConnection -OwningProcess {targetPid} -ErrorAction SilentlyContinue).Count\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null) return 0;

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (int.TryParse(output, out int count))
            {
                return count;
            }
        }
        catch
        {
            // Return 0 if the subprocess fails to initialize
        }
        
        return 0;
    }
}