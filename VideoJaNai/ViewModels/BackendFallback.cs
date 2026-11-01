using System;

namespace VideoJaNai.ViewModels
{
    // libaji's TensorRT-RTX -> TensorRT fallback, as aji_encode logs it on stderr:
    // "[aji:1] TensorRT-RTX failed (<step>: <why>); using TensorRT instead", or "TensorRT-RTX failed
    // to start; using TensorRT instead" (the reason is on the line before). The main window and the
    // preview show it as a warning, so a fallback isn't only a console line.
    public static class BackendFallback
    {
        // The message, without aji_encode's "[aji:N] " prefix, when the line reports a fallback;
        // else null. ("... and TensorRT is unavailable; continuing on TensorRT-RTX" is not one.)
        public static string? FromAjiLog(string line)
        {
            if (!line.Contains("TensorRT-RTX failed", StringComparison.Ordinal) ||
                !line.Contains("using TensorRT instead", StringComparison.Ordinal))
            {
                return null;
            }
            var end = line.StartsWith("[aji:", StringComparison.Ordinal) ? line.IndexOf("] ", StringComparison.Ordinal) : -1;
            return end >= 0 ? line[(end + 2)..] : line;
        }
    }
}
