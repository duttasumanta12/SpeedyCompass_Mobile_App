#if ANDROID
using Android.App;
using Android.Media;
using Android.Media.Audiofx;
using System.Diagnostics;

namespace SpeedyCompass.Services;

public class AndroidRealTimeAudio : IRealTimeAudio
{
    public event EventHandler<byte[]>? OnAudioCaptured;

    private AudioRecord? _audioRecord;
    private AudioTrack? _audioTrack;
    private CancellationTokenSource? _recordCts;

    private NoiseSuppressor? _noiseSuppressor;
    private AcousticEchoCanceler? _echoCanceler;
    private AutomaticGainControl? _automaticGainControl;

    private readonly object _playbackLock = new();
    private readonly object _recordLock = new();

    private readonly int _sampleRate = 8000;
    private const int FrameBytes = 320; // Exactly 20 ms @ 8kHz 16-bit mono
    private const float PlaybackGain = 1.35f;
    private volatile bool _isRecording;

    public AndroidRealTimeAudio()
    {
        var audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Android.Content.Context.AudioService);
        if (audioManager != null)
        {
            audioManager.Mode = Mode.InCommunication;
            audioManager.SpeakerphoneOn = true;

            var maxVoiceCall = audioManager.GetStreamMaxVolume(Android.Media.Stream.VoiceCall);
            audioManager.SetStreamVolume(Android.Media.Stream.VoiceCall, maxVoiceCall, VolumeNotificationFlags.RemoveSoundAndVibrate);

            var maxMusic = audioManager.GetStreamMaxVolume(Android.Media.Stream.Music);
            audioManager.SetStreamVolume(Android.Media.Stream.Music, maxMusic, VolumeNotificationFlags.RemoveSoundAndVibrate);
        }

        var minPlayBuffer = AudioTrack.GetMinBufferSize(_sampleRate, ChannelOut.Mono, Encoding.Pcm16bit);

        _audioTrack = new AudioTrack(
            Android.Media.Stream.VoiceCall,
            _sampleRate,
            ChannelOut.Mono,
            Encoding.Pcm16bit,
            Math.Max(minPlayBuffer * 4, 8192),
            AudioTrackMode.Stream);

        _audioTrack.Play();
    }

    public void StartRecording()
    {
        lock (_recordLock)
        {
            if (_isRecording) return;

            var minRecBuffer = AudioRecord.GetMinBufferSize(_sampleRate, ChannelIn.Mono, Encoding.Pcm16bit);

            _audioRecord = new AudioRecord(
                AudioSource.VoiceCommunication, // Best input source for AEC/NS/AGC pipeline
                _sampleRate,
                ChannelIn.Mono,
                Encoding.Pcm16bit,
                Math.Max(minRecBuffer * 2, FrameBytes * 10));

            if (_audioRecord.State != State.Initialized)
                throw new InvalidOperationException("AudioRecord failed to initialize.");

            EnableVoiceProcessingEffects(_audioRecord.AudioSessionId);

            _recordCts = new CancellationTokenSource();
            _isRecording = true;
            _audioRecord.StartRecording();

            _ = Task.Run(() => CaptureLoop(_recordCts.Token));
        }
    }

    private void EnableVoiceProcessingEffects(int audioSessionId)
    {
        try
        {
            if (NoiseSuppressor.IsAvailable)
            {
                _noiseSuppressor = NoiseSuppressor.Create(audioSessionId);
                if (_noiseSuppressor != null) _noiseSuppressor.SetEnabled(true);
            }

            if (AcousticEchoCanceler.IsAvailable)
            {
                _echoCanceler = AcousticEchoCanceler.Create(audioSessionId);
                if (_echoCanceler != null) _echoCanceler.SetEnabled(true);
            }

            if (AutomaticGainControl.IsAvailable)
            {
                _automaticGainControl = AutomaticGainControl.Create(audioSessionId);
                if (_automaticGainControl != null) _automaticGainControl.SetEnabled(true);
            }

            Debug.WriteLine("[AUDIO] Voice effects enabled (NS/AEC/AGC when available).");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AUDIO] Failed enabling voice effects: {ex}");
        }
    }

    private void CaptureLoop(CancellationToken token)
    {
        var readBuffer = new byte[FrameBytes];
        var accumulator = new List<byte>(FrameBytes * 2);

        try
        {
            while (!token.IsCancellationRequested && _isRecording)
            {
                // Note: This is a blocking call. 
                var bytesRead = _audioRecord?.Read(readBuffer, 0, readBuffer.Length) ?? 0;

                if (bytesRead > 0)
                {
                    // Accumulate bytes until we have exactly our 320-byte payload
                    accumulator.AddRange(readBuffer.Take(bytesRead));

                    while (accumulator.Count >= FrameBytes)
                    {
                        var frame = accumulator.Take(FrameBytes).ToArray();
                        accumulator.RemoveRange(0, FrameBytes);

                        // Fire event with EXACTLY 320 bytes
                        OnAudioCaptured?.Invoke(this, frame);
                    }
                }
            }
        }
        catch (Java.Lang.Exception ex)
        {
            Debug.WriteLine($"AudioRecord read interrupted: {ex.Message}");
        }
    }

    public void StopRecording()
    {
        lock (_recordLock)
        {
            if (!_isRecording) return;
            _isRecording = false;

            try { _recordCts?.Cancel(); } catch { }

            // Stop MUST be called before Release to unblock the Read() method safely
            try
            {
                if (_audioRecord?.RecordingState == RecordState.Recording)
                {
                    _audioRecord?.Stop();
                }
            }
            catch { }

            try { _noiseSuppressor?.Release(); } catch { }
            try { _echoCanceler?.Release(); } catch { }
            try { _automaticGainControl?.Release(); } catch { }

            _noiseSuppressor = null;
            _echoCanceler = null;
            _automaticGainControl = null;

            try { _audioRecord?.Release(); } catch { }

            _audioRecord = null;
            _recordCts?.Dispose();
            _recordCts = null;
        }
    }

    public void PlayAudio(byte[] pcmData)
    {
        if (pcmData == null || pcmData.Length == 0) return;

        lock (_playbackLock)
        {
            if (_audioTrack == null) return;

            if (_audioTrack.PlayState != PlayState.Playing)
                _audioTrack.Play();

            var boosted = ApplyGainPcm16(pcmData, PlaybackGain);
            var written = _audioTrack.Write(boosted, 0, boosted.Length);
            if (written < 0)
                Debug.WriteLine($"AudioTrack.Write failed: {written}");
        }
    }

    private static byte[] ApplyGainPcm16(byte[] pcmData, float gain)
    {
        var boosted = new byte[pcmData.Length];

        for (int i = 0; i < pcmData.Length - 1; i += 2)
        {
            short sample = BitConverter.ToInt16(pcmData, i);
            int scaled = (int)(sample * gain);
            scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
            short clipped = (short)scaled;

            var bytes = BitConverter.GetBytes(clipped);
            boosted[i] = bytes[0];
            boosted[i + 1] = bytes[1];
        }

        return boosted;
    }
}
#endif