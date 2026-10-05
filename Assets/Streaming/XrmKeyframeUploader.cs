using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
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
// Per ora lo zip contiene quello che manda la pagina di test: frame.jpg + metadata.json, preso
// dal frame che il Quest sta streammando. UploadZip(byte[]) è pubblico: quando la cattura keyframe
// completa (depth, pose, intrinseci) arriverà su questo branch, basta passarle lo zip già pronto.
public class XrmKeyframeUploader : MonoBehaviour
{
    [Tooltip("Se vuoto viene cercato in scena.")]
    [SerializeField] XrmSessionClient client;
    [Tooltip("Sorgente del frame da catturare. Se vuota si usa il frame streammato da VideoManager.")]
    [SerializeField] Texture sourceTexture;
    [SerializeField] VideoManager videoManager;
    [Range(1, 100)]
    [SerializeField] int jpegQuality = 85;
    // Grilletto indice destro: A/B e X/Y sono già usati da Movement nella scena Streaming.
    [Tooltip("Tasto del controller che cattura e invia un keyframe. None = solo da codice/ContextMenu.")]
    [SerializeField] OVRInput.RawButton captureButton = OVRInput.RawButton.RIndexTrigger;

    public UnityEvent<string> Uploaded;   // argomento: chiave S3 del file caricato
    public UnityEvent<string> Failed;     // argomento: descrizione dell'errore

    public bool IsBusy { get; private set; }
    public int NextSeq => _seq;

    // Presign in cache: {upload_url, fields, prefix}. Scade dopo 1 ora lato server; lo rinnoviamo
    // prima (50 min), a ogni cambio di sessione, e comunque dopo un 403 di S3.
    string _uploadUrl;
    string _prefix;
    readonly List<KeyValuePair<string, string>> _fields = new();
    string _presignSession;
    float _presignTime = float.NegativeInfinity;
    const float PresignMaxAge = 50 * 60f;

    int _seq;   // numero progressivo del keyframe nella sessione corrente

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
        if (client == null) Debug.LogError("[XRM] XrmKeyframeUploader: nessun XrmSessionClient in scena.");
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
        if (!CanStart()) return;

        Texture src = sourceTexture != null ? sourceTexture : (videoManager != null ? videoManager.CurrentFrame : null);
        if (src == null)
        {
            Fail("nessun frame da catturare (lo streaming video non è ancora partito?)");
            return;
        }

        int seq = _seq;
        byte[] zip;
        try
        {
            byte[] jpg = EncodeJpg(src, out int w, out int h);
            var meta = new Metadata
            {
                session_id = client.SessionId,
                seq = seq,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                width = w,
                height = h
            };
            zip = BuildZip(new Dictionary<string, byte[]>
            {
                { "frame.jpg", jpg },
                { "metadata.json", Encoding.UTF8.GetBytes(JsonUtility.ToJson(meta)) }
            });
        }
        catch (Exception ex)
        {
            Fail($"cattura fallita: {ex.Message}");
            return;
        }

        StartCoroutine(UploadRoutine(zip));
    }

    // Carica uno zip già pronto come prossimo keyframe della sessione.
    public void UploadZip(byte[] zipBytes)
    {
        if (!CanStart()) return;
        if (zipBytes == null || zipBytes.Length == 0)
        {
            Fail("zip vuoto");
            return;
        }
        StartCoroutine(UploadRoutine(zipBytes));
    }

    // Utility per chi costruisce lo zip da sé: nome file → contenuto.
    public static byte[] BuildZip(IDictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        // leaveOpen: la directory centrale dello zip viene scritta alla Dispose dell'archivio,
        // quindi i byte vanno letti DOPO averlo chiuso ma con lo stream ancora vivo.
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
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
        return ms.ToArray();
    }

    // ---------- Implementazione ----------

    bool CanStart()
    {
        if (client == null) { Fail("XrmSessionClient mancante"); return false; }
        if (IsBusy) { Log("Upload già in corso: richiesta ignorata"); return false; }
        return true;
    }

    IEnumerator UploadRoutine(byte[] zip)
    {
        IsBusy = true;
        try
        {
            string session = client.SessionId;
            if (session != _presignSession) _seq = 0;   // sessione nuova: la numerazione riparte

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

                var form = new List<IMultipartFormSection>();
                foreach (var f in _fields)
                    if (f.Key != "key") form.Add(new MultipartFormDataSection(f.Key, f.Value));
                form.Add(new MultipartFormDataSection("key", key));
                form.Add(new MultipartFormFileSection("file", zip, fileName, "application/zip"));   // SEMPRE ultimo

                using var req = UnityWebRequest.Post(_uploadUrl, form);
                req.timeout = 60;
                float t0 = Time.realtimeSinceStartup;
                yield return req.SendWebRequest();

                long code = req.responseCode;
                if (req.result == UnityWebRequest.Result.Success && (code == 200 || code == 201 || code == 204))
                {
                    _seq = seq + 1;
                    Log($"Keyframe {fileName} caricato: {zip.Length / 1024f:F0} kB in {Time.realtimeSinceStartup - t0:F2} s → {key}");

                    bool notified = client.SendData(JsonUtility.ToJson(new UploadedMsg { session_id = session, seq = seq, s3_key = key }));
                    if (!notified) LogWarning("File caricato ma DataChannel non aperto: notifica keyframe-uploaded NON inviata");

                    Uploaded?.Invoke(key);
                    yield break;
                }

                string body = req.downloadHandler != null ? req.downloadHandler.text : "";
                LogWarning($"Upload S3 rifiutato (tentativo {attempt + 1}): HTTP {code} {req.error} {Truncate(body)}");
                if (attempt == 1)
                    Fail($"upload S3 fallito: HTTP {code} {req.error}");
            }
        }
        finally
        {
            IsBusy = false;
        }
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
