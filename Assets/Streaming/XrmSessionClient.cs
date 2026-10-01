using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NativeWebSocket;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Client per il Session Server XRM (Pion SFU). Sostituisce SimpleWebRTC per questo server.
//
// Differenze rispetto al vecchio signaling mesh (SimpleWebRTC + server Node):
//  - UN SOLO peer: il server. Niente NEWPEER/NEWPEERACK, niente discovery.
//  - Il SERVER è sempre l'offerer: qui non si chiama MAI CreateOffer(). Si attende l'offer,
//    si fa SetRemoteDescription → CreateAnswer → SetLocalDescription e si rispedisce l'answer.
//    Lo stesso vale per le rinegoziazioni: quando entra l'Expert il server manda una nuova
//    offer (ora sendrecv) e noi rispondiamo di nuovo. Mai offerte da parte nostra → niente glare.
//  - JSON su WebSocket: {"type":"join"|"offer"|"answer"|"candidate"|"ping", ...}.
//  - I candidati ICE del server possono arrivare PRIMA dell'offer: vengono bufferizzati e
//    applicati dopo SetRemoteDescription.
//  - Nessuno STUN/TURN: il server ha IP pubblico fisso e lo dichiara nei propri candidati.
//  - DataChannel NEGOZIATO (negotiated=true, id=0, label="data"): entrambi i lati lo creano
//    localmente, non passa per l'SDP come canale "annunciato".
//  - H.264 preferito nell'answer: il Quest ha encoder H.264 hardware, VP8 è software.
//
// I track locali (video passthrough + microfono) li aggancia VideoManager tramite l'evento
// PeerConnectionCreated, PRIMA che arrivi la prima offer, così l'answer li include come sendonly.
public class XrmSessionClient : MonoBehaviour
{
    [Header("Server")]
    [Tooltip("Endpoint WebSocket del Session Server, senza query string. Es: wss://host/ws")]
    [SerializeField] string serverUrl = "wss://compile-sleep-achieve-stages.trycloudflare.com/ws";
    [Tooltip("ID di sessione: Quest ed Expert devono usare lo stesso.")]
    [SerializeField] string sessionId = "default";
    [SerializeField] bool autoConnect = true;
    [Tooltip("Secondi di attesa prima di ritentare dopo la caduta del WebSocket.")]
    [SerializeField] float reconnectDelay = 3f;
    [SerializeField] bool verboseLogs = true;

    [Header("Media remoto (Expert → Quest)")]
    [Tooltip("RawImage su cui mostrare il video dell'Expert. Viene attivata al primo frame.")]
    [SerializeField] RawImage remoteVideoImage;
    [Tooltip("AudioSource che riproduce l'audio dell'Expert. Se vuoto ne viene creata una su questo GameObject.")]
    [SerializeField] AudioSource remoteAudioSource;

    [Header("Eventi")]
    public UnityEvent Connected;
    public UnityEvent Disconnected;
    public UnityEvent RemoteVideoReceived;
    public UnityEvent RemoteAudioReceived;
    public UnityEvent<string> DataMessageReceived;

    // Eventi C# per chi deve agganciare/staccare i track locali (VideoManager).
    // PeerConnectionCreated scatta appena esiste la RTCPeerConnection, prima di qualunque offer.
    public event Action<RTCPeerConnection> PeerConnectionCreated;
    public event Action<RTCPeerConnection> PeerConnectionClosing;

    public RTCPeerConnection PeerConnection => _pc;
    public bool IsWebSocketConnected => _ws != null && _ws.State == WebSocketState.Open;
    public bool IsConnected => _pc != null && _pc.ConnectionState == RTCPeerConnectionState.Connected;
    public Texture RemoteVideoTexture { get; private set; }
    public string SessionId => sessionId;

    WebSocket _ws;
    RTCPeerConnection _pc;
    RTCDataChannel _dc;
    bool _wantConnection;                 // true tra Connect() e Disconnect(): abilita la riconnessione
    bool _remoteDescriptionSet;
    Coroutine _answerCoroutine;
    Coroutine _reconnectCoroutine;
    Coroutine _webRtcUpdateCoroutine;     // handle di WebRTC.Update(): va fermata via handle, non con StopCoroutine(WebRTC.Update())
    readonly Queue<string> _pendingOffers = new();
    readonly List<RTCIceCandidateInit> _pendingCandidates = new();
    readonly Queue<string> _outbox = new();   // messaggi WS in ordine FIFO (join → answer → candidate...)
    bool _sending;

    // Video remoto. Il server NON riusa le m-line: se l'Expert esce e rientra, i suoi nuovi track
    // arrivano su m-line NUOVE e quelli vecchi restano negoziati ma fermi sull'ultimo frame. Quindi
    // possono coesistere più track video remoti: si mostra quello i cui frame avanzano davvero.
    readonly List<VideoStreamTrack> _remoteVideoTracks = new();   // il più recente in fondo
    VideoStreamTrack _displayTrack;
    readonly Dictionary<string, uint> _lastFramesDecoded = new(); // per trackIdentifier, dal poll precedente
    Coroutine _statsCoroutine;
    int _statsLogCount;
    string _lastStallSignature;

    // Log su file: /sdcard/Android/data/<package>/files/xrm_debug.log, leggibile con adb pull.
    // Il logcat del Quest ruota in pochi secondi, quindi i log dell'app si perdono subito.
    System.IO.StreamWriter _logFile;
    readonly object _logLock = new();

    // ---------- Messaggi JSON (JsonUtility: i nomi dei campi devono coincidere con il protocollo) ----------
#pragma warning disable 0649   // i campi di InMsg li riempie JsonUtility
    [Serializable] class InMsg { public string type; public string sdp; public string candidate; public string sdp_mid; public int sdp_mline_index = -1; }
#pragma warning restore 0649
    [Serializable] class JoinMsg { public string type = "join"; public string session_id; }
    [Serializable] class SdpMsg { public string type; public string sdp; }
    [Serializable] class CandidateMsg { public string type = "candidate"; public string candidate; public string sdp_mid; public int sdp_mline_index; }

    // ---------- Lifecycle ----------

    void Awake()
    {
        try
        {
            string path = System.IO.Path.Combine(Application.persistentDataPath, "xrm_debug.log");
            _logFile = new System.IO.StreamWriter(path, false) { AutoFlush = true };
            _logFile.WriteLine($"=== XRM log {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {Application.identifier} {Application.version} ===");
        }
        catch (Exception ex) { Debug.LogWarning($"[XRM] Log su file non disponibile: {ex.Message}"); }
    }

    void OnDestroy()
    {
        lock (_logLock)
        {
            _logFile?.Dispose();
            _logFile = null;
        }
    }

    void OnEnable()
    {
        // Pompa il plugin nativo (encoder/decoder). Una sola istanza: più copie insieme = contesa → freeze.
        if (_webRtcUpdateCoroutine == null)
            _webRtcUpdateCoroutine = StartCoroutine(WebRTC.Update());
        if (_statsCoroutine == null)
            _statsCoroutine = StartCoroutine(StatsLoop());
    }

    // Connessione in Start (non in OnEnable) così tutti gli Awake della scena, VideoManager incluso,
    // hanno già sottoscritto PeerConnectionCreated quando la peer connection nasce.
    void Start()
    {
        if (autoConnect) Connect();
    }

    void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        _ws?.DispatchMessageQueue();   // NativeWebSocket consegna OnMessage sul main thread solo da qui
#endif
        FlushOutbox();
        UpdateRemoteVideoDisplay();
    }

    void OnDisable()
    {
        Disconnect();
        if (_webRtcUpdateCoroutine != null)
        {
            StopCoroutine(_webRtcUpdateCoroutine);
            _webRtcUpdateCoroutine = null;
        }
        if (_statsCoroutine != null)
        {
            StopCoroutine(_statsCoroutine);
            _statsCoroutine = null;
        }
    }

    // ---------- API pubblica ----------

    public void Connect()
    {
        _wantConnection = true;
        if (_ws != null) return;   // già connesso o in connessione
        CreatePeerConnection();
        OpenWebSocket();
    }

    public void Disconnect()
    {
        _wantConnection = false;
        if (_reconnectCoroutine != null) { StopCoroutine(_reconnectCoroutine); _reconnectCoroutine = null; }
        CloseWebSocket();
        TeardownPeerConnection();
    }

    public void SetServer(string url, string session)
    {
        serverUrl = url;
        sessionId = session;
    }

    // Invia sul DataChannel "data": il server lo inoltra a tutti gli altri peer della sessione.
    public bool SendData(string message)
    {
        if (_dc == null || _dc.ReadyState != RTCDataChannelState.Open) return false;
        _dc.Send(message);
        return true;
    }

    public bool SendData(byte[] bytes)
    {
        if (_dc == null || _dc.ReadyState != RTCDataChannelState.Open) return false;
        _dc.Send(bytes);
        return true;
    }

    // ---------- WebSocket ----------

    void OpenWebSocket()
    {
        string url = $"{serverUrl.TrimEnd('/')}?session={Uri.EscapeDataString(sessionId)}";
        Log($"WebSocket → {url}");

        var ws = new WebSocket(url);
        _ws = ws;

        ws.OnOpen += () =>
        {
            if (_ws != ws) return;
            Log("WebSocket aperto, invio join");
            Enqueue(JsonUtility.ToJson(new JoinMsg { session_id = sessionId }));
        };
        ws.OnMessage += bytes =>
        {
            if (_ws != ws) return;
            HandleMessage(Encoding.UTF8.GetString(bytes));
        };
        ws.OnError += e => LogError($"WebSocket errore: {e}");
        ws.OnClose += code =>
        {
            if (_ws != ws) return;
            Log($"WebSocket chiuso ({code})");
            _ws = null;
            Disconnected?.Invoke();
            if (_wantConnection) ScheduleReconnect();
        };

        ConnectWebSocketAsync(ws);
    }

    // ws.Connect() di NativeWebSocket resta in await per tutta la vita del socket (contiene il
    // receive loop): va lanciato e dimenticato, gli esiti arrivano dagli eventi.
    async void ConnectWebSocketAsync(WebSocket ws)
    {
        try { await ws.Connect(); }
        catch (Exception ex)
        {
            if (_ws == ws)
            {
                LogError($"WebSocket connect fallito: {ex.Message}");
                _ws = null;
                if (_wantConnection) ScheduleReconnect();
            }
        }
    }

    void CloseWebSocket()
    {
        var ws = _ws;
        _ws = null;
        lock (_outbox) _outbox.Clear();
        if (ws == null) return;
        try
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.Connecting)
                _ = ws.Close();
        }
        catch (Exception ex) { LogError($"WebSocket close: {ex.Message}"); }
    }

    void ScheduleReconnect()
    {
        if (_reconnectCoroutine != null) return;
        _reconnectCoroutine = StartCoroutine(ReconnectAfterDelay());
    }

    // Il server (o il tunnel Cloudflare) è caduto: la sessione lato server non esiste più, quindi si
    // riparte da zero con una peer connection nuova. VideoManager riceve Closing/Created e ricrea i track.
    IEnumerator ReconnectAfterDelay()
    {
        yield return new WaitForSeconds(reconnectDelay);
        _reconnectCoroutine = null;
        if (!_wantConnection || _ws != null) yield break;
        Log("Riconnessione...");
        TeardownPeerConnection();
        CreatePeerConnection();
        OpenWebSocket();
    }

    void Enqueue(string json)
    {
        lock (_outbox) _outbox.Enqueue(json);
    }

    void FlushOutbox()
    {
        if (_sending || !IsWebSocketConnected) return;
        lock (_outbox) { if (_outbox.Count == 0) return; }
        DrainOutbox();
    }

    // Un solo SendText alla volta, in ordine: join prima dell'answer, answer prima dei candidati.
    async void DrainOutbox()
    {
        _sending = true;
        try
        {
            while (true)
            {
                string msg;
                lock (_outbox)
                {
                    if (_outbox.Count == 0) break;
                    msg = _outbox.Dequeue();
                }
                var ws = _ws;
                if (ws == null || ws.State != WebSocketState.Open) break;
                await ws.SendText(msg);
            }
        }
        catch (Exception ex) { LogError($"WebSocket send: {ex.Message}"); }
        finally { _sending = false; }
    }

    // ---------- Signaling ----------

    void HandleMessage(string json)
    {
        InMsg msg;
        try { msg = JsonUtility.FromJson<InMsg>(json); }
        catch (Exception ex)
        {
            LogError($"Messaggio non JSON: {ex.Message} — {Truncate(json)}");
            return;
        }
        if (msg == null || string.IsNullOrEmpty(msg.type)) return;

        switch (msg.type)
        {
            case "ping":
                break;   // keepalive del server, non si risponde
            case "offer":
                Log("OFFER ricevuta dal server");
                _pendingOffers.Enqueue(msg.sdp);
                TryAnswerNextOffer();
                break;
            case "candidate":
                HandleCandidate(msg);
                break;
            case "answer":
                LogWarning("Ricevuta una ANSWER ma il Quest non manda offer: ignorata.");
                break;
            default:
                Log($"Messaggio ignorato: {Truncate(json)}");
                break;
        }
    }

    void HandleCandidate(InMsg msg)
    {
        if (string.IsNullOrEmpty(msg.candidate)) return;   // fine gathering
        var init = new RTCIceCandidateInit
        {
            candidate = msg.candidate,
            sdpMid = msg.sdp_mid,
            sdpMLineIndex = msg.sdp_mline_index >= 0 ? (int?)msg.sdp_mline_index : null
        };
        if (!_remoteDescriptionSet || _pc == null)
        {
            _pendingCandidates.Add(init);   // arrivato prima dell'offer: lo applichiamo dopo SetRemoteDescription
            return;
        }
        AddCandidate(init);
    }

    void AddCandidate(RTCIceCandidateInit init)
    {
        try { _pc.AddIceCandidate(new RTCIceCandidate(init)); }
        catch (Exception ex) { LogWarning($"Candidate scartato: {ex.Message}"); }
    }

    // Le offer si processano una alla volta: una rinegoziazione che arriva mentre stiamo ancora
    // rispondendo alla precedente aspetta in coda, altrimenti SetRemoteDescription in stato
    // have-remote-offer fallisce.
    void TryAnswerNextOffer()
    {
        if (_answerCoroutine != null || _pendingOffers.Count == 0 || _pc == null) return;
        _answerCoroutine = StartCoroutine(AnswerOffer(_pendingOffers.Dequeue()));
    }

    IEnumerator AnswerOffer(string offerSdp)
    {
        var pc = _pc;
        try
        {
            var offer = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = offerSdp };
            var setRemote = pc.SetRemoteDescription(ref offer);
            yield return setRemote;
            if (setRemote.IsError)
            {
                LogError($"SetRemoteDescription fallita: {setRemote.Error.message}");
                yield break;
            }
            _remoteDescriptionSet = true;

            foreach (var c in _pendingCandidates) AddCandidate(c);
            _pendingCandidates.Clear();

            // Prima di CreateAnswer: l'ordine dei codec nell'answer segue queste preferenze,
            // filtrate su quelli presenti nell'offer del server.
            PreferH264(pc);

            var answer = pc.CreateAnswer();
            yield return answer;
            if (answer.IsError)
            {
                LogError($"CreateAnswer fallita: {answer.Error.message}");
                yield break;
            }

            var answerDesc = answer.Desc;
            var setLocal = pc.SetLocalDescription(ref answerDesc);
            yield return setLocal;
            if (setLocal.IsError)
            {
                LogError($"SetLocalDescription fallita: {setLocal.Error.message}");
                yield break;
            }

            Enqueue(JsonUtility.ToJson(new SdpMsg { type = "answer", sdp = answerDesc.sdp }));
            Log($"ANSWER inviata. Video negoziato: {DescribeVideoCodec(answerDesc.sdp)}");
        }
        finally
        {
            _answerCoroutine = null;
        }
        TryAnswerNextOffer();
    }

    // ---------- PeerConnection ----------

    void CreatePeerConnection()
    {
        if (_pc != null) return;

        // Nessun ICE server: con l'SFU il Quest deve solo USCIRE verso un IP pubblico fisso,
        // non farsi raggiungere. Il NAT lo gestisce da solo come per ogni connessione in uscita.
        var config = new RTCConfiguration { iceServers = new RTCIceServer[0] };
        _pc = new RTCPeerConnection(ref config);
        _remoteDescriptionSet = false;

        _pc.OnIceCandidate = candidate =>
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.Candidate)) return;
            Enqueue(JsonUtility.ToJson(new CandidateMsg
            {
                candidate = candidate.Candidate,
                sdp_mid = candidate.SdpMid,
                sdp_mline_index = candidate.SdpMLineIndex ?? 0
            }));
        };
        _pc.OnIceConnectionChange = state => Log($"ICE: {state}");
        _pc.OnConnectionStateChange = state =>
        {
            Log($"PeerConnection: {state}");
            if (state == RTCPeerConnectionState.Connected) Connected?.Invoke();
            // Peer connection morta ma WebSocket vivo: la sessione lato server va comunque rifatta.
            // Chiudere il WS porta nel percorso di riconnessione (OnClose → ScheduleReconnect).
            if (state == RTCPeerConnectionState.Failed && _wantConnection) CloseWebSocket();
        };
        // Il server è l'offerer: non si reagisce a OnNegotiationNeeded.
        _pc.OnNegotiationNeeded = null;
        _pc.OnTrack = OnTrack;

        // DataChannel negoziato: stesso id=0 e label "data" su entrambi i lati, nessun annuncio in SDP.
        _dc = _pc.CreateDataChannel("data", new RTCDataChannelInit { negotiated = true, id = 0 });
        _dc.OnOpen = () => Log("DataChannel aperto");
        _dc.OnClose = () => Log("DataChannel chiuso");
        _dc.OnMessage = bytes =>
        {
            var text = Encoding.UTF8.GetString(bytes);
            DataMessageReceived?.Invoke(text);
        };

        LogVideoCapabilities();
        PeerConnectionCreated?.Invoke(_pc);
    }

    void TeardownPeerConnection()
    {
        if (_answerCoroutine != null) { StopCoroutine(_answerCoroutine); _answerCoroutine = null; }
        _pendingOffers.Clear();
        _pendingCandidates.Clear();
        _remoteDescriptionSet = false;

        if (_pc == null) return;
        PeerConnectionClosing?.Invoke(_pc);   // VideoManager stacca e libera i track locali

        if (_dc != null)
        {
            _dc.Close();
            _dc.Dispose();
            _dc = null;
        }
        _pc.Close();
        _pc.Dispose();
        _pc = null;

        _remoteVideoTracks.Clear();
        _displayTrack = null;
        _lastFramesDecoded.Clear();
        _lastStallSignature = null;
        _statsLogCount = 0;

        RemoteVideoTexture = null;
        if (remoteVideoImage != null) remoteVideoImage.texture = null;
        if (remoteAudioSource != null) remoteAudioSource.Stop();
    }

    void OnTrack(RTCTrackEvent e)
    {
        if (e.Track is VideoStreamTrack video)
        {
            string mid = e.Transceiver?.Mid;
            Log($"Track video remoto ricevuto (mid={mid}, id={video.Id}, totale={_remoteVideoTracks.Count + 1})");
            if (!_remoteVideoTracks.Contains(video)) _remoteVideoTracks.Add(video);
            _displayTrack = video;   // l'ultimo arrivato è il candidato; StatsLoop corregge se non è quello vivo
            // OnVideoReceived scatta al primo frame E a ogni cambio di risoluzione (la texture viene
            // distrutta e ricreata). La RawImage non si aggiorna da qui ma in UpdateRemoteVideoDisplay,
            // che ogni frame legge la texture corrente del track: così un evento perso non la congela.
            video.OnVideoReceived += tex =>
            {
                Log($"Video remoto mid={mid}: texture {tex.width}x{tex.height}");
                RemoteVideoReceived?.Invoke();
            };
        }
        else if (e.Track is AudioStreamTrack audio)
        {
            Log($"Track audio remoto ricevuto (mid={e.Transceiver?.Mid})");
            if (remoteAudioSource == null) remoteAudioSource = gameObject.AddComponent<AudioSource>();
            remoteAudioSource.SetTrack(audio);
            remoteAudioSource.loop = true;
            remoteAudioSource.Play();
            RemoteAudioReceived?.Invoke();
        }
    }

    // Aggancia alla RawImage la texture CORRENTE del track mostrato. Chiamata ogni frame: la texture
    // di un track cambia a ogni cambio di risoluzione del mittente, e il track mostrato può cambiare.
    void UpdateRemoteVideoDisplay()
    {
        if (_displayTrack == null) return;
        Texture tex = _displayTrack.Texture;
        if (tex == null || ReferenceEquals(tex, RemoteVideoTexture)) return;

        RemoteVideoTexture = tex;
        if (remoteVideoImage != null)
        {
            remoteVideoImage.texture = tex;
            if (!remoteVideoImage.gameObject.activeSelf) remoteVideoImage.gameObject.SetActive(true);
        }
    }

    // ---------- Diagnostica ----------

    // Ogni 2 s legge le statistiche della peer connection. Serve a due cose:
    //  1) scegliere quale track video remoto mostrare (quello i cui frame decodificati avanzano);
    //  2) scrivere nel log se il flusso in ingresso arriva, viene decodificato e con quale codec.
    //     "dec" fermo con "pkts" che sale = problema di decodifica; "dec" che sale con immagine
    //     ferma = problema di rendering; "pkts" fermo = il server non sta inoltrando.
    IEnumerator StatsLoop()
    {
        var wait = new WaitForSeconds(2f);
        while (true)
        {
            yield return wait;
            var pc = _pc;
            if (pc == null || pc.ConnectionState != RTCPeerConnectionState.Connected) continue;

            var op = pc.GetStats();
            yield return op;
            if (op.IsError || op.Value == null) continue;
            using (var report = op.Value)
            {
                if (pc == _pc) HandleStats(report);
            }
        }
    }

    void HandleStats(RTCStatsReport report)
    {
        var sb = new StringBuilder();
        var stall = new StringBuilder();
        VideoStreamTrack liveTrack = null;
        bool displayAdvancing = false;

        foreach (var stat in report.Stats.Values)
        {
            if (stat is RTCInboundRTPStreamStats inb && inb.kind == "video")
            {
                string key = inb.trackIdentifier ?? inb.ssrc.ToString();
                _lastFramesDecoded.TryGetValue(key, out uint prev);
                bool advancing = inb.framesDecoded > prev;
                _lastFramesDecoded[key] = inb.framesDecoded;

                var track = _remoteVideoTracks.FirstOrDefault(t => t.Id == inb.trackIdentifier);
                if (advancing && track != null) liveTrack = track;
                if (advancing && track != null && track == _displayTrack) displayAdvancing = true;

                sb.Append($" IN[mid={inb.mid} {CodecName(report, inb.codecId)} {inb.frameWidth}x{inb.frameHeight}" +
                          $" pkts={inb.packetsReceived} lost={inb.packetsLost} recv={inb.framesReceived}" +
                          $" dec={inb.framesDecoded} key={inb.keyFramesDecoded} drop={inb.framesDropped}" +
                          $" pli={inb.pliCount} nack={inb.nackCount} impl={inb.decoderImplementation}" +
                          $"{(advancing ? "" : " FERMO")}{(track == _displayTrack ? " *mostrato" : "")}]");
                stall.Append(inb.mid).Append(advancing ? '+' : '-');
            }
            else if (stat is RTCOutboundRTPStreamStats outb && outb.kind == "video")
            {
                sb.Append($" OUT[{CodecName(report, outb.codecId)} {outb.frameWidth}x{outb.frameHeight}" +
                          $" enc={outb.framesEncoded} impl={outb.encoderImplementation} limit={outb.qualityLimitationReason}]");
            }
        }

        // Il track mostrato è fermo ma un altro avanza (tipico: l'Expert ha ricaricato la pagina e i
        // suoi nuovi track sono su m-line nuove): si passa a quello vivo.
        if (liveTrack != null && !displayAdvancing && liveTrack != _displayTrack)
        {
            Log("Track video mostrato fermo: passo a quello che sta ricevendo frame");
            _displayTrack = liveTrack;
        }

        var tex = _displayTrack != null ? _displayTrack.Texture : null;
        sb.Append(tex != null ? $" TEX[{tex.width}x{tex.height}]" : " TEX[nessuna]");

        // Fitto nei primi 30 s (la fase in cui il problema si manifesta), poi ogni 10 s, e subito
        // a ogni cambio di stato fermo/in movimento.
        string signature = stall.ToString();
        bool changed = signature != _lastStallSignature;
        _lastStallSignature = signature;
        _statsLogCount++;
        if (changed || _statsLogCount <= 15 || _statsLogCount % 5 == 0)
            Log("STATS" + sb);
    }

    static string CodecName(RTCStatsReport report, string codecId)
    {
        if (!string.IsNullOrEmpty(codecId) && report.TryGetValue(codecId, out var s) && s is RTCCodecStats codec)
            return codec.mimeType?.Replace("video/", "") ?? "?";
        return "?";
    }

    // ---------- Codec ----------

    // Mette H.264 in testa alle preferenze di ogni transceiver video che invia. Lo Snapdragon XR2
    // ha encoder hardware solo H.264/HEVC: VP8 significa encode software e thermal throttling.
    // Ordine: H.264 packetization-mode=1 (il Quest usa mode 1) con profilo baseline/constrained
    // baseline (42e01f/42001f, i più compatibili con l'offer del server), poi gli altri H.264,
    // poi rtx (ritrasmissioni), poi tutto il resto come fallback se H.264 non fosse disponibile.
    public static void PreferH264(RTCPeerConnection pc)
    {
        var caps = RTCRtpSender.GetCapabilities(TrackKind.Video)?.codecs;
        if (caps == null || caps.Length == 0) return;

        static int Rank(RTCRtpCodecCapability c)
        {
            string fmtp = c.sdpFmtpLine ?? "";
            switch (c.mimeType)
            {
                case "video/H264":
                {
                    bool mode1 = fmtp.Contains("packetization-mode=1");
                    bool baseline = fmtp.Contains("profile-level-id=42e0") || fmtp.Contains("profile-level-id=4200");
                    return (mode1 ? 0 : 2) + (baseline ? 0 : 1);   // 0..3
                }
                case "video/rtx": return 4;
                case "video/red":
                case "video/ulpfec": return 5;
                case "video/VP8": return 6;
                default: return 7;
            }
        }

        var ordered = caps.OrderBy(Rank).ToArray();   // OrderBy è stabile: a parità di rank resta l'ordine nativo
        foreach (var t in pc.GetTransceivers())
        {
            if (t.Sender?.Track?.Kind != TrackKind.Video) continue;
            var err = t.SetCodecPreferences(ordered);
            if (err != RTCErrorType.None) Debug.LogWarning($"[XRM] SetCodecPreferences: {err}");
        }
    }

    void LogVideoCapabilities()
    {
        if (!verboseLogs) return;
        var caps = RTCRtpSender.GetCapabilities(TrackKind.Video)?.codecs;
        if (caps == null) { LogWarning("Nessuna capability video dall'encoder"); return; }
        var list = caps.Where(c => c.mimeType != "video/rtx" && c.mimeType != "video/red" && c.mimeType != "video/ulpfec")
                       .Select(c => $"{c.mimeType.Replace("video/", "")}[{c.sdpFmtpLine}]");
        Log("Encoder video disponibili: " + string.Join(" | ", list));
    }

    // Estrae dall'SDP il primo codec della m=video: è quello che l'encoder userà.
    static string DescribeVideoCodec(string sdp)
    {
        var lines = sdp.Split('\n');
        int mVideo = Array.FindIndex(lines, l => l.StartsWith("m=video"));
        if (mVideo < 0) return "nessuna m=video";
        var parts = lines[mVideo].Trim().Split(' ');
        if (parts.Length < 4) return lines[mVideo].Trim();
        string pt = parts[3];
        string rtpmap = lines.Skip(mVideo).FirstOrDefault(l => l.StartsWith($"a=rtpmap:{pt} "))?.Trim();
        string fmtp = lines.Skip(mVideo).FirstOrDefault(l => l.StartsWith($"a=fmtp:{pt} "))?.Trim();
        return $"{rtpmap ?? ("pt " + pt)} {fmtp ?? ""}";
    }

    // ---------- Log ----------
    // Prefisso [XRM]: DebugLogOverlay filtra per parola chiave, "xrm" è tra quelle ammesse.

    static string Truncate(string s) => s.Length > 200 ? s.Substring(0, 200) + "..." : s;
    void Log(string msg) { ToFile("I", msg); if (verboseLogs) Debug.Log($"[XRM] {msg}"); }
    void LogWarning(string msg) { ToFile("W", msg); Debug.LogWarning($"[XRM] {msg}"); }
    void LogError(string msg) { ToFile("E", msg); Debug.LogError($"[XRM] {msg}"); }

    void ToFile(string level, string msg)
    {
        lock (_logLock)
        {
            if (_logFile == null) return;
            try { _logFile.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} {msg}"); }
            catch (Exception) { /* disco pieno o file chiuso: il log su file non deve mai rompere lo streaming */ }
        }
    }
}
