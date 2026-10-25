using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace VideoJaNai.ViewModels
{
    public sealed record PreviewFrame(long Id, long PtsMs, byte[] Source, byte[] Output);

    /// <summary>
    /// One aji_encode --preview-session: libaji stays configured (engines loaded) while the
    /// preview window asks for frames. Commands are lines on its stdin ("frame id sec width",
    /// "play id sec width", "stop", "quit"); it answers on stdout with text lines, each FRAME
    /// line followed by the source and output JPEGs.
    /// </summary>
    public sealed class PreviewSession : IDisposable
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly Process _process;
        private readonly object _stdinLock = new();
        private bool _disposed;

        /// <summary>READY: duration, output size and frame rate.</summary>
        public event Action<long, int, int, double>? Ready;
        public event Action<PreviewFrame>? FrameReceived;
        /// <summary>END: the video ended before the position, or playing reached the end.</summary>
        public event Action<long>? Ended;
        /// <summary>PROGRESS lines (engine builds, before READY).</summary>
        public event Action<string>? ProgressLine;
        public event Action<string>? LogLine;
        /// <summary>The process exited (crashed, or closed by Dispose).</summary>
        public event Action<int>? Exited;

        private PreviewSession(Process process)
        {
            _process = process;
        }

        public static PreviewSession Start(string exe, string arguments, string workingDirectory,
                                           IDictionary<string, string>? environment)
        {
            var process = new Process();
            process.StartInfo.FileName = exe;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            if (environment != null)
            {
                foreach (var (k, v) in environment)
                {
                    process.StartInfo.Environment[k] = v;
                }
            }

            var session = new PreviewSession(process);
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    session.LogLine?.Invoke(e.Data);
                }
            };
            process.Start();
            ChildProcessTracker.AddProcess(process);
            process.StandardInput.NewLine = "\n";
            process.BeginErrorReadLine();
            new Thread(session.ReadLoop) { IsBackground = true, Name = "preview session" }.Start();
            return session;
        }

        /// <summary>The frame at a position (width 0 = full size).</summary>
        public void RequestFrame(long id, double seconds, int width) =>
            Send(string.Create(Inv, $"frame {id} {seconds:0.000} {width}"));

        public void Play(long id, double seconds, int width) =>
            Send(string.Create(Inv, $"play {id} {seconds:0.000} {width}"));

        public void Stop() => Send("stop");

        private void Send(string line)
        {
            lock (_stdinLock)
            {
                if (_disposed)
                {
                    return;
                }
                try
                {
                    _process.StandardInput.WriteLine(line);
                    _process.StandardInput.Flush();
                }
                catch (Exception e) when (e is IOException or InvalidOperationException)
                {
                    // exited; Exited reports it
                }
            }
        }

        private void ReadLoop()
        {
            var stdout = new BufferedStream(_process.StandardOutput.BaseStream, 1 << 16);
            try
            {
                while (ReadLine(stdout) is string line)
                {
                    var kind = line.Split(' ', 2)[0];
                    var kv = ParseFields(line);
                    switch (kind)
                    {
                        case "FRAME":
                            var src = ReadExactly(stdout, (int)Field(kv, "src"));
                            var output = ReadExactly(stdout, (int)Field(kv, "out"));
                            FrameReceived?.Invoke(new PreviewFrame(Field(kv, "id"), Field(kv, "pts"), src, output));
                            break;
                        case "END":
                            Ended?.Invoke(Field(kv, "id"));
                            break;
                        case "READY":
                            var fps = kv.TryGetValue("fps", out var f) ? ParseRate(f) : 0;
                            Ready?.Invoke(Field(kv, "duration"), (int)Field(kv, "width"), (int)Field(kv, "height"), fps);
                            break;
                        case "PROGRESS":
                            ProgressLine?.Invoke(line);
                            break;
                        default:
                            LogLine?.Invoke(line);
                            break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException)
            {
                // the process went away mid-frame
            }

            try
            {
                _process.WaitForExit();
                Exited?.Invoke(_process.ExitCode);
            }
            catch (InvalidOperationException)
            {
                Exited?.Invoke(-1);
            }
        }

        private static string? ReadLine(Stream s)
        {
            var bytes = new List<byte>(128);
            while (true)
            {
                var b = s.ReadByte();
                if (b < 0)
                {
                    return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : null;
                }
                if (b == '\n')
                {
                    return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                }
                bytes.Add((byte)b);
            }
        }

        private static byte[] ReadExactly(Stream s, int n)
        {
            var buf = new byte[n];
            s.ReadExactly(buf, 0, n);
            return buf;
        }

        private static Dictionary<string, string> ParseFields(string line)
        {
            var d = new Dictionary<string, string>();
            foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0)
                {
                    d[part[..eq]] = part[(eq + 1)..];
                }
            }
            return d;
        }

        private static long Field(Dictionary<string, string> kv, string key) =>
            kv.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, Inv, out var n) ? n : 0;

        private static double ParseRate(string s)
        {
            var parts = s.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, Inv, out var num) &&
                double.TryParse(parts[1], NumberStyles.Float, Inv, out var den) && den > 0)
            {
                return num / den;
            }
            return 0;
        }

        /// <summary>Asks the session to quit, and kills it if it doesn't promptly.</summary>
        public void Dispose()
        {
            Send("quit");
            lock (_stdinLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
            }
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(3000))
                {
                    _process.Kill(true);
                }
            }
            catch (Exception)
            {
                // already gone
            }
        }
    }
}
