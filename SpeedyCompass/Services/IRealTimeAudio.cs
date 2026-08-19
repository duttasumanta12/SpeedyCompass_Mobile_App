namespace SpeedyCompass.Services;

public interface IRealTimeAudio
{
    // Triggered every ~20ms when the mic captures data
    event EventHandler<byte[]> OnAudioCaptured;

    void StartRecording();
    void StopRecording();

    // Instantly queues incoming WebRTC bytes to the speaker
    void PlayAudio(byte[] pcmData);
}