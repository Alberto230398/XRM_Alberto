using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Pannello di debug agganciato alla camera: mostra gli ultimi Debug.Log/Warning/Error
// direttamente nel visore, senza bisogno di adb/logcat (che si sono rivelati inaffidabili
// per leggere i log di SimpleWebRTCLogger sul Quest).
public class DebugLogOverlay : MonoBehaviour
{
    [SerializeField] int maxLines = 30;
    [SerializeField] Vector3 offsetFromCamera = new Vector3(0, 0, 1.2f);
    [SerializeField] float scale = 0.001f;

    // Senza filtro, ogni riga stampata genera warning TMP sui font per renderizzarla, che a
    // loro volta finiscono nel pannello e generano altre righe → il rumore seppellisce i log
    // utili in un frame. Teniamo solo le righe che parlano di WebRTC/signaling.
    static readonly string[] Keywords = {
        "xrm", "webrtc", "websocket", "peer", "ice", "signal", "offer", "answer", "candidate", "track", "webrtcconnection", "webrtcmanager"
    };

    readonly Queue<string> _lines = new();
    // Application.logMessageReceived può scattare da thread diversi dal main (es. i log dentro
    // il Task.Run del SendLoop di WebRTCManager): niente API Unity nel callback, solo un
    // accodamento thread-safe. Il rendering vero (TMP, non thread-safe) avviene in Update().
    readonly ConcurrentQueue<string> _pending = new();
    TextMeshProUGUI _text;
    readonly StringBuilder _sb = new();

    // Se il blocco arriva dopo un burst di log troppo veloce per leggerlo a schermo, salviamo
    // TUTTO (non filtrato) anche su file con flush immediato, così i dati sono già su disco
    // anche se l'app si blocca un istante dopo — recuperabile con adb pull a freeze avvenuto.
    StreamWriter _fileWriter;
    readonly object _fileLock = new();
    string _logFilePath;

    void Awake()
    {
        BuildUI();

        try
        {
            _logFilePath = Path.Combine(Application.persistentDataPath, "webrtc_debug.log");
            _fileWriter = new StreamWriter(_logFilePath, append: false) { AutoFlush = true };
            _fileWriter.WriteLine($"=== log avviato {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            _pending.Enqueue($"<color=#55ff99>log su file: {_logFilePath}</color>");
        }
        catch (Exception e)
        {
            Debug.LogError($"[DebugLogOverlay] Impossibile aprire il file di log: {e.Message}");
        }

        Application.logMessageReceived += OnLog;
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= OnLog;
        lock (_fileLock) { _fileWriter?.Dispose(); _fileWriter = null; }
    }

    void OnLog(string logString, string stackTrace, LogType type)
    {
        // Su file scriviamo TUTTO, non filtrato: è per la lettura post-mortem con calma,
        // non per lo schermo, quindi il rumore TMP/OpenXR non è un problema qui.
        lock (_fileLock)
        {
            try { _fileWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{type}] {logString}"); }
            catch { /* meglio perdere una riga che bloccare il logging per un errore di IO */ }
        }

        // Gli errori/eccezioni li teniamo sempre a schermo (potrebbero essere il blocco stesso),
        // il resto solo se parla di WebRTC/signaling — niente rumore TMP/OpenXR/altro.
        bool isSevere = type == LogType.Error || type == LogType.Exception;
        if (!isSevere)
        {
            string lower = logString.ToLowerInvariant();
            bool relevant = false;
            foreach (var k in Keywords)
            {
                if (lower.Contains(k)) { relevant = true; break; }
            }
            if (!relevant) return;
        }

        string color = isSevere ? "#ff5555"
                      : type == LogType.Warning ? "#ffcc55"
                      : "#cccccc";
        _pending.Enqueue($"<color={color}>{logString}</color>");
    }

    void Update()
    {
        bool changed = false;
        while (_pending.TryDequeue(out var line))
        {
            _lines.Enqueue(line);
            while (_lines.Count > maxLines) _lines.Dequeue();
            changed = true;
        }
        if (!changed) return;

        _sb.Clear();
        foreach (var l in _lines) _sb.AppendLine(l);
        _text.text = _sb.ToString();
    }

    void BuildUI()
    {
        var cam = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();

        var canvasGO = new GameObject("DebugLogOverlay-Canvas");
        if (cam != null) canvasGO.transform.SetParent(cam.transform, false);
        canvasGO.transform.localPosition = offsetFromCamera;
        canvasGO.transform.localRotation = Quaternion.identity;
        canvasGO.transform.localScale = Vector3.one * scale;

        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRT = canvas.GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(900, 600);

        var bgGO = new GameObject("Background");
        bgGO.transform.SetParent(canvasGO.transform, false);
        var bgImage = bgGO.AddComponent<Image>();
        bgImage.color = new Color(0, 0, 0, 0.75f);
        var bgRT = bgImage.rectTransform;
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;

        var textGO = new GameObject("Text");
        textGO.transform.SetParent(canvasGO.transform, false);
        var textRT = textGO.AddComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = new Vector2(10, 10);
        textRT.offsetMax = new Vector2(-10, -10);

        _text = textGO.AddComponent<TextMeshProUGUI>();
        _text.fontSize = 20;
        _text.alignment = TextAlignmentOptions.TopLeft;
        _text.color = Color.white;
        _text.richText = true;
        _text.text = "";
    }
}
