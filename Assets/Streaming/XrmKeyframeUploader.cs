using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

// Invia uno zip al bucket S3 del Session Server XRM e avvisa gli altri peer sul DataChannel.
//
// I file NON passano da WebRTC. Il flusso, ricavato dalla pagina di test del server, è:
//  1) POST {host}/api/presign?session=ID  → {"upload_url", "fields": {...}, "prefix"}
//     È un "presigned POST" di S3: vale 1 ora e accetta qualunque chiave che inizi con "prefix"
//     (sessions/ID/kf/). Nessun limite di dimensione nella policy.
//  2) POST multipart su upload_url: tutti i "fields", poi "key" = prefix + NNNNNN.zip, e per
//     ULTIMO il campo "file" (S3 ignora tutto ciò che segue il file). Risposta attesa: 204.
//  3) Messaggio JSON sul DataChannel: {"type":"keyframe-uploaded","session_id","seq","s3_key"}.
//     Il server lo inoltra agli altri peer della sessione.
//
// Sorgente dei keyframe: KeyFrameManager. Ogni keyframe che cattura (RGB L/R, depth raw/allineata/
// sobel, pose, intrinseci, reprojection...) arriva qui con KeyframeEncoded e diventa uno zip con gli
// stessi file della cartella keyframes/N + metadata.json. Gli upload sono in CODA, uno alla volta:
// KeyFrameManager può catturare più in fretta di quanto il Wi-Fi riesca a caricare.
// Memoria: in RAM resta solo il keyframe in coda (limite maxQueuedMB). Quando tocca a lui, lo zip e
// poi il corpo multipart vengono scritti su file temporanei e caricati con UploadHandlerFile:
// nessuna copia da decine di MB sull'heap gestito (il GC di Unity non compatta e si frammenta).
// Resta il vecchio percorso di debug (CaptureAndUpload: frame.jpg dal frame streammato) e
// UploadZip(byte[]) per chi ha già uno zip pronto.
public class XrmKeyframeUploader : MonoBehaviour
{
    [Tooltip("Se vuoto viene cercato in scena.")]
    [SerializeField] XrmSessionClient client;
    [Tooltip("Sorgente dei keyframe. Se vuoto viene cercato in scena; se non c'è, solo cattura manuale.")]
    [SerializeField] KeyFrameManager keyFrameManager;
    [Tooltip("MB di keyframe in attesa (in RAM) oltre i quali i nuovi vengono scartati. Il primo in coda passa sempre.")]
    [SerializeField] int maxQueuedMB = 200;
    [Tooltip("Carica solo col DataChannel aperto, così gli altri peer ricevono la notifica keyframe-uploaded.")]
    [SerializeField] bool waitForDataChannel = true;
    [Tooltip("Sorgente del frame da catturare. Se vuota si usa il frame streammato da VideoManager.")]
    [SerializeField] Texture sourceTexture;
    [SerializeField] VideoManager videoManager;
    [Range(1, 100)]
    [SerializeField] int jpegQuality = 85;
    // Grilletto indice destro: A/B e X/Y sono già usati da Movement nella scena Streaming.
    [Tooltip("Tasto del controller che cattura e invia il frame streammato (debug). None = solo da codice/ContextMenu.")]
    [SerializeField] OVRInput.RawButton captureButton = OVRInput.RawButton.None;

    public UnityEvent<string> Uploaded;   // argomento: chiave S3 del file caricato
    public UnityEvent<string> Failed;     // argomento: descrizione dell'errore

    public bool IsBusy { get; private set; }
    public int NextSeq => _seq;
    public int QueuedCount => _queue.Count;
    public float QueuedMB => _queuedBytes / (1024f * 1024f);

    // Lo zip si scrive quando tocca a lui (sessione e seq noti), su un thread di background.
    class Pending
    {
        public Action<string, int, Stream> writeZip;   // (session_id, seq, destinazione)
        public long bytes;                             // RAM trattenuta finché è in coda
    }
    readonly Queue<Pending> _queue = new();
    long _queuedBytes;
    Coroutine _worker;
    string _tempDir;

    // Presign in cache: {upload_url, fields, prefix}. Scade dopo 1 ora lato server; lo rinnoviamo
    // prima (50 min), a ogni cambio di sessione, e comunque dopo un 403 di S3.
    string _uploadUrl;
    string _prefix;
    readonly List<KeyValuePair<string, string>> _fields = new();
    string _presignSession;
    float _presignTime = float.NegativeInfinity;
    const float PresignMaxAge = 50 * 60f;

    int _seq;   // numero progressivo del keyframe nella sessione corrente
    string _seqSession;

    [Serializable]
    class Metadata
    {
        public string session_id;
        public int seq;
        public long timestamp;   // Unix ms, come la pagina di test
        public int width;
        public int height;
    }

    [Serializable]
    class KeyframeMetadata
    {
        public string session_id;
        public int seq;
        public int keyframe_index;   // N di KeyFrameManager (= cartella keyframes/N sul Quest)
        public long timestamp;       // Unix ms della cattura
        public string[] files;
    }

    [Serializable]
    class UploadedMsg
    {
        public string type = "keyframe-uploaded";
        public string session_id;
        public int seq;
        public string s3_key;
    }

    void Awake()
    {
        if (client == null) client = GetComponentInParent<XrmSessionClient>() ?? FindAnyObjectByType<XrmSessionClient>();
        if (videoManager == null) videoManager = FindAnyObjectByType<VideoManager>();
        if (keyFrameManager == null) keyFrameManager = FindAnyObjectByType<KeyFrameManager>();
        if (client == null) Debug.LogError("[XRM] XrmKeyframeUploader: nessun XrmSessionClient in scena.");

        // Avanzi di upload interrotti (app chiusa o componente disattivato a metà).
        _tempDir = Path.Combine(Application.temporaryCachePath, "xrm_keyframes");
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
            Directory.CreateDirectory(_tempDir);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[XRM] Keyframe: pulizia {_tempDir} fallita: {ex.Message}");
        }
    }

    void OnEnable()
    {
        if (keyFrameManager != null) keyFrameManager.KeyframeEncoded += OnKeyframeEncoded;
        if (_queue.Count > 0 && _worker == null) _worker = StartCoroutine(Worker());
    }

    void OnDisable()
    {
        if (keyFrameManager != null) keyFrameManager.KeyframeEncoded -= OnKeyframeEncoded;
        _worker = null;   // le coroutine si fermano col componente: la riavvia OnEnable (o Enqueue)
        IsBusy = false;
    }

    void Update()
    {
        if (captureButton != OVRInput.RawButton.None && OVRInput.GetDown(captureButton))
            CaptureAndUpload();
    }

    // ---------- API pubblica ----------

    // Cattura il frame corrente, lo zippa con i metadata e lo carica.
    [ContextMenu("Capture And Upload")]
    public void CaptureAndUpload()
    {
        if (!CanStart(0)) return;

        Texture src = sourceTexture != null ? sourceTexture : (videoManager != null ? videoManager.CurrentFrame : null);
        if (src == null)
        {
            Fail("nessun frame da catturare (lo streaming video non è ancora partito?)");
            return;
        }

        byte[] jpg;
        int w, h;
        try
        {
            jpg = EncodeJpg(src, out w, out h);   // GPU readback: va fatto ora, sul main thread
        }
        catch (Exception ex)
        {
            Fail($"cattura fallita: {ex.Message}");
            return;
        }

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Enqueue(jpg.Length, (session, seq, dst) =>
        {
            var meta = new Metadata { session_id = session, seq = seq, timestamp = timestamp, width = w, height = h };
            WriteZip(dst, new Dictionary<string, byte[]>
            {
                { "frame.jpg", jpg },
                { "metadata.json", Encoding.UTF8.GetBytes(JsonUtility.ToJson(meta)) }
            });
        });
    }

    // Keyframe completo da KeyFrameManager: stessi file della cartella keyframes/N + metadata.json.
    void OnKeyframeEncoded(int index, IReadOnlyDictionary<string, byte[]> files)
    {
        long bytes = 0;
        foreach (var f in files) bytes += f.Value.Length;
        if (!CanStart(bytes)) return;

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Enqueue(bytes, (session, seq, dst) =>
        {
            var all = new Dictionary<string, byte[]>(files.Count + 1);
            foreach (var f in files) all[f.Key] = f.Value;
            var meta = new KeyframeMetadata { session_id = session, seq = seq, keyframe_index = index, timestamp = timestamp, files = new List<string>(all.Keys).ToArray() };
            all["metadata.json"] = Encoding.UTF8.GetBytes(JsonUtility.ToJson(meta));
            WriteZip(dst, all);
        });
    }

    // Carica uno zip già pronto come prossimo keyframe della sessione.
    public void UploadZip(byte[] zipBytes)
    {
        if (zipBytes == null || zipBytes.Length == 0)
        {
            Fail("zip vuoto");
            return;
        }
        if (!CanStart(zipBytes.Length)) return;
        Enqueue(zipBytes.Length, (_, _, dst) => dst.Write(zipBytes, 0, zipBytes.Length));
    }

    // Utility per chi costruisce lo zip da sé: nome file → contenuto.
    public static byte[] BuildZip(IDictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        WriteZip(ms, files);
        return ms.ToArray();
    }

    // Scrive lo zip direttamente su dst (file temporaneo o memoria).
    static void WriteZip(Stream dst, IDictionary<string, byte[]> files)
    {
        // leaveOpen: la directory centrale dello zip viene scritta alla Dispose dell'archivio,
        // quindi dst va chiuso DOPO l'archivio (lo chiude il chiamante).
        using (var archive = new ZipArchive(dst, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var kv in files)
            {
                // JPEG/PNG/EXR sono già compressi: ricomprimerli costa CPU sul Quest e non guadagna nulla.
                bool alreadyCompressed = kv.Key.EndsWith(".jpg") || kv.Key.EndsWith(".png") || kv.Key.EndsWith(".exr");
                var entry = archive.CreateEntry(kv.Key, alreadyCompressed ? System.IO.Compression.CompressionLevel.NoCompression
                                                                         : System.IO.Compression.CompressionLevel.Fastest);
                using var es = entry.Open();
                es.Write(kv.Value, 0, kv.Value.Length);
            }
        }
    }

    // ---------- Implementazione ----------

    bool CanStart(long bytes)
    {
        if (client == null) { Fail("XrmSessionClient mancante"); return false; }
        if (_queue.Count > 0 && _queuedBytes + bytes > maxQueuedMB * 1024L * 1024L)
        {
            Fail($"coda piena ({_queue.Count} keyframe, {QueuedMB:F0} MB): keyframe scartato");
            return false;
        }
        return true;
    }

    void Enqueue(long bytes, Action<string, int, Stream> writeZip)
    {
        _queue.Enqueue(new Pending { writeZip = writeZip, bytes = bytes });
        _queuedBytes += bytes;
        if (_queue.Count > 1) Log($"Keyframe in coda: {_queue.Count} ({QueuedMB:F0} MB)");
        if (_worker == null && isActiveAndEnabled) _worker = StartCoroutine(Worker());
    }

    IEnumerator Worker()
    {
        while (_queue.Count > 0)
        {
            if (waitForDataChannel && !client.IsDataChannelOpen)
            {
                yield return null;
                continue;
            }

            string session = client.SessionId;
            if (session != _seqSession)   // sessione nuova: la numerazione riparte
            {
                _seq = 0;
                _seqSession = session;
            }

            IsBusy = true;
            var item = _queue.Peek();
            int seq = _seq;
            string zipPath = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".zip");
            // Zip di vari MB: fuori dal main thread per non far scattare il visore.
            var zipTask = Task.Run(() =>
            {
                using var fs = File.Create(zipPath);
                item.writeZip(session, seq, fs);
            });
            while (!zipTask.IsCompleted) yield return null;

            // Da qui il keyframe vive solo nel file: si toglie dalla coda (anche se lo zip è fallito)
            // e il GC può liberarne i byte.
            _queue.Dequeue();
            _queuedBytes -= item.bytes;
            string zipError = zipTask.IsFaulted ? zipTask.Exception?.GetBaseException().Message ?? "?" : null;
            // I locali di una coroutine sono campi dell'iteratore: senza azzerarli il keyframe
            // (catturato da item e dalla lambda del task) resterebbe in RAM per tutto l'upload.
            item = null;
            zipTask = null;

            if (zipError != null)
                Fail($"zip fallito: {zipError}");
            else
                yield return UploadRoutine(session, zipPath);   // ritenta già al suo interno

            TryDelete(zipPath);
            IsBusy = false;
        }
        _worker = null;
    }

    IEnumerator UploadRoutine(string session, string zipPath)
    {
        long zipLength = new FileInfo(zipPath).Length;

        // Due tentativi: se S3 rifiuta (presign scaduto o credenziali temporanee ruotate)
        // si richiede un presign nuovo e si riprova una volta.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool needPresign = attempt > 0 || session != _presignSession ||
                               Time.realtimeSinceStartup - _presignTime > PresignMaxAge;
            if (needPresign)
            {
                string presignError = null;
                yield return RequestPresign(session, e => presignError = e);
                if (presignError != null)
                {
                    Fail($"presign fallito: {presignError}");
                    yield break;
                }
            }

            int seq = _seq;
            string fileName = seq.ToString("D6") + ".zip";
            string key = _prefix + fileName;

            var form = new List<KeyValuePair<string, string>>();
            foreach (var f in _fields)
                if (f.Key != "key") form.Add(f);
            form.Add(new KeyValuePair<string, string>("key", key));

            // Corpo multipart scritto su file e caricato in streaming: niente copie in RAM.
            string boundary = "xrm" + Guid.NewGuid().ToString("N");
            string bodyPath = zipPath + ".body";
            var bodyTask = Task.Run(() => WriteMultipartBody(bodyPath, boundary, form, fileName, zipPath));
            while (!bodyTask.IsCompleted) yield return null;
            if (bodyTask.IsFaulted)
            {
                TryDelete(bodyPath);
                Fail($"preparazione upload fallita: {bodyTask.Exception?.GetBaseException().Message}");
                yield break;
            }

            var req = new UnityWebRequest(_uploadUrl, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerFile(bodyPath),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 60
            };
            req.uploadHandler.contentType = $"multipart/form-data; boundary={boundary}";
            long code;
            bool ok;
            string error, body;
            float t0 = Time.realtimeSinceStartup;
            try
            {
                yield return req.SendWebRequest();
                code = req.responseCode;
                ok = req.result == UnityWebRequest.Result.Success && (code == 200 || code == 201 || code == 204);
                error = req.error;
                body = req.downloadHandler.text;
            }
            finally
            {
                req.Dispose();        // chiude il file prima di cancellarlo
                TryDelete(bodyPath);
            }

            if (ok)
            {
                _seq = seq + 1;
                Log($"Keyframe {fileName} caricato: {zipLength / 1024f:F0} kB in {Time.realtimeSinceStartup - t0:F2} s → {key}");

                bool notified = client.SendData(JsonUtility.ToJson(new UploadedMsg { session_id = session, seq = seq, s3_key = key }));
                if (!notified) LogWarning("File caricato ma DataChannel non aperto: notifica keyframe-uploaded NON inviata");

                Uploaded?.Invoke(key);
                yield break;
            }

            LogWarning($"Upload S3 rifiutato (tentativo {attempt + 1}): HTTP {code} {error} {Truncate(body)}");
            if (attempt == 1)
                Fail($"upload S3 fallito: HTTP {code} {error}");
        }
    }

    // multipart/form-data per il presigned POST di S3: tutti i campi, poi il file SEMPRE per ultimo.
    static void WriteMultipartBody(string path, string boundary, List<KeyValuePair<string, string>> fields,
                                   string fileName, string zipPath)
    {
        using var dst = File.Create(path);
        void Write(string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            dst.Write(b, 0, b.Length);
        }

        foreach (var f in fields)
            Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"{f.Key}\"\r\n\r\n{f.Value}\r\n");
        Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\n" +
              "Content-Type: application/zip\r\n\r\n");
        using (var src = File.OpenRead(zipPath))
            src.CopyTo(dst);
        Write($"\r\n--{boundary}--\r\n");
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Debug.LogWarning($"[XRM] Keyframe: impossibile cancellare {path}: {ex.Message}"); }
    }

    IEnumerator RequestPresign(string session, Action<string> onError)
    {
        string url = $"{client.HttpBaseUrl}/api/presign?session={Uri.EscapeDataString(session)}";
        using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST) { downloadHandler = new DownloadHandlerBuffer() };
        req.timeout = 20;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            onError($"HTTP {req.responseCode} {req.error}");
            yield break;
        }

        try
        {
            // "fields" è un dizionario con chiavi arbitrarie: JsonUtility non lo sa leggere.
            var json = JObject.Parse(req.downloadHandler.text);
            string uploadUrl = (string)json["upload_url"];
            string prefix = (string)json["prefix"];
            var fields = json["fields"] as JObject;
            if (string.IsNullOrEmpty(uploadUrl) || prefix == null || fields == null)
            {
                onError("risposta senza upload_url/prefix/fields");
                yield break;
            }

            _fields.Clear();
            foreach (var p in fields.Properties())
                _fields.Add(new KeyValuePair<string, string>(p.Name, (string)p.Value));
            _uploadUrl = uploadUrl;
            _prefix = prefix;
            _presignSession = session;
            _presignTime = Time.realtimeSinceStartup;
            Log($"Presign ottenuto: {uploadUrl} prefix={prefix}");
        }
        catch (Exception ex)
        {
            onError($"risposta non valida: {ex.Message}");
        }
    }

    // Copia la sorgente (RenderTexture o Texture qualsiasi) in una Texture2D leggibile e la codifica.
    byte[] EncodeJpg(Texture src, out int width, out int height)
    {
        width = src.width;
        height = src.height;

        var tmp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        var prevActive = RenderTexture.active;
        Texture2D tex = null;
        try
        {
            Graphics.Blit(src, tmp);
            RenderTexture.active = tmp;
            tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply(false);
            return tex.EncodeToJPG(jpegQuality);
        }
        finally
        {
            RenderTexture.active = prevActive;   // va sempre ripristinato
            RenderTexture.ReleaseTemporary(tmp);
            if (tex != null) Destroy(tex);
        }
    }

    void Fail(string reason)
    {
        if (client != null) client.WriteLog($"Keyframe: {reason}", LogType.Error);
        else Debug.LogError($"[XRM] Keyframe: {reason}");
        Failed?.Invoke(reason);
    }

    void Log(string msg) => client.WriteLog(msg, LogType.Log);
    void LogWarning(string msg) => client.WriteLog(msg, LogType.Warning);
    static string Truncate(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 300 ? s.Substring(0, 300) + "..." : s);
}
