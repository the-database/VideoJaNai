using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VideoJaNai.Lang;

namespace VideoJaNai.ViewModels
{
    /// <summary>
    /// The preview window: a player over a --preview-session. Paused, the frame at the slider's
    /// position is shown at full size; playing streams frames at the window's size. Both carry
    /// their source frame, so Source / Upscaled switches instantly.
    ///
    /// Playing goes through a buffer: frames are shown at their own timestamps, not as they
    /// arrive. A GPU that keeps up delivers them in real time (the session paces itself), so the
    /// buffer stays small; a slower one would otherwise play in slow motion, so playback waits
    /// for the buffer to fill and pauses to refill it, like a video site.
    /// </summary>
    public class PreviewWindowViewModel : ViewModelBase
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // starts a session with the current settings (and names the video), or says why it can't
        private readonly Func<(PreviewSession? session, string? error, string? title)> _startSession;
        private readonly Action<string> _log;
        private PreviewSession? _session;
        private long _requestId;
        private long _shownPtsMs = -1;
        private double _stepPtsMs = -1;   // where frame steps have got to, ahead of the frames
        private double _frameMs = 1000.0 / 24;
        private int _outputWidth = 1920;

        // playing: frames of the play request _playId, in arrival (= display) order
        private const double StartBufferMs = 300;    // before the first frame is shown
        private const double MaxBufferMs = 16000;    // the refill target stops growing here
        private const double NearRealTime = 0.9;     // making frames at least this fast: no stalls
        private readonly object _bufferLock = new();
        private readonly List<PreviewFrame> _buffer = [];
        private readonly SemaphoreSlim _arrived = new(0);
        private long _playId = -1;
        private bool _playEnded;
        private CancellationTokenSource? _playCts;
        private int _arrivals;
        private long _arrivalCountStart;
        private double _generateFps;

        public event Action? CloseRequested;

        public PreviewWindowViewModel(Func<(PreviewSession? session, string? error, string? title)> startSession, Action<string> log)
        {
            _startSession = startSession;
            _log = log;
        }

        private string _title = Resources.PreviewWindowTitle;
        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        /// <summary>Width the images are wanted at while playing (the picture's size on screen).</summary>
        public int ViewWidth { get; set; } = 1280;

        private bool _isReady;
        public bool IsReady
        {
            get => _isReady;
            set => this.RaiseAndSetIfChanged(ref _isReady, value);
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        private double _durationMs = 1;
        public double DurationMs
        {
            get => _durationMs;
            set => this.RaiseAndSetIfChanged(ref _durationMs, value);
        }

        private double _positionMs;
        /// <summary>The slider. Set by the person dragging it: shows the frame there.</summary>
        public double PositionMs
        {
            get => _positionMs;
            set
            {
                if (Math.Abs(value - _positionMs) < 0.5)
                {
                    return;
                }
                this.RaiseAndSetIfChanged(ref _positionMs, value);
                this.RaisePropertyChanged(nameof(TimeText));
                SeekTo(value);
            }
        }

        // moved by playback, not by the person: no request
        private void SetPositionQuietly(double ms)
        {
            _positionMs = ms;
            this.RaisePropertyChanged(nameof(PositionMs));
            this.RaisePropertyChanged(nameof(TimeText));
        }

        public string TimeText => $"{FormatTime(_positionMs)} / {FormatTime(_durationMs)}";

        private static string FormatTime(double ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.fff", Inv) : t.ToString(@"m\:ss\.fff", Inv);
        }

        private bool _playing;
        public bool Playing
        {
            get => _playing;
            set => this.RaiseAndSetIfChanged(ref _playing, value);
        }

        private double _bufferedMs;
        /// <summary>How far the play buffer reaches (shown under the slider while playing).</summary>
        public double BufferedMs
        {
            get => _bufferedMs;
            set => this.RaiseAndSetIfChanged(ref _bufferedMs, value);
        }

        private bool _showSource;
        public bool ShowSource
        {
            get => _showSource;
            set
            {
                this.RaiseAndSetIfChanged(ref _showSource, value);
                this.RaisePropertyChanged(nameof(ShowUpscaled));
                this.RaisePropertyChanged(nameof(Image));
            }
        }

        public bool ShowUpscaled
        {
            get => !_showSource;
            set => ShowSource = !value;
        }

        private Bitmap? _sourceImage, _outputImage;
        public Bitmap? Image => _showSource ? _sourceImage : _outputImage;

        public void Start()
        {
            IsReady = false;
            StopPlayback();
            StatusText = Resources.PreviewStartingText;
            var (session, error, title) = _startSession();
            if (session == null)
            {
                StatusText = error ?? Resources.PreviewStoppedText;
                return;
            }
            _session = session;
            Title = $"{Resources.PreviewWindowTitle} - {title}";
            session.Ready += (duration, w, h, fps) => Dispatcher.UIThread.Post(() =>
            {
                if (_session != session) return;
                DurationMs = Math.Max(1, duration);
                _frameMs = fps > 0 ? 1000.0 / fps : _frameMs;
                _outputWidth = Math.Max(64, w);
                IsReady = true;
                StatusText = string.Create(Inv, $"{w}x{h}, {fps:0.###} fps");
                SeekTo(Math.Min(_positionMs, DurationMs));
            });
            session.ProgressLine += line => Dispatcher.UIThread.Post(() =>
            {
                if (_session != session) return;
                if (line.Contains("phase=build_engine", StringComparison.Ordinal) &&
                    !line.Contains("pct=100", StringComparison.Ordinal))
                {
                    StatusText = Resources.PreviewBuildingEngineText;
                }
            });
            session.LogLine += _log;
            session.FrameReceived += OnFrame;
            session.Ended += id =>
            {
                if (id == Interlocked.Read(ref _playId))
                {
                    // the play loop shows what is buffered, then stops
                    lock (_bufferLock) _playEnded = true;
                    _arrived.Release();
                    return;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    if (_session != session || id != _requestId) return;
                    StatusText = Resources.PreviewPastEndText;
                });
            };
            session.Exited += code => Dispatcher.UIThread.Post(() =>
            {
                if (_session != session) return;   // replaced or closed on purpose
                _session = null;
                IsReady = false;
                StopPlayback();
                StatusText = Resources.PreviewStoppedText;
            });
        }

        // a frame arrives on the session's reader thread: a played one goes into the buffer;
        // any other is decoded there and shown on the UI thread
        private void OnFrame(PreviewFrame f)
        {
            if (f.Id == Interlocked.Read(ref _playId))
            {
                lock (_bufferLock)
                {
                    _buffer.Add(f);
                    CountArrival();
                }
                _arrived.Release();
                return;
            }

            var (src, output) = Decode(f);
            if (output == null)
            {
                src?.Dispose();
                return;   // a bad frame only costs that frame
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (f.Id != _requestId)
                {
                    // from a request since replaced
                    src?.Dispose();
                    output.Dispose();
                    return;
                }
                SetImages(src, output, f.PtsMs);
                _stepPtsMs = -1;
            });
        }

        private static (Bitmap? src, Bitmap? output) Decode(PreviewFrame f)
        {
            Bitmap? src = null;
            try
            {
                if (f.Source.Length > 0)
                {
                    using var ms = new MemoryStream(f.Source);
                    src = new Bitmap(ms);
                }
                using var outMs = new MemoryStream(f.Output);
                return (src, new Bitmap(outMs));
            }
            catch (Exception)
            {
                return (src, null);
            }
        }

        // on the UI thread: shows a frame (the source frame stays if this one came without one)
        private void SetImages(Bitmap? src, Bitmap output, long ptsMs)
        {
            var oldSrc = _sourceImage;
            var oldOut = _outputImage;
            _sourceImage = src ?? oldSrc;
            _outputImage = output;
            this.RaisePropertyChanged(nameof(Image));
            if (!ReferenceEquals(oldSrc, _sourceImage)) oldSrc?.Dispose();
            oldOut?.Dispose();
            _shownPtsMs = ptsMs;
            SetPositionQuietly(ptsMs);
        }

        // how fast frames are being made (under _bufferLock)
        private void CountArrival()
        {
            var now = Stopwatch.GetTimestamp();
            if (_arrivals == 0)
            {
                _arrivalCountStart = now;
            }
            _arrivals++;
            var secs = Stopwatch.GetElapsedTime(_arrivalCountStart, now).TotalSeconds;
            if (secs >= 1)
            {
                _generateFps = (_arrivals - 1) / secs;
                _arrivals = 0;
            }
        }

        // Plays request id from the buffer: each frame at its timestamp, on a clock that only runs
        // while there is something to show. Buffering waits for `target` ms of frames ahead of the
        // last one shown; running dry raises the target (2 s, then doubling up to MaxBufferMs).
        private async Task PlayLoop(long id, double fromMs, CancellationToken ct)
        {
            var target = StartBufferMs;
            var buffering = true;
            var lastShownMs = fromMs;
            double basePtsMs = 0;
            long baseTicks = 0;
            long lastStatus = 0;
            PreviewFrame? next = null;
            Bitmap? nextSrc = null, nextOut = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    PreviewFrame? head;
                    double bufferedEndMs;
                    bool ended;
                    lock (_bufferLock)
                    {
                        head = _buffer.Count > 0 ? _buffer[0] : null;
                        bufferedEndMs = _buffer.Count > 0 ? _buffer[^1].PtsMs : lastShownMs;
                        ended = _playEnded;
                    }

                    if (Stopwatch.GetElapsedTime(lastStatus).TotalMilliseconds >= 250)
                    {
                        lastStatus = Stopwatch.GetTimestamp();
                        PostPlayStatus(id, buffering, bufferedEndMs - lastShownMs, target, bufferedEndMs);
                    }

                    if (head == null && ended)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (Interlocked.Read(ref _playId) != id) return;
                            StopPlayback();
                            StatusText = Resources.PreviewEndText;
                        });
                        return;
                    }

                    if (buffering)
                    {
                        if (head != null && (bufferedEndMs - lastShownMs >= target || ended))
                        {
                            buffering = false;
                            basePtsMs = head.PtsMs;
                            baseTicks = Stopwatch.GetTimestamp();
                            lastStatus = 0;
                        }
                        else
                        {
                            await _arrived.WaitAsync(100, ct);
                        }
                        continue;
                    }

                    var clockMs = basePtsMs + Stopwatch.GetElapsedTime(baseTicks).TotalMilliseconds;
                    if (head == null)
                    {
                        if (clockMs > lastShownMs + (2 * _frameMs))
                        {
                            buffering = true;
                            double generate;
                            lock (_bufferLock) generate = _generateFps;
                            if (generate >= NearRealTime * (1000.0 / _frameMs))
                            {
                                // only just behind: carry on from the next frame (a few percent
                                // slow) rather than stall for seconds to buffer
                                target = 0;
                            }
                            else
                            {
                                // well behind: wait for more than last time
                                target = Math.Min(Math.Max(target * 2, 2000), MaxBufferMs);
                                lastStatus = 0;
                            }
                        }
                        else
                        {
                            await _arrived.WaitAsync(10, ct);
                        }
                        continue;
                    }

                    // decode the next frame ahead of its time
                    if (!ReferenceEquals(next, head))
                    {
                        nextSrc?.Dispose();
                        nextOut?.Dispose();
                        (nextSrc, nextOut) = Decode(head);
                        next = head;
                    }

                    var dueInMs = head.PtsMs - clockMs;
                    if (dueInMs > 1)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(dueInMs, 20)), ct);
                        continue;
                    }

                    lock (_bufferLock)
                    {
                        if (_buffer.Count > 0 && ReferenceEquals(_buffer[0], head))
                        {
                            _buffer.RemoveAt(0);
                        }
                    }
                    lastShownMs = head.PtsMs;
                    var (src, output) = (nextSrc, nextOut);
                    next = null;
                    nextSrc = nextOut = null;
                    if (output == null)
                    {
                        src?.Dispose();   // a bad frame only costs that frame
                        continue;
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (Interlocked.Read(ref _playId) != id)
                        {
                            src?.Dispose();
                            output.Dispose();
                            return;
                        }
                        SetImages(src, output, head.PtsMs);
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                nextSrc?.Dispose();
                nextOut?.Dispose();
            }
        }

        private void PostPlayStatus(long id, bool buffering, double aheadMs, double targetMs, double bufferedEndMs)
        {
            double generate;
            lock (_bufferLock)
            {
                generate = _generateFps;
            }
            var fps = 1000.0 / _frameMs;
            var speed = generate > 0 ? string.Create(Inv, $"{generate:0.0} / {fps:0.###} fps") : string.Empty;
            var ahead = Math.Max(0, aheadMs) / 1000;
            var text = buffering && targetMs > 0
                ? string.Create(Inv, $"{Resources.PreviewBufferingText} {ahead:0.0} / {targetMs / 1000:0.0} s") +
                  (speed.Length > 0 ? " - " + speed : string.Empty)
                : generate > 0 && generate < fps * 0.95
                    ? $"{speed} - {Resources.PreviewSlowText} - {string.Format(Inv, Resources.PreviewBufferedText, ahead)}"
                    : speed;
            Dispatcher.UIThread.Post(() =>
            {
                if (Interlocked.Read(ref _playId) != id) return;
                StatusText = text;
                BufferedMs = bufferedEndMs;
            });
        }

        // ends playing (if it was): the loop stops and the buffer is dropped
        private void StopPlayback()
        {
            Interlocked.Exchange(ref _playId, -1);
            _playCts?.Cancel();
            _playCts = null;
            lock (_bufferLock)
            {
                _buffer.Clear();
                _playEnded = false;
                _arrivals = 0;
                _generateFps = 0;
            }
            Playing = false;
            BufferedMs = 0;
        }

        private void SeekTo(double ms)
        {
            if (_session == null || !IsReady)
            {
                return;
            }
            StopPlayback();
            _session.RequestFrame(++_requestId, ms / 1000.0, 0);
        }

        public void TogglePlay()
        {
            if (_session == null || !IsReady)
            {
                return;
            }
            if (Playing)
            {
                StopPlayback();
                // show the frame it stopped on at full size
                _session.RequestFrame(++_requestId, Math.Max(0, _shownPtsMs) / 1000.0, 0);
                return;
            }
            var from = _positionMs >= _durationMs - _frameMs ? 0 : _positionMs;
            StopPlayback();
            var id = ++_requestId;
            Interlocked.Exchange(ref _playId, id);
            Playing = true;
            StatusText = Resources.PreviewBufferingText;
            _playCts = new CancellationTokenSource();
            var ct = _playCts.Token;
            Task.Run(() => PlayLoop(id, from, ct));
            // never bigger than the output, and at most 1920: past that the JPEGs cost more than
            // a frame time on big screens (paused frames are full size anyway)
            _session.Play(id, from / 1000.0, Math.Clamp(ViewWidth, 64, Math.Min(_outputWidth, 1920)));
        }

        /// <summary>One frame back or forward (playing stops).</summary>
        public void Step(int direction)
        {
            if (_session == null || !IsReady)
            {
                return;
            }
            // from the frame last asked for, so quick presses each move a frame even before
            // the frames arrive
            var at = _stepPtsMs >= 0 ? _stepPtsMs : _shownPtsMs >= 0 ? _shownPtsMs : _positionMs;
            _stepPtsMs = Math.Clamp(at + (direction * _frameMs), 0, _durationMs);
            StopPlayback();
            SetPositionQuietly(_stepPtsMs);
            // the first frame at or after the position is shown: aim half a frame early
            _session.RequestFrame(++_requestId, Math.Max(0, _stepPtsMs - (0.5 * _frameMs)) / 1000.0, 0);
        }

        public void ToggleSource() => ShowSource = !ShowSource;

        /// <summary>Restarts the session with the settings as they are now, at the same position.</summary>
        public void Reload()
        {
            StopSession();
            Start();
        }

        public void RequestClose() => CloseRequested?.Invoke();

        /// <summary>The window closed (or an upscale is starting): end the session.</summary>
        public void StopSession()
        {
            var s = _session;
            _session = null;
            IsReady = false;
            StopPlayback();
            if (s != null)
            {
                Task.Run(s.Dispose);
            }
        }

        public void ReleaseImages()
        {
            _sourceImage?.Dispose();
            _outputImage?.Dispose();
            _sourceImage = _outputImage = null;
            this.RaisePropertyChanged(nameof(Image));
        }
    }
}
