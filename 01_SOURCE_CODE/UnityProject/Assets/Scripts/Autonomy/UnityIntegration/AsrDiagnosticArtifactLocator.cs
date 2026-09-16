using System;
using System.IO;

namespace Autonomy.UnityIntegration
{
    public static class AsrDiagnosticArtifactLocator
    {
        public static bool TryFindDiagnosticsCsv(string folder, out string csvPath)
        {
            csvPath = string.Empty;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return false;
            }

            string[] traced = Directory.GetFiles(folder, "asr_diagnostics_*_*.csv", SearchOption.TopDirectoryOnly);
            if (traced.Length > 0)
            {
                Array.Sort(traced, StringComparer.OrdinalIgnoreCase);
                csvPath = traced[0];
                return true;
            }

            string legacy = Path.Combine(folder, "asr_diagnostics.csv");
            if (File.Exists(legacy))
            {
                csvPath = legacy;
                return true;
            }

            return false;
        }

        public static bool IsCanonicalDiagnosticsFileName(string fileName)
        {
            return string.Equals(fileName, "asr_diagnostics.csv", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "asr_diagnostics.jsonl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "asr_summary.md", StringComparison.OrdinalIgnoreCase);
        }
    }
}
