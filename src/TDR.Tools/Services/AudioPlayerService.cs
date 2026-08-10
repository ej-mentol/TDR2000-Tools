using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TDR.Tools.Services
{
    public class AudioWavInfo
    {
        public int SampleRate { get; set; } = 22050;
        public int BitsPerSample { get; set; } = 16;
        public int Channels { get; set; } = 1;
        public double DurationSeconds { get; set; } = 0.0;
        public string FormatText => $"{SampleRate / 1000.0:F1} kHz @ {BitsPerSample}-bit {(Channels == 1 ? "Mono" : "Stereo")}";
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

        private bool _isPlaying;
        private bool _isMuted;
        private bool _isLooping;
        private System.Threading.CancellationTokenSource? _progressCts;

        public bool IsPlaying => _isPlaying;
        public bool IsMuted => _isMuted;
        public bool IsLooping
        {
            get => _isLooping;
            set => _isLooping = value;
        }

        public event Action<bool>? PlaybackStateChanged;
        public event Action<double, double, double>? ProgressUpdated; // (elapsedSeconds, totalDurationSeconds, percent)

        public AudioWavInfo ParseWavHeader(byte[] bytes)
        {
            var info = new AudioWavInfo();
            if (bytes == null || bytes.Length < 44) return info;

            try
            {
                if (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F')
                {
                    int channels = BitConverter.ToInt16(bytes, 22);
                    int sampleRate = BitConverter.ToInt32(bytes, 24);
                    int byteRate = BitConverter.ToInt32(bytes, 28);
                    int bitsPerSample = BitConverter.ToInt16(bytes, 34);

                    info.Channels = channels > 0 ? channels : 1;
                    info.SampleRate = sampleRate > 0 ? sampleRate : 22050;
                    info.BitsPerSample = bitsPerSample > 0 ? bitsPerSample : 16;

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

            try
            {
                var info = ParseWavHeader(wavBytes);
                if (!_isMuted && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    PlaySound(wavBytes, IntPtr.Zero, SND_ASYNC | SND_MEMORY);
                }

                _isPlaying = true;
                PlaybackStateChanged?.Invoke(true);

                _progressCts = new System.Threading.CancellationTokenSource();
                var token = _progressCts.Token;
                double duration = info.DurationSeconds > 0 ? info.DurationSeconds : 1.0;
                DateTime startTime = DateTime.UtcNow;

                Task.Run(async () =>
                {
                    while (_isPlaying && !token.IsCancellationRequested)
                    {
                        double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        double percent = Math.Min(100.0, (elapsed / duration) * 100.0);
                        ProgressUpdated?.Invoke(elapsed, duration, percent);

                        if (elapsed >= duration)
                        {
                            if (_isLooping)
                            {
                                startTime = DateTime.UtcNow;
                                if (!_isMuted && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                {
                                    PlaySound(wavBytes, IntPtr.Zero, SND_ASYNC | SND_MEMORY);
                                }
                            }
                            else
                            {
                                _isPlaying = false;
                                ProgressUpdated?.Invoke(0.0, duration, 0.0);
                                PlaybackStateChanged?.Invoke(false);
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
                }, token);
            }
            catch
            {
                _isPlaying = false;
                PlaybackStateChanged?.Invoke(false);
            }
        }

        public void Stop()
        {
            try
            {
                _progressCts?.Cancel();
                _progressCts = null;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    PlaySound(null, IntPtr.Zero, SND_PURGE);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
            finally
            {
                _isPlaying = false;
                ProgressUpdated?.Invoke(0.0, 0.0, 0.0);
                PlaybackStateChanged?.Invoke(false);
            }
        }

        public void TogglePlay(byte[] wavBytes)
        {
            if (_isPlaying)
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
            _isMuted = !_isMuted;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (_isMuted)
                {
                    PlaySound(null, IntPtr.Zero, SND_PURGE);
                }
                else if (_isPlaying && currentWavBytes != null)
                {
                    PlaySound(currentWavBytes, IntPtr.Zero, SND_ASYNC | SND_MEMORY);
                }
            }
        }
    }
}
