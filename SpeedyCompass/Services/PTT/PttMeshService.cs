using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace SpeedyCompass.Services;
public class PttMeshService : IPttMeshService
{
    private const int FrameSize = 160;
    private const int MediaLogSampleWindow = 100;
    private static readonly TimeSpan AudioLevelPublishInterval = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan AudioLevelStaleAfter = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OfferRetryInterval = TimeSpan.FromSeconds(3);

    private readonly SignalRService _signalR;
    private readonly IRealTimeAudio _audioEngine;

    // We use GoogleId as the key instead of ConnectionId so it survives reconnects!
    private readonly ConcurrentDictionary<string, RTCPeerConnection> _peers = new();
    private readonly ConcurrentDictionary<string, byte> _pendingPeerCreations = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _negotiationByPeer = new();
    private readonly ConcurrentDictionary<string, bool> _remoteDescriptionSetByPeer = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RTCIceCandidateInit>> _pendingIceByPeer = new();
    private readonly ConcurrentDictionary<string, bool> _localOfferSentByPeer = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastOfferAttemptUtcByPeer = new();

    private readonly object _audioLevelLock = new();
    private DateTime _lastAudioLevelPublishUtc = DateTime.MinValue;
    private DateTime _lastOutgoingSampleUtc = DateTime.MinValue;
    private DateTime _lastIncomingSampleUtc = DateTime.MinValue;
    private float _lastOutgoingLevel = 0f;
    private float _lastIncomingLevel = 0f;

    public event Action<float, float>? AudioLevelsUpdated; // outgoingLevel, incomingLevel (0..1)

    private string _currentGroupName = string.Empty;
    private string _myGoogleId = string.Empty;
    private bool _isMyMicOpen = false;
    private int _sentFrameCount = 0;
    private int _receivedFrameCount = 0;

    public PttMeshService(SignalRService signalR, IRealTimeAudio audioEngine)
    {
        _signalR = signalR;
        _audioEngine = audioEngine;

        // Wire up the microphone pipeline
        _audioEngine.OnAudioCaptured += HandleLiveMicrophoneData;

        // Wire up the SignalR listeners
        _signalR.WebRtcOfferReceived += HandleIncomingOffer;
        _signalR.WebRtcAnswerReceived += HandleIncomingAnswer;
        _signalR.IceCandidateReceived += HandleIncomingIceCandidate;

        // PTT Control
        _signalR.PttLocked += OnPttLocked;
        _signalR.PttReleased += OnPttReleased;

        Log("PttMeshService initialized. Waiting for session details.");
    }

    public void InitializeSession(string groupName, string myGoogleId)
    {
        _currentGroupName = groupName;
        _myGoogleId = myGoogleId;
        Log($"Session initialized. Group='{_currentGroupName}', MyGoogleId='{_myGoogleId}'.");
    }

    // Call this when you get a new Roster list!
    public async Task SyncMeshNetworkAsync(List<Models.Rider> currentRoster)
    {
        Log($"SyncMeshNetworkAsync called. RosterCount={currentRoster?.Count ?? 0}, ExistingPeers={_peers.Count}.");

        if (currentRoster == null || currentRoster.Count == 0)
            return;

        var targetOnlinePeerIds = currentRoster
            .Where(r => r.GoogleId != _myGoogleId && r.IsOnline)
            .Select(r => r.GoogleId)
            .ToHashSet(StringComparer.Ordinal);

        // Full-mesh cleanup: remove stale/offline peers no longer in roster.
        foreach (var stalePeerId in _peers.Keys.Where(id => !targetOnlinePeerIds.Contains(id)).ToList())
        {
            if (_peers.TryRemove(stalePeerId, out var stalePc))
            {
                try { stalePc.close(); } catch { /* no-op */ }
                RemovePeerState(stalePeerId);
                Log($"Removed stale peer '{stalePeerId}' from mesh.");
            }
        }

        // Full-mesh create: connect to every online rider except self.
        foreach (var rider in currentRoster)
        {
            if (rider.GoogleId == _myGoogleId || !rider.IsOnline) continue;
            await TryCreatePeerConnectionAsync(
                rider.GoogleId,
                isInitiator: ShouldInitiateOffer(_myGoogleId, rider.GoogleId));
        }

        // --- Self-healing logic ---
        foreach (var peerId in targetOnlinePeerIds)
        {
            if (!_peers.TryGetValue(peerId, out var pc)) continue;

            var connected = pc.connectionState == RTCPeerConnectionState.connected;
            var remoteSet = _remoteDescriptionSetByPeer.TryGetValue(peerId, out var rs) && rs;
            var shouldInitiate = ShouldInitiateOffer(_myGoogleId, peerId);

            if (!connected && shouldInitiate && !remoteSet && ShouldRetryOfferNow(peerId))
            {
                Log($"Offer self-heal: retrying offer to '{peerId}'.");
                await CreateAndSendOfferAsync(peerId, pc);
            }
        }
    }

    private async Task TryCreatePeerConnectionAsync(string targetGoogleId, bool isInitiator)
    {
        if (_peers.ContainsKey(targetGoogleId))
            return;

        if (!_pendingPeerCreations.TryAdd(targetGoogleId, 0))
        {
            Log($"Peer creation already in progress for '{targetGoogleId}'. Skipping duplicate attempt.");
            return;
        }

        try
        {
            if (!_peers.ContainsKey(targetGoogleId))
            {
                Log($"Creating {(isInitiator ? "initiator" : "responder")} peer for '{targetGoogleId}'.");
                await CreatePeerConnectionAsync(targetGoogleId, isInitiator);
            }
        }
        finally
        {
            _pendingPeerCreations.TryRemove(targetGoogleId, out _);
        }
    }

    private async Task CreatePeerConnectionAsync(string targetGoogleId, bool isInitiator)
    {
        Log($"CreatePeerConnectionAsync start. Target='{targetGoogleId}', IsInitiator={isInitiator}.");

        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer> {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer
                    {
                        urls = "turn:openrelay.metered.ca:80",
                        username = "openrelayproject",
                        credential = "openrelayproject",
                        credentialType = RTCIceCredentialType.password
                    }
            },
            iceTransportPolicy = RTCIceTransportPolicy.all // Try all methods
        };

        var pc = new RTCPeerConnection(config);

        if (!_peers.TryAdd(targetGoogleId, pc))
        {
            Log($"Peer already exists for '{targetGoogleId}'. Discarding duplicate RTCPeerConnection.");
            try { pc.close(); } catch { /* no-op */ }
            return;
        }

        InitializePeerState(targetGoogleId);

        // Connection-state diagnostics for one-way failure pinpointing.
        pc.onconnectionstatechange += (state) =>
        {
            Log($"Peer '{targetGoogleId}' connection state => {state}.");

            if (state == RTCPeerConnectionState.failed ||
                state == RTCPeerConnectionState.disconnected ||
                state == RTCPeerConnectionState.closed)
            {
                Log($"[ONE-WAY-DIAG] Peer '{targetGoogleId}' transport degraded at connectionState={state}.");
            }
        };

        pc.oniceconnectionstatechange += (state) =>
        {
            Log($"Peer '{targetGoogleId}' ICE state => {state}.");

            if (state == RTCIceConnectionState.failed ||
                state == RTCIceConnectionState.disconnected ||
                state == RTCIceConnectionState.closed)
            {
                Log($"[ONE-WAY-DIAG] Peer '{targetGoogleId}' ICE degraded at iceConnectionState={state}.");
            }
        };

        // 1. Setup Audio Track
        var audioFormat = new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMU);
        pc.addTrack(new MediaStreamTrack(SDPMediaTypesEnum.audio, false, new List<SDPAudioVideoMediaFormat> { audioFormat }));

        // 2. Handle ICE Candidates
        pc.onicecandidate += async (candidate) =>
        {
            if (candidate != null)
            {
                var json = JsonSerializer.Serialize(new { candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex });
                Log($"Local ICE generated for '{targetGoogleId}'. Mid={candidate.sdpMid}, Line={candidate.sdpMLineIndex}.");
                await _signalR.SendIceCandidate(_currentGroupName, targetGoogleId, json);
            }
        };

        // 3. Route incoming audio to speakers!
        pc.OnRtpPacketReceived += HandleIncomingRtpPacket;

        // 4. Start Handshake if Initiator
        if (isInitiator)
        {
            await CreateAndSendOfferAsync(targetGoogleId, pc);
        }
    }

    private void InitializePeerState(string peerGoogleId)
    {
        _remoteDescriptionSetByPeer.TryAdd(peerGoogleId, false);
        _pendingIceByPeer.TryAdd(peerGoogleId, new ConcurrentQueue<RTCIceCandidateInit>());
        _negotiationByPeer.GetOrAdd(peerGoogleId, _ => new SemaphoreSlim(1, 1));
        _localOfferSentByPeer.TryAdd(peerGoogleId, false);
    }

    private void RemovePeerState(string peerGoogleId)
    {
        _remoteDescriptionSetByPeer.TryRemove(peerGoogleId, out _);
        _pendingIceByPeer.TryRemove(peerGoogleId, out _);
        _localOfferSentByPeer.TryRemove(peerGoogleId, out _);
        _lastOfferAttemptUtcByPeer.TryRemove(peerGoogleId, out _);

        if (_negotiationByPeer.TryRemove(peerGoogleId, out var gate))
        {
            gate.Dispose();
        }
    }

    private SemaphoreSlim GetNegotiationGate(string peerGoogleId)
    {
        return _negotiationByPeer.GetOrAdd(peerGoogleId, _ => new SemaphoreSlim(1, 1));
    }

    private void FlushPendingIce(string peerGoogleId, RTCPeerConnection pc)
    {
        if (!_pendingIceByPeer.TryGetValue(peerGoogleId, out var queue))
            return;

        while (queue.TryDequeue(out var queuedIce))
        {
            pc.addIceCandidate(queuedIce);
            Log($"Flushed queued ICE for '{peerGoogleId}'.");
        }
    }

    private async Task CreateAndSendOfferAsync(string targetGoogleId, RTCPeerConnection pc)
    {
        var gate = GetNegotiationGate(targetGoogleId);
        await gate.WaitAsync();

        try
        {
            _lastOfferAttemptUtcByPeer[targetGoogleId] = DateTime.UtcNow;

            var offer = pc.createOffer(null);
            await pc.setLocalDescription(offer);
            await _signalR.SendWebRtcOffer(_currentGroupName, targetGoogleId, offer.toJSON());
            Log($"WebRTC offer sent to '{targetGoogleId}'.");

            _localOfferSentByPeer[targetGoogleId] = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async void HandleIncomingOffer(string senderGoogleId, string sdp)
    {
        try
        {
            Log($"Offer received from '{senderGoogleId}'. SDP length={sdp?.Length ?? 0}.");

            await TryCreatePeerConnectionAsync(senderGoogleId, isInitiator: false);

            if (!_peers.TryGetValue(senderGoogleId, out var pc))
            {
                Log($"Offer received but peer lookup failed for '{senderGoogleId}'.");
                return;
            }

            if (!RTCSessionDescriptionInit.TryParse(sdp, out var desc) || desc == null)
            {
                Log($"Offer parse failed for sender '{senderGoogleId}'.");
                return;
            }

            var gate = GetNegotiationGate(senderGoogleId);
            await gate.WaitAsync();
            try
            {
                pc.setRemoteDescription(desc);
                _remoteDescriptionSetByPeer[senderGoogleId] = true;
                FlushPendingIce(senderGoogleId, pc);
                Log($"Remote offer applied for '{senderGoogleId}'.");

                var answer = pc.createAnswer(null);
                await pc.setLocalDescription(answer);
                await _signalR.SendWebRtcAnswer(_currentGroupName, senderGoogleId, answer.toJSON());
                Log($"Answer sent to '{senderGoogleId}'.");
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PTT-MESH] HandleIncomingOffer error: {ex}");
        }
    }

    private async void HandleIncomingAnswer(string senderGoogleId, string sdp)
    {
        if (!_peers.TryGetValue(senderGoogleId, out var pc))
        {
            Log($"Answer received for unknown peer '{senderGoogleId}'.");
            return;
        }

        if (!RTCSessionDescriptionInit.TryParse(sdp, out var desc) || desc == null)
        {
            Log($"Answer parse failed for sender '{senderGoogleId}'.");
            return;
        }

        var gate = GetNegotiationGate(senderGoogleId);
        await gate.WaitAsync();
        try
        {
            pc.setRemoteDescription(desc);
            _remoteDescriptionSetByPeer[senderGoogleId] = true;
            FlushPendingIce(senderGoogleId, pc);
            Log($"Remote answer applied for '{senderGoogleId}'.");
        }
        finally
        {
            gate.Release();
        }
    }

    private async void HandleIncomingIceCandidate(string senderGoogleId, string json)
    {
        await TryCreatePeerConnectionAsync(senderGoogleId, isInitiator: false);

        if (!_peers.TryGetValue(senderGoogleId, out var pc))
        {
            Log($"ICE received for unknown peer '{senderGoogleId}'.");
            return;
        }

        if (!RTCIceCandidateInit.TryParse(json, out var ice) || ice == null)
        {
            Log($"ICE parse failed for sender '{senderGoogleId}'. PayloadLength={json?.Length ?? 0}.");
            return;
        }

        if (!_remoteDescriptionSetByPeer.TryGetValue(senderGoogleId, out var remoteSet) || !remoteSet)
        {
            var queue = _pendingIceByPeer.GetOrAdd(senderGoogleId, _ => new ConcurrentQueue<RTCIceCandidateInit>());
            queue.Enqueue(ice);
            Log($"Queued ICE for '{senderGoogleId}' until remote description is set.");

            if (ShouldInitiateOffer(_myGoogleId, senderGoogleId) &&
                _peers.TryGetValue(senderGoogleId, out var peer) &&
                (!_localOfferSentByPeer.TryGetValue(senderGoogleId, out var sent) || !sent))
            {
                Log($"ICE-first fallback: sending offer to '{senderGoogleId}'.");
                await CreateAndSendOfferAsync(senderGoogleId, peer);
            }
            return;
        }

        pc.addIceCandidate(ice);
        Log($"Remote ICE added for '{senderGoogleId}'.");
    }

    // --- PTT LOGIC ---
    private void OnPttLocked(string speakerName)
    {
        var myName = Preferences.Default.Get("username", "");
        _isMyMicOpen = (speakerName == myName);

        Log($"PttLocked received. Speaker='{speakerName}', LocalUser='{myName}', MicOpen={_isMyMicOpen}.");
        PublishAudioLevels(0f, 0f, force: true);

        if (_isMyMicOpen) _audioEngine.StartRecording();
    }

    private void OnPttReleased()
    {
        Log($"PttReleased received. MicWasOpen={_isMyMicOpen}.");

        if (_isMyMicOpen)
        {
            _audioEngine.StopRecording();
            _isMyMicOpen = false;
            Log("Microphone stopped after PttReleased.");
        }

        PublishAudioLevels(0f, 0f, force: true);
    }

    private void HandleLiveMicrophoneData(object sender, byte[] rawPcmData)
    {
        var hasConnectedPeer = _peers.Values.Any(p => p.connectionState == RTCPeerConnectionState.connected);
        if (!_isMyMicOpen || !hasConnectedPeer) return;

        try
        {
            int samples = rawPcmData.Length / 2;
            byte[] pcmuData = new byte[samples];

            for (int i = 0; i < samples; i++)
            {
                short pcmSample = BitConverter.ToInt16(rawPcmData, i * 2);
                pcmuData[i] = MuLawEncoder.LinearToMuLawSample(pcmSample);
            }

            foreach (var pc in _peers.Values)
            {
                if (pc.connectionState == RTCPeerConnectionState.connected)
                {
                    pc.SendAudio((uint)FrameSize, pcmuData);
                }
            }

            PublishAudioLevels(CalculateNormalizedLevel(rawPcmData), null);

            _sentFrameCount++;
            if (_sentFrameCount % MediaLogSampleWindow == 0)
            {
                Log($"Sent audio frames={_sentFrameCount}, ConnectedPeers={_peers.Values.Count(p => p.connectionState == RTCPeerConnectionState.connected)}, RawBytes={rawPcmData.Length}, PcmuBytes={pcmuData.Length}.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PTT-MESH] HandleLiveMicrophoneData error: {ex}");
        }
    }

    private void HandleIncomingRtpPacket(IPEndPoint remoteEP, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType == SDPMediaTypesEnum.audio)
        {
            try
            {
                byte[] pcmuData = rtpPacket.Payload;
                byte[] pcmData = new byte[pcmuData.Length * 2];

                for (int i = 0; i < pcmuData.Length; i++)
                {
                    short pcmSample = MuLawDecoder.MuLawToLinearSample(pcmuData[i]);
                    byte[] sampleBytes = BitConverter.GetBytes(pcmSample);
                    pcmData[i * 2] = sampleBytes[0];
                    pcmData[(i * 2) + 1] = sampleBytes[1];
                }

                _audioEngine.PlayAudio(pcmData);
                PublishAudioLevels(null, CalculateNormalizedLevel(pcmData));

                _receivedFrameCount++;
                if (_receivedFrameCount % MediaLogSampleWindow == 0)
                {
                    Log($"Received audio frames={_receivedFrameCount} from {remoteEP}. PayloadBytes={pcmuData.Length}, DecodedBytes={pcmData.Length}.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PTT-MESH] HandleIncomingRtpPacket error: {ex}");
            }
        }
    }

    private void PublishAudioLevels(float? outgoing, float? incoming, bool force = false)
    {
        float tx;
        float rx;

        lock (_audioLevelLock)
        {
            var now = DateTime.UtcNow;

            if (outgoing.HasValue) { _lastOutgoingLevel = outgoing.Value; _lastOutgoingSampleUtc = now; }
            if (incoming.HasValue) { _lastIncomingLevel = incoming.Value; _lastIncomingSampleUtc = now; }

            if ((now - _lastOutgoingSampleUtc) > AudioLevelStaleAfter) _lastOutgoingLevel = 0f;
            if ((now - _lastIncomingSampleUtc) > AudioLevelStaleAfter) _lastIncomingLevel = 0f;

            if (!force && (now - _lastAudioLevelPublishUtc) < AudioLevelPublishInterval) return;

            _lastAudioLevelPublishUtc = now;
            tx = _lastOutgoingLevel;
            rx = _lastIncomingLevel;
        }

        AudioLevelsUpdated?.Invoke(tx, rx);
    }

    private static float CalculateNormalizedLevel(byte[] pcm16Data)
    {
        if (pcm16Data == null || pcm16Data.Length < 2)
            return 0f;

        int sampleCount = pcm16Data.Length / 2;
        if (sampleCount == 0)
            return 0f;

        double sumSquares = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(pcm16Data, i * 2);
            double normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }

        double rms = Math.Sqrt(sumSquares / sampleCount);
        double boosted = Math.Min(1.0, rms * 4.0); // visual scaling for better readability
        return (float)boosted;
    }

    private static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[PTT-MESH] {message}");
    }

    private static bool ShouldInitiateOffer(string myGoogleId, string peerGoogleId)
    {
        // deterministic role split for each pair in the mesh
        return string.CompareOrdinal(myGoogleId, peerGoogleId) < 0;
    }

    // Add helper
    private bool ShouldRetryOfferNow(string peerGoogleId)
    {
        var now = DateTime.UtcNow;
        var last = _lastOfferAttemptUtcByPeer.GetOrAdd(peerGoogleId, DateTime.MinValue);

        if ((now - last) < OfferRetryInterval)
            return false;

        _lastOfferAttemptUtcByPeer[peerGoogleId] = now;
        return true;
    }
}