using System;
    using System.IO;

    namespace NetworkWatchdogService;

    public static class TelemetryExporter
    {
        private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "export");
        private static readonly string HealthTrendFile = Path.Combine(LogDir, "HealthTrend_v2.csv");
        private static readonly string RestartFile = Path.Combine(LogDir, "RestartEvents_v2.csv");

        static TelemetryExporter()
        {
            if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);

            if (!File.Exists(HealthTrendFile))
                File.WriteAllText(HealthTrendFile, "Timestamp,ServiceName,PID,ActiveConnections\n");

            if (!File.Exists(RestartFile))
                File.WriteAllText(RestartFile, "Timestamp,ServiceName,PID,Action,Reason\n");
        }

        public static void RecordHealthSnapshot(string serviceName, int pid, int connectionCount)
        {
            string record = $"{DateTime.Now:O},{serviceName},{pid},{connectionCount}\n";
            File.AppendAllText(HealthTrendFile, record);
        }

        public static void RecordRestart(string serviceName, int pid, string reason)
        {
            string record = $"{DateTime.Now:O},{serviceName},{pid},RESTART,{reason}\n";
            File.AppendAllText(RestartFile, record);
        }
    }