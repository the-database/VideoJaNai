using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using VideoJaNai.Lang;

namespace VideoJaNai.ViewModels
{
    /// <summary>
    /// The preview window: a player over a --preview-session. Paused, the frame at the slider's
    /// position is shown at full size; playing streams frames at the window's size. Both carry
    /// their source frame, so Source / Upscaled switches instantly.
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
        private int _playFrames;
        private DateTime _playCountStart;

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
            Playing = false;
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
            session.Ended += id => Dispatcher.UIThread.Post(() =>
            {
                if (_session != session || id != _requestId) return;
                if (Playing)
                {
                    Playing = false;
                    StatusText = Resources.PreviewEndText;
                }
                else
                {
                    StatusText = Resources.PreviewPastEndText;
                }
            });
            session.Exited += code => Dispatcher.UIThread.Post(() =>
            {
                if (_session != session) return;   // replaced or closed on purpose
                _session = null;
                IsReady = false;
                Playing = false;
                StatusText = Resources.PreviewStoppedText;
            });
        }

        // a frame arrives on the session's reader thread: decode there, show on the UI thread
        private void OnFrame(PreviewFrame f)
        {
            Bitmap? src = null, output = null;
            try
            {
                if (f.Source.Length > 0)
                {
                    using var ms = new MemoryStream(f.Source);
                    src = new Bitmap(ms);
                }
                using (var ms = new MemoryStream(f.Output))
                {
                    output = new Bitmap(ms);
                }
            }
            catch (Exception)
            {
                src?.Dispose();
                output?.Dispose();
                return;   // a bad frame only costs that frame
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (f.Id != _requestId)
                {
                    // from a request since replaced
                    src?.Dispose();
                    output?.Dispose();
                    return;
                }
                var oldSrc = _sourceImage;
                var oldOut = _outputImage;
                _sourceImage = src ?? oldSrc;
                _outputImage = output;
                this.RaisePropertyChanged(nameof(Image));
                if (!ReferenceEquals(oldSrc, _sourceImage)) oldSrc?.Dispose();
                oldOut?.Dispose();

                _shownPtsMs = f.PtsMs;
                _stepPtsMs = -1;
                SetPositionQuietly(f.PtsMs);
                if (Playing)
                {
                    CountPlayedFrame();
                }
            });
        }

        private void CountPlayedFrame()
        {
            var now = DateTime.UtcNow;
            if (_playFrames == 0)
            {
                _playCountStart = now;
            }
            _playFrames++;
            var secs = (now - _playCountStart).TotalSeconds;
            if (secs >= 1)
            {
                var fps = (_playFrames - 1) / secs;
                var target = 1000.0 / _frameMs;
                StatusText = fps < target * 0.95
                    ? string.Create(Inv, $"{fps:0.0} / {target:0.###} fps - {Resources.PreviewSlowText}")
                    : string.Create(Inv, $"{fps:0.0} / {target:0.###} fps");
                _playFrames = 0;
            }
        }

        private void SeekTo(double ms)
        {
            if (_session == null || !IsReady)
            {
                return;
            }
            Playing = false;
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
                Playing = false;
                // show the frame it stopped on at full size
                _session.RequestFrame(++_requestId, Math.Max(0, _shownPtsMs) / 1000.0, 0);
                return;
            }
            var from = _positionMs >= _durationMs - _frameMs ? 0 : _positionMs;
            Playing = true;
            _playFrames = 0;
            // never bigger than the output, and at most 1920: past that the JPEGs cost more than
            // a frame time on big screens (paused frames are full size anyway)
            _session.Play(++_requestId, from / 1000.0, Math.Clamp(ViewWidth, 64, Math.Min(_outputWidth, 1920)));
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
            Playing = false;
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
            Playing = false;
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
