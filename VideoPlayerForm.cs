// ============================================================================
//  VideoPlayerForm.cs — OpenGL preview window.
//
//  Uses OpenTK GLControl to render video frames (or an image) onto a
//  hardware-accelerated texture, with NAudio providing audio output for
//  video files. ffmpeg is used to pipe raw RGB video frames and s16le PCM
//  audio, which keeps the converter's dependency set unchanged.
//
//  Design summary:
//    * Two ffmpeg child processes: one decodes video to rawvideo (rgb24),
//      the other decodes audio to s16le PCM fed into a NAudio
//      BufferedWaveProvider -> WaveOutEvent.
//    * Audio clock is the master when present; otherwise a wall clock is
//      used. Playback is paced by presenting the newest queued frame whose
//      PTS is <= the clock.
//    * For images the control bar is hidden and the picture is shown as a
//      centered, aspect-correct texture.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;

namespace VideoConverter
{
    public partial class VideoPlayerForm : Form
    {
        // ---- UI ----
        private GLControl _glc;
        private System.Windows.Forms.Timer _renderTimer;
        private Panel _controlBar;
        private Button _btnPlay;
        private TrackBar _seekBar;
        private Label _lblTime;

        // ---- GL / frame state ----
        private int _tex;
        private bool _texDirty;
        private bool _hasFrame;
        private int _frameW, _frameH;
        private byte[] _frameData;

        // ---- playback state ----
        private readonly string _path;
        private readonly bool _isImage;
        private ConcurrentQueue<VideoFrame> _queue = new ConcurrentQueue<VideoFrame>();
        private VideoFrame _displayFrame;
        private CancellationTokenSource _cts;
        private Process _videoProc, _audioProc;
        private Stream _videoStream, _audioStream;
        private WaveOutEvent _waveOut;
        private BufferedWaveProvider _waveProvider;
        private bool _hasAudio;
        private double _duration;
        private double _fps;
        private double _startOffset;
        private DateTime _startWall;
        private bool _paused;
        private double _pauseClock;
        private bool _started;
        private bool _ended;
        private bool _seeking;

        private class VideoFrame
        {
            public double Pts;
            public byte[] Data;
        }

        public VideoPlayerForm(string path, bool isImage)
        {
            _path = path;
            _isImage = isImage;
            BuildUI();
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            this.Text = isImage ? "图片预览" : "视频播放";
        }

        private void BuildUI()
        {
            this.ClientSize = new Size(820, 520);
            this.MinimumSize = new Size(480, 320);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.Black;

            _glc = new GLControl(new GraphicsMode(32, 24, 0, 0))
            {
                Dock = DockStyle.Fill
            };
            _glc.Load += (s, e) => InitGL();
            _glc.Resize += (s, e) =>
            {
                if (!_glc.IsDisposed)
                {
                    _glc.MakeCurrent();
                    GL.Viewport(0, 0, _glc.Width, _glc.Height);
                }
            };
            _glc.Paint += (s, e) => Render();
            this.Controls.Add(_glc);

            if (!_isImage)
            {
                _controlBar = new Panel
                {
                    Dock = DockStyle.Bottom,
                    Height = 50,
                    BackColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle
                };

                _btnPlay = new Button
                {
                    Dock = DockStyle.Left,
                    Width = 78,
                    FlatStyle = FlatStyle.Flat,
                    Text = "▶ 播放",
                    Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                    BackColor = Color.FromArgb(124, 77, 255),
                    ForeColor = Color.White
                };
                _btnPlay.FlatAppearance.BorderSize = 0;
                _btnPlay.Click += BtnPlay_Click;

                _lblTime = new Label
                {
                    Dock = DockStyle.Right,
                    Width = 150,
                    Text = "00:00:00 / 00:00:00",
                    Font = new Font("Microsoft YaHei UI", 9F),
                    ForeColor = Color.FromArgb(90, 90, 90),
                    TextAlign = ContentAlignment.MiddleCenter
                };

                _seekBar = new TrackBar
                {
                    Dock = DockStyle.Fill,
                    Minimum = 0,
                    Maximum = 1000,
                    TickStyle = TickStyle.None
                };
                _seekBar.MouseDown += (s, e) => { _seeking = true; };
                _seekBar.MouseUp += (s, e) =>
                {
                    _seeking = false;
                    Seek(_seekBar.Value / 1000.0 * _duration);
                };

                _controlBar.Controls.Add(_lblTime);
                _controlBar.Controls.Add(_seekBar);
                _controlBar.Controls.Add(_btnPlay);
                this.Controls.Add(_controlBar);
            }

            _renderTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _renderTimer.Tick += (s, e) => RenderFrame();
        }

        private void InitGL()
        {
            if (_glc.IsDisposed || _tex != 0) return;
            _glc.MakeCurrent();
            GL.ClearColor(0f, 0f, 0f, 1f);
            _tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _tex);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.Viewport(0, 0, _glc.Width, _glc.Height);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _glc.MakeCurrent();
            if (_tex == 0) InitGL();
            if (_isImage)
                LoadImage();
            else
                StartPlayback();
            _renderTimer.Start();
        }

        // ====================================================================
        //  Playback control
        // ====================================================================

        private void StartPlayback()
        {
            if (_started) return;
            _started = true;
            _cts = new CancellationTokenSource();

            // Probe on a background thread (avoids blocking the UI thread and a
            // potential SynchronizationContext deadlock), then finish setup on
            // the UI thread where the audio device / controls live.
            _ = Task.Run(async () =>
            {
                MediaInfo info = null;
                try { info = await FFmpegHelper.ProbeDetailedAsync(_path); }
                catch { /* fall back to defaults */ }
                if (IsDisposed || !IsHandleCreated) return;
                try { this.Invoke((Action)(() => SetupPlayback(info))); }
                catch (ObjectDisposedException) { /* form closed */ }
            });
        }

        private void SetupPlayback(MediaInfo info)
        {
            if (IsDisposed) return;

            int srcW = info?.Width ?? 0;
            int srcH = info?.Height ?? 0;
            _duration = info?.DurationSeconds ?? 0;
            _fps = info?.FrameRate ?? 0;
            if (_fps <= 0) _fps = 25;

            int tw = srcW > 0 ? Math.Min(srcW, 720) : 720;
            int th = srcH > 0 ? (int)Math.Round((double)tw * srcH / srcW / 2) * 2 : 404;
            if (th <= 0) th = 404;
            _frameW = tw;
            _frameH = th;

            _hasAudio = (info?.AudioTracks?.Count ?? 0) > 0;
            if (_hasAudio)
            {
                _waveProvider = new BufferedWaveProvider(new WaveFormat(44100, 16, 2));
                _waveOut = new WaveOutEvent();
                _waveOut.Init(_waveProvider);
            }

            _startOffset = 0;
            _startWall = DateTime.Now;
            _paused = false;
            _ended = false;

            StartPipelines(0);
            _ = Task.Run(() => ReadVideo(_cts.Token));
            if (_hasAudio) _ = Task.Run(() => ReadAudio(_cts.Token));
            if (_hasAudio) _waveOut.Play();

            UpdatePlayButton();
        }

        private void StartPipelines(double offset)
        {
            string ss = offset.ToString("0.###", CultureInfo.InvariantCulture);
            string vArgs = string.Format(
                "-ss {0} -i \"{1}\" -an -f rawvideo -pix_fmt rgb24 -vf scale={2}:{3} -",
                ss, _path, _frameW, _frameH);
            _videoProc = StartFfmpeg(vArgs, out _videoStream);

            if (_hasAudio)
            {
                string aArgs = string.Format(
                    "-ss {0} -i \"{1}\" -vn -f s16le -ar 44100 -ac 2 -",
                    ss, _path);
                _audioProc = StartFfmpeg(aArgs, out _audioStream);
            }
        }

        private static Process StartFfmpeg(string args, out Stream stdout)
        {
            var psi = new ProcessStartInfo(FFmpegHelper.FFmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = Process.Start(psi);
            stdout = p.StandardOutput.BaseStream;
            return p;
        }

        private async Task ReadVideo(CancellationToken ct)
        {
            int frameBytes = _frameW * _frameH * 3;
            var buf = new byte[frameBytes];
            double pts = _startOffset;
            double frameDur = 1.0 / _fps;
            try
            {
                using (var stream = _videoProc.StandardOutput.BaseStream)
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (_paused) { await Task.Delay(50, ct); continue; }
                        int total = 0;
                        while (total < frameBytes)
                        {
                            int r = await stream.ReadAsync(buf, total, frameBytes - total, ct);
                            if (r == 0) return; // EOF
                            total += r;
                        }
                        var data = (byte[])buf.Clone();
                        _queue.Enqueue(new VideoFrame { Pts = pts, Data = data });
                        pts += frameDur;
                        while (_queue.Count > 80) await Task.Delay(5, ct);
                    }
                }
            }
            catch { /* process killed or cancelled */ }
        }

        private async Task ReadAudio(CancellationToken ct)
        {
            try
            {
                using (var stream = _audioProc.StandardOutput.BaseStream)
                {
                    var buf = new byte[8192];
                    while (!ct.IsCancellationRequested)
                    {
                        if (_paused) { await Task.Delay(50, ct); continue; }
                        int r = await stream.ReadAsync(buf, 0, buf.Length, ct);
                        if (r == 0) break; // EOF
                        _waveProvider.AddSamples(buf, 0, r);
                    }
                }
            }
            catch { /* process killed or cancelled */ }
        }

        private double CurrentClock()
        {
            if (_paused) return _pauseClock;
            if (_hasAudio && _waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
            {
                long pos = _waveOut.GetPosition();
                double sec = pos / (double)(44100 * 2 * 2);
                return _startOffset + sec;
            }
            return _startOffset + (DateTime.Now - _startWall).TotalSeconds;
        }

        private void RenderFrame()
        {
            if (_isImage) { Render(); return; }

            double clock = CurrentClock();
            while (_queue.TryPeek(out var f))
            {
                if (f.Pts <= clock + 0.06)
                {
                    if (_queue.TryDequeue(out var df))
                    {
                        _displayFrame = df;
                        _frameData = df.Data;
                        _hasFrame = true;
                        _texDirty = true;
                    }
                }
                else break;
            }

            Render();
            UpdateProgress(clock);

            if (_duration > 0 && clock >= _duration - 0.05)
            {
                _ended = true;
                _paused = false;
                try { _waveOut?.Stop(); } catch { }
                StopPipelines();
                _renderTimer.Stop();
                UpdatePlayButton();
            }
        }

        private void Render()
        {
            if (_glc.IsDisposed || _tex == 0) return;
            _glc.MakeCurrent();
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            if (_hasFrame && _frameW > 0 && _frameH > 0)
            {
                GL.Enable(EnableCap.Texture2D);
                GL.BindTexture(TextureTarget.Texture2D, _tex);
                if (_texDirty)
                {
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb,
                        _frameW, _frameH, 0, OpenTK.Graphics.OpenGL.PixelFormat.Rgb, PixelType.UnsignedByte, _frameData);
                    _texDirty = false;
                }
                GL.Color3(1f, 1f, 1f);

                float viewAspect = (float)_glc.Width / _glc.Height;
                float imgAspect = (float)_frameW / _frameH;
                float qw, qh;
                if (imgAspect > viewAspect) { qw = 1f; qh = viewAspect / imgAspect; }
                else { qw = imgAspect / viewAspect; qh = 1f; }

                GL.Begin(PrimitiveType.Quads);
                GL.TexCoord2(0, 0); GL.Vertex2(-qw, qh);
                GL.TexCoord2(1, 0); GL.Vertex2(qw, qh);
                GL.TexCoord2(1, 1); GL.Vertex2(qw, -qh);
                GL.TexCoord2(0, 1); GL.Vertex2(-qw, -qh);
                GL.End();
                GL.Disable(EnableCap.Texture2D);
            }

            _glc.SwapBuffers();
        }

        private void UpdateProgress(double clock)
        {
            if (_isImage || _lblTime == null) return;
            if (_duration > 0 && !_seeking)
            {
                int pct = (int)Math.Max(0, Math.Min(1000, clock / _duration * 1000));
                if (_seekBar.Value != pct) _seekBar.Value = pct;
            }
            _lblTime.Text = FFmpegHelper.FormatDuration(clock) + " / " + FFmpegHelper.FormatDuration(_duration);
        }

        private void BtnPlay_Click(object sender, EventArgs e)
        {
            if (_isImage || _btnPlay == null) return;
            if (!_started) { StartPlayback(); return; }
            if (_ended) { Seek(0); return; }
            if (_paused) ResumePlayback(); else PausePlayback();
        }

        private void PausePlayback()
        {
            _paused = true;
            _pauseClock = CurrentClock();
            try { _waveOut?.Pause(); } catch { }
            UpdatePlayButton();
        }

        private void ResumePlayback()
        {
            _paused = false;
            _startWall = DateTime.Now - TimeSpan.FromSeconds(_pauseClock - _startOffset);
            try { _waveOut?.Play(); } catch { }
            UpdatePlayButton();
        }

        private void Seek(double sec)
        {
            if (_isImage || !_started) return;
            if (sec < 0) sec = 0;
            if (_duration > 0 && sec > _duration) sec = _duration;

            StopPipelines();
            _queue = new ConcurrentQueue<VideoFrame>();
            _displayFrame = null;
            _hasFrame = false;
            try { _waveProvider?.ClearBuffer(); } catch { }

            _startOffset = sec;
            _startWall = DateTime.Now;
            _paused = false;
            _ended = false;

            StartPipelines(sec);
            _ = Task.Run(() => ReadVideo(_cts.Token));
            if (_hasAudio)
            {
                _ = Task.Run(() => ReadAudio(_cts.Token));
                try { _waveOut.Play(); } catch { }
            }
            if (!_renderTimer.Enabled) _renderTimer.Start();
            UpdatePlayButton();
        }

        private void StopPipelines()
        {
            try { if (_videoProc != null && !_videoProc.HasExited) _videoProc.Kill(); } catch { }
            try { if (_audioProc != null && !_audioProc.HasExited) _audioProc.Kill(); } catch { }
            try { _videoProc?.Dispose(); } catch { }
            try { _audioProc?.Dispose(); } catch { }
            _videoProc = null;
            _audioProc = null;
        }

        private void UpdatePlayButton()
        {
            if (_isImage || _btnPlay == null) return;
            _btnPlay.Text = _ended ? "↻ 重播" : (_paused ? "▶ 播放" : "⏸ 暂停");
        }

        // ====================================================================
        //  Images
        // ====================================================================

        private void LoadImage()
        {
            try
            {
                using (var bmp = new Bitmap(_path))
                {
                    _frameW = bmp.Width;
                    _frameH = bmp.Height;
                    _frameData = BitmapToRgb24(bmp);
                    _hasFrame = true;
                    _texDirty = true;
                    Render();
                }
            }
            catch (Exception ex)
            {
                _hasFrame = false;
                MessageBox.Show(this, "无法打开图片：\n" + ex.Message, "预览",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static byte[] BitmapToRgb24(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = new byte[w * h * 3];
            var rect = new Rectangle(0, 0, w, h);
            var bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            int stride = bmpData.Stride;
            var row = new byte[stride];
            try
            {
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        IntPtr.Add(bmpData.Scan0, y * stride), row, 0, stride);
                    for (int x = 0; x < w; x++)
                    {
                        int src = x * 3;
                        int dst = (y * w + x) * 3;
                        data[dst] = row[src + 2];     // R
                        data[dst + 1] = row[src + 1]; // G
                        data[dst + 2] = row[src];     // B
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
            return data;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            try { _renderTimer?.Stop(); } catch { }
            try { _cts?.Cancel(); } catch { }
            StopPipelines();
            try { _waveOut?.Stop(); } catch { }
            try { _waveOut?.Dispose(); } catch { }
            _waveOut = null;
        }
    }
}
