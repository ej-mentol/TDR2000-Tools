using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TDR.Tools.Services
{
    public class AudioWavInfo
    {
        public bool IsValid { get; set; }
        public int SampleRate { get; set; } = 22050;
        public int BitsPerSample { get; set; } = 16;
        public int Channels { get; set; } = 1;
        public double DurationSeconds { get; set; } = 0.0;
        public string FormatText => IsValid
            ? $"{SampleRate / 1000.0:F1} kHz @ {BitsPerSample}-bit {(Channels == 1 ? "Mono" : "Stereo")}"
            : "Non-RIFF / Raw Audio";
    }

    public class AudioPlayerService
    {
        private static AudioPlayerService? _instance;
        public static AudioPlayerService Instance => _instance ??= new AudioPlayerService();

        [DllImport("winmm.dll", EntryPoint = "PlaySound", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool PlaySound(byte[]? ptr, IntPtr hModule, int flags);

        private const int SND_ASYNC = 0x0001;
        private const int SND_MEMORY = 0x0004;
        private const int SND_PURGE = 0x0040;

        private readonly object _lock = new();
        private bool _isPlaying;
        private bool _isMuted;
        private bool _isLooping;
        private GCHandle _pinnedHandle;
        private System.Threading.CancellationTokenSource? _progressCts;

        public bool IsPlaying { get { lock (_lock) return _isPlaying; } }
        public bool IsMuted { get { lock (_lock) return _isMuted; } }
        public bool IsLooping
        {
            get { lock (_lock) return _isLooping; }
            set { lock (_lock) _isLooping = value; }
        }

        public event Action<bool>? PlaybackStateChanged;
        public event Action<double, double, double>? ProgressUpdated; // (elapsedSeconds, totalDurationSeconds, percent)

        public AudioWavInfo ParseWavHeader(byte[] bytes)
        {
            var info = new AudioWavInfo();
            if (bytes == null || bytes.Length < 44) return info;

            try
            {
                if (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
                    bytes[8] == 'W' && bytes[9] == 'A' && bytes[10] == 'V' && bytes[11] == 'E')
                {
                    int channels = BitConverter.ToInt16(bytes, 22);
                    int sampleRate = BitConverter.ToInt32(bytes, 24);
                    int byteRate = BitConverter.ToInt32(bytes, 28);
                    int bitsPerSample = BitConverter.ToInt16(bytes, 34);

                    info.Channels = channels > 0 ? channels : 1;
                    info.SampleRate = sampleRate > 0 ? sampleRate : 22050;
                    info.BitsPerSample = bitsPerSample > 0 ? bitsPerSample : 16;
                    info.IsValid = true;

                    if (byteRate > 0)
                    {
                        info.DurationSeconds = (double)(bytes.Length - 44) / byteRate;
                    }
                }
            }
            catch
            {
                // Fallback on invalid header
            }

            return info;
        }

        public void PlayWav(byte[] wavBytes)
        {
            Stop();

            if (wavBytes == null || wavBytes.Length == 0) return;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                LogService.Instance.Info("[Audio] In-app WAV preview via WinMM is supported on Windows.");
                return;
            }

            lock (_lock)
            {
                try
                {
                    var info = ParseWavHeader(wavBytes);
                    if (!_isMuted)
                    {
                        // Pin memory for asynchronous WinMM playback so GC does not relocate it during playback
                        _pinnedHandle = GCHandle.Alloc(wavBytes, GCHandleType.Pinned);
                        bool ok = PlaySound(wavBytes, IntPtr.Zero, SND_ASYNC | SND_MEMORY);
                        if (!ok)
                        {
                            if (_pinnedHandle.IsAllocated)
                            {
                                _pinnedHandle.Free();
                            }
                            LogService.Instance.Warn("[Audio] PlaySound failed: file is not a playable RIFF/WAVE stream.");
                            return;
                        }
                    }

                    _isPlaying = true;
                    PlaybackStateChanged?.Invoke(true);

                    _progressCts = new System.Threading.CancellationTokenSource();
                    var token = _progressCts.Token;
                    double duration = info.DurationSeconds > 0 ? info.DurationSeconds : 1.0;
                    DateTime startTime = DateTime.UtcNow;

                    Task.Run(async () =>
                    {
                        try
                        {
                            while (!token.IsCancellationRequested)
                            {
                                lock (_lock)
                                {
                                    if (!_isPlaying) break;
                                }

                                double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                                double percent = Math.Min(100.0, (elapsed / duration) * 100.0);

                                try
                                {
                                    ProgressUpdated?.Invoke(elapsed, duration, percent);
                                }
                                catch { }

                                if (elapsed >= duration)
                                {
                                    bool loop;
                                    lock (_lock) { loop = _isLooping; }

                                    if (loop)
                                    {
                                        startTime = DateTime.UtcNow;
                                        if (!_isMuted && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                        {
                                            PlaySound(wavBytes, IntPtr.Zero, SND_ASYNC | SND_MEMORY);
                                        }
                                    }
                                    else
                                    {
                                        Stop();
                                        break;
                                    }
                                }

                                try
                                {
                                    await Task.Delay(40, token);
                                }
                                catch
                                {
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            Stop();
                        }
                    }, token);
                }
                catch
                {
                    Stop();
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                try
                {
                    _progressCts?.Cancel();
                    _progressCts = null;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        PlaySound(null, IntPtr.Zero, SND_PURGE);
                    }

                    if (_pinnedHandle.IsAllocated)
                    {
                        _pinnedHandle.Free();
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
                finally
                {
                    _isPlaying = false;
                    try { ProgressUpdated?.Invoke(0.0, 0.0, 0.0); } catch { }
                    try { PlaybackStateChanged?.Invoke(false); } catch { }
                }
            }
        }

        public void TogglePlay(byte[] wavBytes)
        {
            if (IsPlaying)
            {
                Stop();
            }
            else
            {
                PlayWav(wavBytes);
            }
        }

        public void ToggleMute(byte[]? currentWavBytes = null)
        {
            lock (_lock)
            {
                _isMuted = !_isMuted;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (_isMuted)
                    {
                        PlaySound(null, IntPtr.Zero, SND_PURGE);
                    }
                    else if (_isPlaying && currentWavBytes != null)
                    {
                        if (!_pinnedHandle.IsAllocated)
                        {
                            _pinnedHandle = GCHandle.Alloc(currentWavBytes, GCHandleType.Pinned);
                        }
                        PlaySound(currentWavBytes, IntPtr.Zero, SND_ASYNC | SND_MEMORY);
                    }
                }
            }
        }
    }
}
