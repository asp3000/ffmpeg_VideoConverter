// ============================================================================
//  VideoPreviewEngine.cs — shared, continuous video-preview decode engine.
//
//  Replaces the old "spawn one ffmpeg per frame" approach that the editor
//  used (FFmpegHelper.GetFrameAtTimeAsync in a loop). A single long-lived
//  ffmpeg process decodes the video to raw rgb24 at a capped resolution;
//  decoded frames are raised through FrameDecoded on the UI thread so any
//  consumer (editor tabs, player) can blit / post-process them.
//
//  This is the single canonical decode pipeline: VideoEditForm and
//  VideoPlayerForm both build on it, removing the duplicated rawvideo read
//  loop that used to live in each module.
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoConverter
{
    public sealed class VideoPreviewEngine : IDisposable
    {
        public sealed class FrameDecodedEventArgs : EventArgs
        {
            // Caller owns this buffer (a fresh copy); dispose previous frame
            // yourself. Layout is contiguous R,G,B (rgb24), row-major.
            public byte[] Rgb24;
            public int Width;
            public int Height;
            public double Pts;
            public bool IsKey;
        }

        public event EventHandler<FrameDecodedEventArgs> FrameDecoded;
        public event EventHandler PositionChanged;
        public event EventHandler PlaybackEnded;

        private readonly string _filePath;
        private readonly int _maxFrameWidth;
        private readonly SynchronizationContext _sync;

        private CancellationTokenSource _cts;
        private Process _proc;
        private bool _running;     // a decode pipeline is currently alive
        private bool _paused;
        private double _startOffset;
        private DateTime _startWall;
        private bool _disposed;

        public VideoPreviewEngine(string filePath, int maxFrameWidth = 640)
        {
            _filePath = filePath;
            _maxFrameWidth = Math.Max(16, maxFrameWidth);
            // Capture the UI synchronization context so frames are raised on
            // the thread that created the engine (required for WinForms).
            _sync = SynchronizationContext.Current;
        }

        // ---- inputs supplied by the caller after its own probe ----
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public double DurationSec { get; set; }
        public double FrameRate { get; set; }

        // ---- live state ----
        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }
        public double PositionSec { get; private set; }
        public bool IsPlaying => _running && !_paused;

        /// <summary>Derive the decode resolution (capped width, even height).</summary>
        public void ComputeFrameSize()
        {
            int sw = SourceWidth > 0 ? SourceWidth : 1280;
            int sh = SourceHeight > 0 ? SourceHeight : 720;
            int fw = Math.Min(sw, _maxFrameWidth);
            int fh = (int)Math.Round(fw * sh / (double)sw);
            fh = Math.Max(2, fh / 2 * 2);
            FrameWidth = fw;
            FrameHeight = fh;
        }

        public void Play()
        {
            if (_disposed) return;
            if (DurationSec > 0 && PositionSec >= DurationSec - 0.02) PositionSec = 0;
            _paused = false;
            if (!_running) StartPipeline(PositionSec, false);
        }

        public void Pause()
        {
            _paused = true;
            StopPipeline();
        }

        public void TogglePlay()
        {
            if (IsPlaying) Pause();
            else Play();
        }

        public void Seek(double seconds)
        {
            if (_disposed) return;
            if (seconds < 0) seconds = 0;
            if (DurationSec > 0) seconds = Math.Min(DurationSec, seconds);
            PositionSec = seconds;
            StopPipeline();
            if (_paused) RefreshCurrentFrame();
            else StartPipeline(seconds, false);
        }

        /// <summary>
        /// Decode and emit exactly one frame at the current position. Used for
        /// paused previews and when a slider changes while paused. No-op while
        /// playing (the live loop already refreshes every frame).
        /// </summary>
        public void RefreshCurrentFrame()
        {
            if (_disposed || IsPlaying) return;
            StartPipeline(PositionSec, true);
        }

        public void Stop()
        {
            _paused = true;
            StopPipeline();
            PositionSec = 0;
            RaisePositionChanged();
        }

        // ------------------------------------------------------------------
        // Pipeline
        // ------------------------------------------------------------------

        private void StartPipeline(double offset, bool oneShot)
        {
            StopPipeline();
            ComputeFrameSize();
            if (FrameWidth <= 0 || FrameHeight <= 0) return;
            if (!File.Exists(FFmpegHelper.FFmpegPath)) return;

            var ct = (_cts = new CancellationTokenSource()).Token;
            _running = true;
            _startOffset = offset;
            _startWall = DateTime.Now;

            string ss = offset.ToString("0.###", CultureInfo.InvariantCulture);
            string args = string.Format(CultureInfo.InvariantCulture,
                "-nostdin -ss {0} -i \"{1}\" -an -f rawvideo -pix_fmt rgb24 -vf scale={2}:{3} -",
                ss, _filePath, FrameWidth, FrameHeight);

            var psi = new ProcessStartInfo
            {
                FileName = FFmpegHelper.FFmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            try { proc.Start(); }
            catch { _running = false; return; }
            ProcessGuard.Register(proc);
            _proc = proc;

            // Drain stderr (event-driven) so the pipe cannot deadlock and we
            // would notice fatal errors; we simply abandon the process.
            proc.ErrorDataReceived += (s, e) => { /* intentionally ignored for preview */ };
            try { proc.BeginErrorReadLine(); } catch { }

            _ = Task.Run(() => ReadLoop(proc, ct, oneShot));
        }

        private async Task ReadLoop(Process proc, CancellationToken ct, bool oneShot)
        {
            int frameBytes = FrameWidth * FrameHeight * 3;
            var buf = new byte[frameBytes];
            double frameDur = FrameRate > 0 ? 1.0 / FrameRate : 1.0 / 25.0;
            double pts = _startOffset;
            bool ended = false;
            try
            {
                using (var stream = proc.StandardOutput.BaseStream)
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var frameStart = DateTime.Now;
                        int total = 0;
                        while (total < frameBytes)
                        {
                            int r = await stream.ReadAsync(buf, total, frameBytes - total, ct).ConfigureAwait(false);
                            if (r == 0) { total = -1; break; } // EOF
                            total += r;
                        }
                        if (total < 0) { ended = true; break; }

                        var copy = new byte[frameBytes];
                        Buffer.BlockCopy(buf, 0, copy, 0, frameBytes);

                        double wallPts = _startOffset + (DateTime.Now - _startWall).TotalSeconds;
                        PositionSec = wallPts;

                        RaiseFrame(new FrameDecodedEventArgs
                        {
                            Rgb24 = copy,
                            Width = FrameWidth,
                            Height = FrameHeight,
                            Pts = pts
                        });
                        pts += frameDur;

                        if (oneShot) break;

                        if (DurationSec > 0 && wallPts >= DurationSec - 0.02)
                        {
                            ended = true;
                            break;
                        }

                        int elapsedMs = (int)(DateTime.Now - frameStart).TotalMilliseconds;
                        int delay = (int)(frameDur * 1000) - elapsedMs;
                        if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                _running = false;
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                try { proc.Dispose(); } catch { }
                if (ReferenceEquals(_proc, proc)) _proc = null;
            }

            if (ended) RaisePlaybackEnded();
        }

        private void StopPipeline()
        {
            try { _cts?.Cancel(); } catch { }
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
            _running = false;
            _cts = null;
        }

        // ------------------------------------------------------------------
        // Event dispatch (always on the captured UI context)
        // ------------------------------------------------------------------

        private void RaiseFrame(FrameDecodedEventArgs e)
        {
            if (_sync != null)
                _sync.Post(_ => { FrameDecoded?.Invoke(this, e); PositionChanged?.Invoke(this, EventArgs.Empty); }, null);
            else
            {
                FrameDecoded?.Invoke(this, e);
                PositionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void RaisePositionChanged()
        {
            if (_sync != null) _sync.Post(_ => PositionChanged?.Invoke(this, EventArgs.Empty), null);
            else PositionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RaisePlaybackEnded()
        {
            if (_sync != null) _sync.Post(_ => PlaybackEnded?.Invoke(this, EventArgs.Empty), null);
            else PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopPipeline();
            FrameDecoded = null;
            PositionChanged = null;
            PlaybackEnded = null;
        }
    }

    // Static helpers shared by preview consumers (rgb24 <-> Bitmap).
    public static class Rgb24Convert
    {
        public static Bitmap ToBitmap(byte[] rgb24, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                for (int y = 0; y < h; y++)
                {
                    int src = y * w * 3;
                    int dst = y * stride;
                    System.Runtime.InteropServices.Marshal.Copy(rgb24, src, IntPtr.Add(data.Scan0, dst), w * 3);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }
    }
}
