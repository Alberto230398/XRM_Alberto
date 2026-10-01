using System.Linq;
using Unity.WebRTC;
using UnityEngine;

// Produce i track locali (video passthrough/composito + microfono) e li aggancia alla peer
// connection verso il Session Server XRM (vedi XrmSessionClient).
//
// Con l'SFU c'è UNA sola peer connection e il server è l'offerer: i track vanno aggiunti PRIMA
// che arrivi la prima offer, così l'answer li negozia come sendonly. XrmSessionClient invoca
// PeerConnectionCreated appena crea la connessione (prima di aprire il WebSocket) e
// PeerConnectionClosing prima di chiuderla: qui si creano/distruggono i track in quei momenti.
// Il track vive per una SESSIONE: a ogni riconnessione se ne crea uno fresco (riusare lo stesso
// encoder tra sessioni lasciava ~2s di latenza dopo un restart del server).
public class VideoManager : MonoBehaviour
{
    [SerializeField] MonoBehaviour[] videoSources;   // MonoBehaviour che implementano VideoInterface
    [SerializeField] AudioSource audioSource;
    [SerializeField] int activeSourceIndex = 0;
    [Tooltip("Se vuoto viene cercato in scena.")]
    [SerializeField] XrmSessionClient client;

    VideoInterface[] sources => videoSources.Select(s => s as VideoInterface).ToArray();

    RenderTexture camRenderTexture;      // RT su cui disegna la sorgente attiva: è il feed del track
    VideoInterface currentSource;
    VideoStreamTrack _videoStreamTrack;
    AudioStreamTrack _audioStreamTrack;
    MediaStream _mediaStream;
    RTCRtpSender _videoSender;
    float _nextCap;                      // tempo del prossimo rinforzo del tetto bitrate

    // TETTO del bitrate Quest→server. Solo un MASSIMO: NIENTE minBitrate.
    // Un minBitrate è un PAVIMENTO che impedisce alla congestion control (GCC) di scendere quando il
    // link non regge: l'encoder continua a spingere più di quanto il Wi-Fi trasporta, l'eccesso si
    // accumula nelle code (driver Wi-Fi/router) e la latenza CRESCE all'infinito → bufferbloat.
    // In bidirezionale è peggio: il Wi-Fi è half-duplex, le due direzioni si dividono lo stesso
    // tempo radio, quindi la capacità reale per direzione si dimezza e il pavimento sfonda prima.
    const ulong MaxBps = 1_500_000u;   // 1.5 Mbps
    const uint MaxFps = 30u;           // con H264 hardware il framerate non è più il collo di bottiglia

    void Awake()
    {
        if (client == null)
            client = GetComponentInParent<XrmSessionClient>() ?? FindAnyObjectByType<XrmSessionClient>();
        if (client == null)
        {
            Debug.LogError("[VideoManager] Nessun XrmSessionClient in scena: niente streaming.");
            return;
        }
        client.PeerConnectionCreated += OnPeerConnectionCreated;
        client.PeerConnectionClosing += OnPeerConnectionClosing;
        // Se la connessione esiste già (ordine Awake non garantito) agganciamo subito.
        if (client.PeerConnection != null) OnPeerConnectionCreated(client.PeerConnection);
    }

    void Update()
    {
        // Rinforza il TETTO ogni secondo (una rinegoziazione può resettarlo). Qui si rimette solo
        // il massimo: se il bitrate effettivo è sceso è la congestion control che sta lavorando,
        // NON va "riportato su", altrimenti si accumula ritardo in rete.
        if (_videoSender != null && Time.time >= _nextCap)
        {
            _nextCap = Time.time + 1f;
            ApplyCap(_videoSender);
        }
    }

    void OnPeerConnectionCreated(RTCPeerConnection pc)
    {
        if (_videoStreamTrack != null) return;   // già agganciati a questa connessione

        _mediaStream = new MediaStream();
        _videoStreamTrack = new VideoStreamTrack(CreateVideo());
        _videoSender = pc.AddTrack(_videoStreamTrack, _mediaStream);

        var mic = CreateAudio();
        if (mic != null)
        {
            _audioStreamTrack = new AudioStreamTrack(mic);
            pc.AddTrack(_audioStreamTrack, _mediaStream);
        }

        ApplyCap(_videoSender);
        Debug.Log("[VideoManager] Track video/audio agganciati alla peer connection");
    }

    void OnPeerConnectionClosing(RTCPeerConnection pc)
    {
        ReleaseTracks();
    }

    // Butta i track (e l'encoder): la connessione successiva ne crea di freschi.
    // La RenderTexture e la sorgente video restano: vengono riusate dalla sessione successiva.
    void ReleaseTracks()
    {
        _videoSender = null;

        _videoStreamTrack?.Dispose();
        _videoStreamTrack = null;

        _audioStreamTrack?.Dispose();
        _audioStreamTrack = null;

        _mediaStream?.Dispose();
        _mediaStream = null;

        StopAudio();
    }

    // (Ri)crea la RT-sorgente (una volta) e (ri)avvia la sorgente attiva che la disegna.
    public RenderTexture CreateVideo()
    {
        // RT riusata tra le connessioni: riallocarla senza Release() perdeva ~6.5MB di VRAM a giro.
        // 640²: scelto quando l'encoder era VP8 software. Con H.264 hardware (vedi
        // XrmSessionClient.PreferH264) si può alzare, ma l'SFU inoltra l'RTP così com'è e il
        // browser dell'Expert riceve esattamente questa risoluzione: cambiarla qui cambia anche lì.
        if (camRenderTexture == null)
        {
            camRenderTexture = new RenderTexture(640, 640, 0, RenderTextureFormat.BGRA32);
            camRenderTexture.Create();
        }
        SwitchSource(activeSourceIndex);
        return camRenderTexture;
    }

    // Avvia il microfono e restituisce l'AudioSource che lo riproduce. Unity.WebRTC cattura l'audio
    // via OnAudioFilterRead sull'AudioSource, quindi DEVE essere in Play() e in loop: senza, il filtro
    // non gira e il track è muto. Per NON sentirsi in locale, instrada l'Output dell'AudioSource su un
    // AudioMixerGroup silenziato (−80 dB): NON usare volume/mute, azzererebbero anche il segnale catturato.
    AudioSource CreateAudio()
    {
        if (audioSource == null)
        {
            Debug.LogWarning("[VideoManager] Nessuna AudioSource assegnata: stream senza audio.");
            return null;
        }
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("[VideoManager] Nessun microfono disponibile (permesso RECORD_AUDIO non concesso?).");
            return null;
        }

        string device = Microphone.devices[0];
        if (Microphone.IsRecording(device)) Microphone.End(device);

        // loop=true: il buffer viene riscritto in cerchio, così il mic registra all'infinito.
        audioSource.clip = Microphone.Start(device, true, 1, AudioSettings.outputSampleRate);
        audioSource.loop = true;   // l'AudioSource rilegge in loop il clip che il mic aggiorna
        audioSource.Play();        // ← senza questo il track è muto: fa girare OnAudioFilterRead
        return audioSource;
    }

    void StopAudio()
    {
        if (audioSource != null) audioSource.Stop();
        if (Microphone.devices.Length > 0 && Microphone.IsRecording(Microphone.devices[0]))
            Microphone.End(Microphone.devices[0]);
    }

    void SwitchSource(int index)
    {
        currentSource?.stop();
        activeSourceIndex = index;
        currentSource = sources[index];
        currentSource.initVideo(camRenderTexture);
    }

    static void ApplyCap(RTCRtpSender sender)
    {
        var param = sender.GetParameters();
        if (param.encodings == null) return;
        foreach (var enc in param.encodings)
        {
            enc.maxBitrate = MaxBps;
            enc.maxFramerate = MaxFps;
            // NIENTE enc.minBitrate: se la congestion control abbassa il bitrate NON è un guasto
            // da "correggere", è il sistema che si adatta al link.
        }
        sender.SetParameters(param);
    }

    void OnDestroy()
    {
        if (client != null)
        {
            client.PeerConnectionCreated -= OnPeerConnectionCreated;
            client.PeerConnectionClosing -= OnPeerConnectionClosing;
        }

        ReleaseTracks();   // libera i track prima di distruggere la RT

        currentSource?.stop();
        if (camRenderTexture != null)
        {
            camRenderTexture.Release();
            Destroy(camRenderTexture);
            camRenderTexture = null;
        }
    }
}
