using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Meta.XR.EnvironmentDepth;
using Unity.XR.Oculus;
using static Unity.XR.Oculus.Utils;
using System.Runtime.CompilerServices;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine.Experimental.Rendering;
using System.Collections.Concurrent;
using System.Threading.Tasks;


public class KeyFrameManager : MonoBehaviour
{
    private Vector3 _lastKeyframePosition;
    private Quaternion _lastKeyframeRotation;

    private Quaternion _prevHeadRotation;
    private bool _hasPrevHeadRotation = false;
    private bool _hasPrevHeadPosition = false;

    private Vector3 _lastHeadPosition;
    private Quaternion _lastHeadRotation;

    [SerializeField] float translationThreshold = 0.1f; // 10cm
    [SerializeField] float rotationThreshold = 5f; // 5 gradi
    [SerializeField] float maxHeadAngularSpeed = 30f;
    [SerializeField] float maxHeadTranslationSpeed = 0.5f; // metri/sec

    [SerializeField] float MaximumPosDeviation = 0.1f; // metri/sec
    [SerializeField] float MaximumRotDeviation = 5f; // gradi/sec

    // Accessor alla Passthrough Camera API — sensori fisicamente separati
    // dalla depth camera, ciascuno con pose/intrinseci propri.
    [SerializeField] PassthroughCameraAccess passthroughCameraLeft;
    [SerializeField] PassthroughCameraAccess passthroughCameraRight;
    [SerializeField] Camera leftCamera;
    [SerializeField] Material rawDepthMaterial;         // Shader DepthRawCopy: estrae la slice 0 del Texture2DArray senza modifiche
    [SerializeField] Material alignedDepthMaterial;     // Allinea la depth sul frame RGB della PCA
    [SerializeField] Material depthColorMaterial;       // Shader DepthColorReprojection: depth -> 3D -> colore RGB (point cloud colorato in layout depth)
    [SerializeField] Material sobelMaterial;            // Shader DepthSobel: gate del gradiente relativo sulla depth allineata (metrica)
    // Soglia del gradiente relativo del Sobel (|∇D|/D). Va tenuta separata per le due
    // risoluzioni: a risoluzione depth (320x320) ogni texel copre più scena, quindi la
    // stessa discontinuità dà un gradiente più alto e serve una soglia più permissiva.
    // <= 0 disattiva il gate (passthrough della depth). Regolabili da Inspector.
    [SerializeField] float sobelTauRelRGBRes = 0.05f;   // per l'aligned depth a risoluzione RGB
    [SerializeField] float sobelTauRelDepthRes = 0.15f; // per l'aligned depth a risoluzione depth
    [SerializeField] EnvironmentDepthManager environmentDepthManager; // Gate della cattura su IsDepthAvailable
    [SerializeField] Text debugText;

    // Tasto che accende/spegne lo scan. Nella scena Streaming A/B e X/Y sono usati da Movement.
    [SerializeField] OVRInput.RawButton scanToggleButton = OVRInput.RawButton.A;
    [SerializeField] bool scanEnabledOnStart = true;
    [SerializeField] bool saveToDisk = true;         // persistentDataPath/keyframes/N
    [SerializeField] bool verboseLog = true;         // log a ogni frame (spegnerlo se c'è DebugLogOverlay)

    // Invocato per ogni keyframe catturato con i file già codificati (nome -> contenuto),
    // gli stessi che finiscono nella cartella su disco. Lo usa XrmKeyframeUploader.
    // I byte[] non vanno modificati: possono essere letti da un altro thread.
    public event System.Action<int, IReadOnlyDictionary<string, byte[]>> KeyframeEncoded;
    public bool ScanEnabled => _scanEnabled;

    // Keyframe in lettura dalla GPU o in codifica. Ognuno trattiene ~70 MB di pixel grezzi
    // finché non è codificato: oltre questo numero le nuove catture vengono rimandate.
    [SerializeField] int maxKeyframesInFlight = 2;
    int _inFlight;

    // Lavoro che il thread di codifica rimanda al main thread (eseguito in Update).
    readonly ConcurrentQueue<System.Action> _mainThreadQueue = new();

    // Istanze separate dei materiali usati due volte per keyframe (risoluzione RGB e depth).
    Material _alignedMatRgb, _alignedMatDepth, _sobelMatRgb, _sobelMatDepth;

    private int _keyframeCount = 0;

    private bool _firstKeyframeCaptured = false;

    // === SCAN ON/OFF ===
    // DEBUG: interruttore dello scan dell'ambiente. Se false, CaptureKeyframe()
    // esce subito e non cattura nulla. Si commuta col tasto A del controller
    // destro (vedi Update()). Default: true (scan attivo all'avvio).
    private bool _scanEnabled = true;

    private uint _lastCapturedDepthTexId = uint.MaxValue;

    private uint _lastSeenDepthTexId = uint.MaxValue;
    // Buffer in memoria dei record; viene scritto su disco periodicamente e alla chiusura.
    private readonly KeyframeLog _log = new KeyframeLog();
    private double _lastLogFlushTime = 0;

    HeadPoseList _headPoseHistory;
    [SerializeField] int maxHeadPoseHistorySize = 60; // capacità del ring buffer delle pose della testa (ne bastano 3 per la deviazione)

    void Awake()
    {
        // Risolve automaticamente il depth manager se non wired in Inspector,
        // altrimenti il gate IsDepthAvailable sotto blocca ogni cattura.
        if (environmentDepthManager == null)
            environmentDepthManager = FindFirstObjectByType<EnvironmentDepthManager>();
        _scanEnabled = scanEnabledOnStart;

        if (alignedDepthMaterial != null)
        {
            _alignedMatRgb = new Material(alignedDepthMaterial);
            _alignedMatDepth = new Material(alignedDepthMaterial);
        }
        if (sobelMaterial != null)
        {
            _sobelMatRgb = new Material(sobelMaterial);
            _sobelMatDepth = new Material(sobelMaterial);
        }
    }

    void Start()
    {
        // Costruito qui (non come field initializer) così legge maxHeadPoseHistorySize
        // dopo che l'Inspector ha deserializzato il valore.
        _headPoseHistory = new HeadPoseList(maxHeadPoseHistorySize);
    }

    void OnEnable()
    {
        // Ci agganciamo al render loop invece che a Update: vedi nota in testa.
        Application.onBeforeRender += CaptureKeyframe;
    }

    void OnDisable()
    {
        Application.onBeforeRender -= CaptureKeyframe;
    }

    void OnDestroy()
    {
        // Completa i readback in volo: le callback rilasciano RenderTexture e NativeArray.
        AsyncGPUReadback.WaitAllRequests();
        if (_alignedMatRgb != null) Destroy(_alignedMatRgb);
        if (_alignedMatDepth != null) Destroy(_alignedMatDepth);
        if (_sobelMatRgb != null) Destroy(_sobelMatRgb);
        if (_sobelMatDepth != null) Destroy(_sobelMatDepth);
    }

    void Update()
    {
        while (_mainThreadQueue.TryDequeue(out var action))
            action();

        // DEBUG: tasto A del controller destro (RawButton.A = A fisico del Touch destro).
        // Un solo tasto per tutto. GetDown = solo il frame della pressione (un click = un'azione).
        //   - scan SPENTO -> lo riaccende, niente invio.
        //   - scan ACCESO -> lo spegne E invia i keyframe (fine scan).
        if (scanToggleButton != OVRInput.RawButton.None && OVRInput.GetDown(scanToggleButton))
        {
            if (!_scanEnabled)
            {
                // era spento -> riparte lo scan
                _scanEnabled = true;
                Debug.Log($"[SCAN] Scan ABILITATO ({scanToggleButton})");
                if (debugText != null)
                    debugText.text = $"Scan attiva, kf = {_keyframeCount}";
            }
            else
            {
                // era acceso -> ferma lo scan e invia
                _scanEnabled = false;
                Debug.Log($"[SEND] Scan DISABILITATO, avvio invio keyframe ({scanToggleButton})");
                // _keyframeCount = numero di keyframe catturati/salvati = quelli che verranno inviati.
                if (debugText != null)
                    debugText.text = $"Scan disattivata, kf inviati: {_keyframeCount}";
                //RetrieveAndSendData();
            }
        }
    }

    // Ordine 100 > 0 del manager Meta -> giriamo SEMPRE dopo che i global depth
    // sono stati aggiornati col frame corrente.
    [BeforeRenderOrder(100)]
    void CaptureKeyframe()
    {
        if (verboseLog)
            Debug.Log("-----------CaptureKeyframe() called at time: " + System.DateTime.Now.ToString("HH:mm:ss.fff") + "-----------");

        // === GATE SCAN ON/OFF ===
        // DEBUG: se lo scan e' disabilitato (tasto A, vedi Update()) non catturiamo nulla.
        // Se durante un test non vengono salvati keyframe, controlla PRIMA questo flag.
        if (!_scanEnabled) return;

        // === CONTROLLI DI VALIDITÀ ===
        // Se la depth non è disponibile o la camera PCA non sta girando, esci subito.
        if (environmentDepthManager == null || !environmentDepthManager.IsDepthAvailable) return;
        if (passthroughCameraLeft == null || !passthroughCameraLeft.IsPlaying) return;

        // Pose della testa (camera PCA sinistra) in questo istante.
        var pose = passthroughCameraLeft.GetCameraPose();

        /*
        // === STIMA VELOCITÀ DELLA TESTA ===
        float headAngularSpeed = 0f; // gradi/sec
        if (_hasPrevHeadRotation)
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f)
                headAngularSpeed = Quaternion.Angle(pose.rotation, _prevHeadRotation) / dt;
        }
        _prevHeadRotation = pose.rotation;
        _hasPrevHeadRotation = true;

        // === Stima Traslazione testa ===

        float headTranslation = 0f; // metri/sec
        if (_hasPrevHeadPosition)
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f)
                headTranslation = Vector3.Distance(pose.position, _lastHeadPosition) / dt;
        }
        _lastHeadPosition = pose.position;
        _hasPrevHeadPosition = true;*/

        // === Controllo 1: FRAME DEPTH NUOVO ===

        uint depthTexId = 0;
        if (!Utils.GetEnvironmentDepthTextureId(ref depthTexId)) return;

        // Un frame depth è "nuovo" se l'id è cambiato rispetto al tick precedente.
        bool isNewDepthFrame = depthTexId != _lastSeenDepthTexId;
        _lastSeenDepthTexId = depthTexId;
        if (depthTexId == _lastCapturedDepthTexId) return;

        // === Controllo 2: TESTA TROPPO VELOCE ===
      
        /*if (headAngularSpeed > maxHeadAngularSpeed)
        {
            if (isNewDepthFrame)
                LogEvent("skip_head_fast", depthTexId, headAngularSpeed, pose, -1, 0f, 0f);
            return;
        }

        // === Controllo 2b: TESTA TROPPO VELOCE (TRASLAZIONE) ===

        if (headTranslation > maxHeadTranslationSpeed)
        {
            if (isNewDepthFrame)
                LogEvent("skip_head_fast_translation", depthTexId, headAngularSpeed, pose, -1, 0f, 0f);
            return;
        }*/

        // Salvo la posizione della testa ad ogni tick

        HeadPose headPose = new HeadPose
        {
            timestamp = Time.unscaledTimeAsDouble,
            position = pose.position,
            rotation = pose.rotation
        };

       _headPoseHistory.Push(headPose);

       bool isHeadPoseValid = false;

       if (!_headPoseHistory.HasThree()) return;

        HeadPose actual = _headPoseHistory.Last();
        HeadPose last = _headPoseHistory.Prev();
        HeadPose prev = _headPoseHistory.PrevPrev();

        isHeadPoseValid = CalculateDeviation(actual, last, prev);

        if (!isHeadPoseValid)
            return;

        // === PRIMO KEYFRAME ===
        // Appena la depth diventa disponibile cattura il primo keyframe

        // Troppi keyframe ancora in lettura/codifica: si salta (riprova ai frame successivi)
        // invece di accumulare decine di MB di pixel in RAM.
        if (_inFlight >= maxKeyframesInFlight) return;

        if (!_firstKeyframeCaptured)
        {
            if (!DoCaptureKeyframe(pose, depthTexId)) return;
            _firstKeyframeCaptured = true;
            _lastKeyframePosition = pose.position;
            _lastKeyframeRotation = pose.rotation; 
            //LogEvent("captured", depthTexId, headAngularSpeed, pose, _keyframeCount - 1, 0f, 0f);
            return;
        }

        // === GATE 3: SPAZIATURA TRA KEYFRAME ===
        // Cattura solo se ci siamo spostati/ruotati abbastanza rispetto all'ULTIMO
        // keyframe salvato.

        float translation = Vector3.Distance(pose.position, _lastKeyframePosition);
        float rotation = Quaternion.Angle(pose.rotation, _lastKeyframeRotation);
        if (translation <= translationThreshold && rotation <= rotationThreshold)
            return;

        // Tutti i gate superati: catturiamo il keyframe.
        if (!DoCaptureKeyframe(pose, depthTexId)) return;
        _lastKeyframePosition = pose.position;
        _lastKeyframeRotation = pose.rotation;
    }

    
    // Confronta la pose della camera RGB con la pose della camera DEPTH nel momento
    // in cui ciascun frame è stato catturato.
    //
    // La pose RGB arriva da rgbPose (già calcolata al timestamp del frame RGB).
    // La pose DEPTH la ricaviamo dalla reprojection matrix globale della depth
    // (_EnvironmentDepthReprojectionMatrices[0]): quella matrice mappa world -> clip
    // della camera depth ed è costruita da Meta con la createPose, cioè la posizione
    // della testa NEL MOMENTO in cui il sensore ha catturato la depth (non adesso).
    //
    // Invertendo la matrice (clip -> world) ricostruiamo geometricamente dove guardava
    // e dov'era la camera depth in quell'istante. L'angolo tra le due direzioni di
    // sguardo (skewAngleDeg) è il disallineamento reale: se è grande, depth e RGB
    // stanno inquadrando due parti diverse della stanza.

    bool CalculateDeviation(HeadPose actual, HeadPose last, HeadPose prev)
    {
        bool isFrameOk = false;

        float FirstDiff = Vector3.Distance(actual.position, last.position) / (float)(actual.timestamp - last.timestamp);
        float SecondDiff = Vector3.Distance(last.position, prev.position) / (float)(last.timestamp - prev.timestamp);
        float PosDev = 0.5f*(FirstDiff + SecondDiff);

        float FirstRotDiff = Quaternion.Angle(actual.rotation, last.rotation) / (float)(actual.timestamp - last.timestamp);
        float SecondRotDiff = Quaternion.Angle(last.rotation, prev.rotation) / (float)(last.timestamp - prev.timestamp);
        float RotDev = 0.5f*(FirstRotDiff + SecondRotDiff);

        if (PosDev > MaximumPosDeviation || RotDev > MaximumRotDeviation)
        {
            isFrameOk = false;
            if (verboseLog) Debug.Log($"Head Pose Deviation Exceeded: PosDev={PosDev} m/s, RotDev={RotDev} deg/s");
        }
        else
        {
            isFrameOk = true;
        }

        return isFrameOk;
    }
    bool TryComputeDepthRgbSkew(Pose rgbPose, out Vector3 rgbFwd, out Vector3 depthFwd,
                                out Vector3 depthEye, out float skewAngleDeg, out float posDiffM)
    {
        rgbFwd = rgbPose.rotation * Vector3.forward;
        depthFwd = Vector3.zero;
        depthEye = Vector3.zero;
        skewAngleDeg = 0f;
        posDiffM = 0f;

        var reproj = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
        if (reproj == null || reproj.Length == 0) return false;

        // m mappa world -> clip della camera depth (proj * view, con la createPose
        // della depth già dentro). Estraiamo i piani del frustum in world space
        // sommando/sottraendo le righe (Gribb-Hartmann): NIENTE inversa, NIENTE
        // divisione per w — così evitiamo i NaN del metodo precedente.
        Matrix4x4 m = reproj[0];
        Vector4 r0 = m.GetRow(0);
        Vector4 r1 = m.GetRow(1);
        Vector4 r3 = m.GetRow(3);

        // Piani laterali del frustum (passano TUTTI per il centro ottico della camera).
        Vector4 left   = r3 + r0;
        Vector4 right  = r3 - r0;
        Vector4 bottom = r3 + r1;

        // Centro ottico della depth = intersezione dei tre piani laterali.
        // Formula standard di intersezione di 3 piani (n_i · X + d_i = 0).
        Vector3 nL = new Vector3(left.x, left.y, left.z);
        Vector3 nR = new Vector3(right.x, right.y, right.z);
        Vector3 nB = new Vector3(bottom.x, bottom.y, bottom.z);
        Vector3 cRB = Vector3.Cross(nR, nB);
        float det = Vector3.Dot(nL, cRB);
        if (Mathf.Abs(det) < 1e-12f) return false;
        Vector3 cBL = Vector3.Cross(nB, nL);
        Vector3 cLR = Vector3.Cross(nL, nR);
        depthEye = (-left.w * cRB - right.w * cBL - bottom.w * cLR) / det;

        // Direzione di sguardo: la riga w della matrice (r3) punta lungo l'asse ottico,
        // perché left+right = bottom+top = 2*r3 (le componenti laterali si annullano).
        // È indipendente dalla convenzione dello z-clip.
        Vector3 fwd = new Vector3(r3.x, r3.y, r3.z);
        if (fwd.sqrMagnitude < 1e-12f) return false;
        fwd.Normalize();
        // Depth e RGB sono co-locate sulla stessa testa: il forward vero è quasi
        // parallelo a quello RGB. Il segno di r3 dipende dalla convenzione, quindi
        // scegliamo il verso più vicino all'RGB (un vero disallineamento resta piccolo,
        // non arriva mai a ribaltare la scelta).
        if (Vector3.Dot(fwd, rgbFwd) < 0f) fwd = -fwd;
        depthFwd = fwd;

        skewAngleDeg = Vector3.Angle(depthFwd, rgbFwd);
        posDiffM = Vector3.Distance(depthEye, rgbPose.position);
        return true;
    }

    // Cattura NON bloccante. Sul main thread restano solo i Blit (comandi GPU, costano ~nulla
    // in CPU) e le richieste di AsyncGPUReadback. Prima ogni keyframe faceva ~10 ReadPixels (ognuno
    // ferma la CPU finché la GPU non ha finito) + 5 PNG + 5 EXR + scrittura su disco nel render
    // loop: centinaia di ms di freeze a keyframe, videochiamata compresa.
    //   1) main:    Blit sui RenderTexture temporanei + AsyncGPUReadback.RequestIntoNativeArray
    //   2) main:    callback dei readback (qualche frame dopo) → quando sono arrivati tutti...
    //   3) thread:  codifica PNG/EXR (ImageConversion.EncodeNativeArrayTo* è thread-safe) + disco
    //   4) main:    HttpManager + evento KeyframeEncoded (l'uploader usa API Unity)
    // Restituisce false se il keyframe non è partito (dati non pronti).
    bool DoCaptureKeyframe(Pose pose, uint depthTexId)
    {
        // Pose PCA — usate per la riproiezione RGB (world -> spazio colore),
        // NON per l'unprojection della depth (il sensore depth ha pose propria,
        // già bakata nella reprojection matrix, vedi sotto).
        var rightPose = passthroughCameraRight.GetCameraPose();

        // TUTTI i dati depth dai GLOBAL dello shader (nessuna chiamata a Utils
        // per la desc): sono scritti atomicamente dal manager Meta nello stesso
        // OnBeforeRender, quindi mutuamente coerenti col frame corrente.
        var depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
        if (depthTex == null) return false;

        Matrix4x4[] reproj = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
        Vector4 zParams = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams");
        if (reproj == null || reproj.Length == 0) return false;

        // Matrice world -> clip della DEPTH camera (fov del sensore + createPose
        // del sensore già inclusi nel blocco proj*view). La sua inversa è
        // l'unprojection depth->world autorevole per il server. NON è la eye
        // camera: è calibrata sul sensore depth.
        Matrix4x4 depthWorldToClip = reproj[0];

        Texture leftTex = passthroughCameraLeft.GetTexture();
        Texture rightTex = passthroughCameraRight.GetTexture();
        if (leftTex == null || rightTex == null) return false;

        if (verboseLog)
        {
            Debug.Log("----------------SYSTEM TIME WHEN SAVING RGB TEXTURE:" + System.DateTime.Now.ToString("HH:mm:ss:fff"));
            Debug.Log("----------------PCA TIME WHEN TEXTURE WAS CREATED:" + passthroughCameraLeft.Timestamp.ToString("HH:mm:ss:fff"));
        }

        // Risoluzione dell'immagine RGB corrente (può differire dal sensore pieno):
        // serve per calcolare il crop di aspect-ratio come fa il SDK.
        Vector2 currentRes = passthroughCameraLeft.CurrentResolution;
        int depthW = depthTex.width, depthH = depthTex.height;
        var intrinsics = passthroughCameraLeft.Intrinsics;
        var rightIntrinsics = passthroughCameraRight.Intrinsics;

        var job = new KeyframeJob { index = _keyframeCount++ };

        // === COPPIA A RISOLUZIONE RGB ===
        // RGB nativo + depth allineata renderizzata alla risoluzione RGB: combaciano
        // pixel-per-pixel senza stretch di aspect-ratio.
        RenderTexture rgb = BlitColor(leftTex, leftTex.width, leftTex.height);
        RenderTexture rgbRight = BlitColor(rightTex, rightTex.width, rightTex.height);
        RenderTexture alignedDepth = RenderAlignedDepth(_alignedMatRgb,
            depthTex, depthWorldToClip, pose.position, pose.rotation,
            intrinsics, zParams, currentRes, (int)currentRes.x, (int)currentRes.y);

        // === COPPIA A RISOLUZIONE DEPTH ===
        // Stessa registrazione (crop ancora basato su currentRes), ma griglia di
        // output = risoluzione depth. RGB ricampionato dalla camera texture direttamente
        // alla risoluzione depth (un solo resample). Depth allineata idem: le due
        // restano pixel-per-pixel tra loro (stesso stretch di aspect-ratio).
        RenderTexture rgbDepthRes = BlitColor(leftTex, depthW, depthH);
        RenderTexture rgbRightDepthRes = BlitColor(rightTex, depthW, depthH);
        RenderTexture alignedDepthDepthRes = RenderAlignedDepth(_alignedMatDepth,
            depthTex, depthWorldToClip, pose.position, pose.rotation,
            intrinsics, zParams, currentRes, depthW, depthH);

        // Depth allineata col gate del gradiente relativo (edge-bleeding azzerato).
        // Una versione per ciascuna risoluzione, così combacia pixel-per-pixel con la
        // rispettiva depth allineata. Ora parte direttamente dalla RT allineata sulla GPU.
        RenderTexture alignedDepthSobel = RenderDepthSobel(_sobelMatRgb, alignedDepth, sobelTauRelRGBRes);
        RenderTexture alignedDepthSobelDepthRes = RenderDepthSobel(_sobelMatDepth, alignedDepthDepthRes, sobelTauRelDepthRes);

        // Depth raw nativa + point cloud colorato (già a risoluzione depth).
        RenderTexture rawDepth = RenderDepthRaw(depthTex);
        RenderTexture depthColored = RenderDepthColored(
            depthTex, rgb, depthWorldToClip,
            pose.position, pose.rotation, intrinsics, currentRes);

        // Tutti i Blit sono già in coda alla GPU: ora si chiedono le letture, nello stesso ordine
        // dei file della vecchia cartella. Un output null (materiale mancante) viene saltato.
        const Texture2D.EXRFlags exrFloatZip = Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP;
        job.Add("LeftRGB.png", rgb, ImageKind.Png);
        job.Add("RightRGB.png", rgbRight, ImageKind.Png);
        job.Add("rawDepth.exr", rawDepth, ImageKind.Exr, Texture2D.EXRFlags.None);   // half, come prima
        job.Add("alignedDepth.exr", alignedDepth, ImageKind.Exr, exrFloatZip);
        job.Add("LeftRGB_depthRes.png", rgbDepthRes, ImageKind.Png);
        job.Add("RightRGB_depthRes.png", rgbRightDepthRes, ImageKind.Png);
        job.Add("alignedDepth_depthRes.exr", alignedDepthDepthRes, ImageKind.Exr, exrFloatZip);
        job.Add("alignedDepth_sobel.exr", alignedDepthSobel, ImageKind.Exr, exrFloatZip);
        job.Add("alignedDepth_sobel_depthRes.exr", alignedDepthSobelDepthRes, ImageKind.Exr, exrFloatZip);
        job.Add("Colored.png", depthColored, ImageKind.Png);

        // === METADATI === (piccoli: si calcolano ora, con le pose di QUESTO frame)
        // Pose PCA sinistra/destra (world/tracking space) — usate per
        // riproiettare i punti depth unprojected nello spazio camera RGB server-side.
        string timestamp = passthroughCameraLeft.Timestamp.ToString("HH:mm:ss:fff");
        job.texts["LeftCamPose.json"] = JsonUtility.ToJson(new PoseData
        {
            px = pose.position.x, py = pose.position.y, pz = pose.position.z,
            rx = pose.rotation.x, ry = pose.rotation.y, rz = pose.rotation.z, rw = pose.rotation.w,
            timestamp = timestamp
        });
        job.texts["RightCamPose.json"] = JsonUtility.ToJson(new PoseData
        {
            px = rightPose.position.x, py = rightPose.position.y, pz = rightPose.position.z,
            rx = rightPose.rotation.x, ry = rightPose.rotation.y, rz = rightPose.rotation.z, rw = rightPose.rotation.w,
            timestamp = timestamp
        });
        // Intrinseci RGB
        job.texts["LeftIntrinsics.json"] = JsonUtility.ToJson(new IntrinsicsData
        {
            FocalLength = intrinsics.FocalLength,
            PrincipalPoint = intrinsics.PrincipalPoint,
            SensorResolution = intrinsics.SensorResolution
        });
        job.texts["RightIntrinsics.json"] = JsonUtility.ToJson(new IntrinsicsData
        {
            FocalLength = rightIntrinsics.FocalLength,
            PrincipalPoint = rightIntrinsics.PrincipalPoint,
            SensorResolution = rightIntrinsics.SensorResolution
        });
        job.texts["PassthroughCamDistance.txt"] = Vector3.Distance(pose.position, rightPose.position)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Matrice di reprojection della DEPTH camera (world -> clip): fov + pose
        // del sensore depth già bakati. Il server unprojetta la depth con la sua
        // inversa (salvata sotto).
        job.texts["reprojection.json"] = JsonUtility.ToJson(new Matrix4x4Data(depthWorldToClip));
        job.texts["reprojection_inverse.json"] = JsonUtility.ToJson(new Matrix4x4Data(depthWorldToClip.inverse));
        // zBufferParams (per la linearizzazione offline dei valori di depth raw)
        job.texts["zbuffer_params.json"] = JsonUtility.ToJson(new ZBufferParamsData
        {
            x = zParams.x, y = zParams.y, z = zParams.z, w = zParams.w
        });
        // Risoluzione nativa della depth texture.
        job.texts["depth_meta.json"] = JsonUtility.ToJson(new DepthMetaData { width = depthW, height = depthH });

        job.dir = saveToDisk ? $"{Application.persistentDataPath}/keyframes/{job.index}" : null;

        // Marca il frame depth come consumato: il prossimo keyframe userà un id diverso.
        _lastCapturedDepthTexId = depthTexId;
        _inFlight++;

        // A scan attiva: mostra il conteggio incrementale dei keyframe catturati.
        if (debugText != null)
            debugText.text = $"Scan attiva, kf = {_keyframeCount}";

        StartReadbacks(job);
        Debug.Log($"Keyframe captured: {_keyframeCount} | pos: {pose.position} | depthTexId: {depthTexId}");
        return true;
    }

    // ---------- Pipeline asincrona ----------

    enum ImageKind { Png, Exr }

    class ImageOut
    {
        public string name;
        public RenderTexture rt;
        public ImageKind kind;
        public Texture2D.EXRFlags exrFlags;
        public NativeArray<byte> data;   // Persistent: liberato dal thread di codifica
        public int width, height;
        public GraphicsFormat format;
    }

    class KeyframeJob
    {
        public int index;
        public string dir;   // null = niente disco
        public readonly List<ImageOut> images = new();
        public readonly Dictionary<string, string> texts = new();
        public int pending;
        public bool failed;

        public void Add(string name, RenderTexture rt, ImageKind kind, Texture2D.EXRFlags flags = 0)
        {
            if (rt != null) images.Add(new ImageOut { name = name, rt = rt, kind = kind, exrFlags = flags });
        }
    }

    void StartReadbacks(KeyframeJob job)
    {
        job.pending = job.images.Count;
        foreach (var img in job.images)
        {
            // Lettura nel formato NATIVO della RT (ARGB32 → 4 byte, ARGBFloat → 16 byte): nessuna
            // conversione sulla GPU, quindi gli stessi byte che dava ReadPixels (niente decode sRGB).
            img.width = img.rt.width;
            img.height = img.rt.height;
            img.format = img.rt.graphicsFormat;
            int bytesPerPixel = img.kind == ImageKind.Exr ? 16 : 4;
            img.data = new NativeArray<byte>(img.width * img.height * bytesPerPixel,
                                             Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var captured = img;
            AsyncGPUReadback.RequestIntoNativeArray(ref img.data, img.rt, 0, req => OnReadback(job, captured, req));
        }
        if (job.pending == 0) FinishOnMainThread(job, null, "nessuna immagine prodotta (materiali mancanti?)");
    }

    // Main thread, qualche frame dopo la richiesta.
    void OnReadback(KeyframeJob job, ImageOut img, AsyncGPUReadbackRequest req)
    {
        if (req.hasError) job.failed = true;
        RenderTexture.ReleaseTemporary(img.rt);   // la GPU ha finito di usarla
        img.rt = null;
        if (--job.pending > 0) return;

        if (job.failed)
        {
            foreach (var i in job.images) if (i.data.IsCreated) i.data.Dispose();
            FinishOnMainThread(job, null, "AsyncGPUReadback fallito");
            return;
        }
        Task.Run(() => EncodeAndSave(job));
    }

    // Thread di background: niente API Unity qui, tranne ImageConversion (thread-safe).
    void EncodeAndSave(KeyframeJob job)
    {
        try
        {
            var files = new Dictionary<string, byte[]>();
            foreach (var img in job.images)
            {
                NativeArray<byte> encoded = img.kind == ImageKind.Png
                    ? ImageConversion.EncodeNativeArrayToPNG(img.data, img.format, (uint)img.width, (uint)img.height)
                    : ImageConversion.EncodeNativeArrayToEXR(img.data, img.format, (uint)img.width, (uint)img.height, 0, img.exrFlags);
                files[img.name] = encoded.ToArray();
                encoded.Dispose();
                img.data.Dispose();   // i pixel grezzi (fino a 26 MB) si liberano appena codificati
            }
            foreach (var t in job.texts)
                files[t.Key] = System.Text.Encoding.UTF8.GetBytes(t.Value);

            if (job.dir != null)
            {
                System.IO.Directory.CreateDirectory(job.dir);
                foreach (var f in files)
                    System.IO.File.WriteAllBytes($"{job.dir}/{f.Key}", f.Value);
            }
            _mainThreadQueue.Enqueue(() => FinishOnMainThread(job, files, null));
        }
        catch (System.Exception ex)
        {
            foreach (var img in job.images) if (img.data.IsCreated) img.data.Dispose();
            _mainThreadQueue.Enqueue(() => FinishOnMainThread(job, null, ex.Message));
        }
    }

    void FinishOnMainThread(KeyframeJob job, Dictionary<string, byte[]> files, string error)
    {
        _inFlight--;
        if (error != null)
        {
            Debug.LogError($"[KF] Keyframe {job.index} scartato: {error}");
            return;
        }

        // ---------------INVIO FRAME BY FRAME----------------
        // HttpManager c'è solo nella scena 3D Reconstruction.
        if (HttpManager.httpMng != null)
            HttpManager.httpMng.SetRGBTexture(
                files["LeftRGB.png"], files["LeftRGB_depthRes.png"], files["alignedDepth.exr"],
                files["alignedDepth_depthRes.exr"], files["alignedDepth_sobel_depthRes.exr"],
                job.texts["LeftCamPose.json"], job.texts["LeftIntrinsics.json"], job.texts["reprojection.json"],
                job.texts["zbuffer_params.json"], job.texts["depth_meta.json"]);

        KeyframeEncoded?.Invoke(job.index, files);
    }

    // ---------- Rendering su GPU (solo comandi, nessuna attesa) ----------

    static RenderTexture BlitColor(Texture src, int w, int h)
    {
        // ARGB32 = R8G8B8A8: si legge come RGBA32 senza conversioni.
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(src, rt);
        return rt;
    }

    static RenderTexture NewFloatRT(int w, int h) =>
        RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat);

    /// <summary>
    /// Depth raw a risoluzione nativa della depth camera (non registrata, non allineata
    /// all'RGB). Registrazione/allineamento rimandati al server usando la reprojection
    /// matrix + pose/intrinseci PCA.
    /// </summary>
    RenderTexture RenderDepthRaw(Texture depthTexArray)
    {
        // Deve passare attraverso lo shader di array-sampling (DepthRawCopy):
        // un Graphics.Blit semplice usa lo shader sampler2D di default e non
        // può leggere una slice di Texture2DArray, dando un risultato
        // piatto/uniforme ("monocolore").
        if (rawDepthMaterial == null)
        {
            Debug.LogError("rawDepthMaterial (Custom/DepthRawCopy) is not assigned — rawDepth.exr would be monochrome.");
            return null;
        }
        // Target float a 4 canali: render target RFloat a canale singolo sono inaffidabili su Quest.
        var rt = NewFloatRT(depthTexArray.width, depthTexArray.height);
        Graphics.Blit(depthTexArray, rt, rawDepthMaterial);
        return rt;
    }

    void RetrieveAndSendData()
    {
        // Costruiamo SOLO il path della cartella che contiene tutti i keyframe
        // salvati in locale (persistentDataPath/keyframes) e lo passiamo come
        // stringa a HttpManager. Zip + invio sono responsabilita' di HttpManager,
        // qui non tocchiamo file ne' rete.
        string keyframesDir = $"{Application.persistentDataPath}/keyframes";

        // DEBUG: se l'invio non parte, verifica che questa cartella esista e sia piena.
        if (!System.IO.Directory.Exists(keyframesDir))
        {
            Debug.LogWarning($"[SEND] Cartella keyframe inesistente: {keyframesDir}");
            return;
        }

        Debug.Log($"[SEND] Passo la cartella a HttpManager: {keyframesDir}");
        //HttpManager.httpMng.SendKeyframesFolder(keyframesDir);
    }

    // Replica di CalcSensorCropRegion (metodo privato del SDK PassthroughCameraAccess):
    // l'immagine RGB corrente è un ritaglio centrato del sensore pieno per adattarne
    // l'aspect ratio (es. sensore 1280x1280, immagine 1280x960 -> crop (0,160,1280,960)).
    // Restituisce (cropX, cropY, cropWidth, cropHeight) in pixel del sensore: mappa la
    // viewport [0,1] dell'immagine nelle coordinate pixel del sensore, coerente con
    // gli intrinseci (FocalLength/PrincipalPoint sono nel frame del sensore pieno).
    static Vector4 CalcSensorCropRegion(Vector2 sensorResolution, Vector2 currentResolution)
    {
        Vector2 scaleFactor = new Vector2(currentResolution.x / sensorResolution.x,
                                          currentResolution.y / sensorResolution.y);
        scaleFactor /= Mathf.Max(scaleFactor.x, scaleFactor.y);
        return new Vector4(
            sensorResolution.x * (1f - scaleFactor.x) * 0.5f,
            sensorResolution.y * (1f - scaleFactor.y) * 0.5f,
            sensorResolution.x * scaleFactor.x,
            sensorResolution.y * scaleFactor.y);
    }

    // mat: istanza dedicata (una per risoluzione). Senza un'attesa sincrona tra i due Blit,
    // impostare due volte lo stesso materiale nello stesso frame sarebbe fragile.
    RenderTexture RenderAlignedDepth(Material mat, Texture depthTexArray, Matrix4x4 reprojMatrix,
    Vector3 rgbPos, Quaternion rgbRot, PassthroughCameraAccess.CameraIntrinsics intr, Vector4 zParams,
    Vector2 currentResolution, int outWidth, int outHeight)
    {
        if (mat == null) { Debug.LogError("alignedDepthMaterial not assigned"); return null; }

        mat.SetMatrix("_ReprojMatrix", reprojMatrix);
        mat.SetVector("_RGBPosition", rgbPos);
        mat.SetMatrix("_RGBRotation", Matrix4x4.Rotate(rgbRot));
        mat.SetVector("_FocalLength", new Vector4(intr.FocalLength.x, intr.FocalLength.y));
        mat.SetVector("_PrincipalPoint", new Vector4(intr.PrincipalPoint.x, intr.PrincipalPoint.y));
        mat.SetVector("_EnvironmentDepthZBufferParams", zParams);

        // Crop di aspect-ratio del sensore (come CalcSensorCropRegion del SDK): mappa
        // la viewport [0,1] dell'immagine RGB nelle coordinate pixel del sensore pieno.
        // Passare (0,0,sensor) darebbe un errore di scala verticale che disallinea la
        // depth ai bordi (0 al centro, massimo in alto/basso).
        mat.SetVector("_CropRegion",
            CalcSensorCropRegion(intr.SensorResolution, currentResolution));

        // Risoluzione di output parametrizzata (outWidth/outHeight): il crop sopra
        // resta basato su currentResolution, quindi il frustum campionato è sempre il
        // piano immagine RGB — qui cambia solo il numero di pixel della griglia. Con
        // outW/outH = currentResolution si ottiene la coppia a risoluzione RGB; con
        // outW/outH = risoluzione depth quella a risoluzione depth.
        var rt = NewFloatRT(outWidth, outHeight);
        Graphics.Blit(depthTexArray, rt, mat);
        return rt;
    }

    /// <summary>
    /// Applica il gate del gradiente relativo (shader Custom/DepthSobel) a una depth GIÀ
    /// allineata: emette la DEPTH MASCHERATA, cioè il valore metrico originale dove
    /// |∇D|/D <= tauRel e 0 dove il pixel viene scartato (edge-bleeding) o è invalido.
    /// L'output eredita la risoluzione della depth allineata sorgente.
    /// </summary>
    /// <param name="tauRel">Soglia del gradiente relativo passata allo shader per QUESTO
    /// blit. Va scelta in base alla risoluzione della depth sorgente (più alta a bassa
    /// risoluzione). <= 0 disattiva il gate (passthrough).</param>
    RenderTexture RenderDepthSobel(Material mat, RenderTexture alignedDepth, float tauRel)
    {
        if (alignedDepth == null) return null;
        if (mat == null) { Debug.LogError("sobelMaterial (Custom/DepthSobel) not assigned"); return null; }

        // Istanza dedicata per risoluzione: ognuna tiene il proprio tauRel.
        mat.SetFloat("_TauRel", tauRel);

        // Target float: la depth mascherata è metrica, non va clampata a [0,1].
        var rt = NewFloatRT(alignedDepth.width, alignedDepth.height);
        Graphics.Blit(alignedDepth, rt, mat);
        return rt;
    }

    /// <summary>
    /// Forward warp: depth -> 3D world -> colore RGB. Per ogni pixel della depth
    /// ricostruisce il punto 3D con l'inversa della reproj matrix, lo proietta nella
    /// camera RGB e ne campiona il colore. Output = point cloud colorato in layout
    /// depth (risoluzione depth camera). Usa lo shader Custom/DepthColorReprojection.
    /// </summary>
    RenderTexture RenderDepthColored(Texture depthTexArray, Texture rgbTex, Matrix4x4 reprojMatrix,
        Vector3 rgbPos, Quaternion rgbRot, PassthroughCameraAccess.CameraIntrinsics intr,
        Vector2 currentResolution)
    {
        if (depthColorMaterial == null) { Debug.LogError("depthColorMaterial not assigned"); return null; }

        // Inversa: mappa il clip-space della depth -> world. Precalcolata qui (niente inverse in shader).
        depthColorMaterial.SetMatrix("_InvReprojMatrix", reprojMatrix.inverse);
        depthColorMaterial.SetVector("_RGBPosition", rgbPos);
        depthColorMaterial.SetMatrix("_RGBRotation", Matrix4x4.Rotate(rgbRot));
        depthColorMaterial.SetVector("_FocalLength", new Vector4(intr.FocalLength.x, intr.FocalLength.y));
        depthColorMaterial.SetVector("_PrincipalPoint", new Vector4(intr.PrincipalPoint.x, intr.PrincipalPoint.y));

        // Stesso crop di aspect-ratio del SDK: senza, le rgbUV campionate dal
        // point cloud colorato risultano disallineate al colore RGB reale.
        depthColorMaterial.SetVector("_CropRegion",
            CalcSensorCropRegion(intr.SensorResolution, currentResolution));
        depthColorMaterial.SetTexture("_RGBTex", rgbTex);

        var rt = RenderTexture.GetTemporary(depthTexArray.width, depthTexArray.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(depthTexArray, rt, depthColorMaterial);
        return rt;
    }
}

[System.Serializable]
struct PoseData
{
    public float px, py, pz;
    public float rx, ry, rz, rw;
    public string timestamp;
}

[System.Serializable]
struct IntrinsicsData
{
    public Vector2 FocalLength;
    public Vector2 PrincipalPoint;
    public Vector2 SensorResolution;
}

[System.Serializable]
struct ZBufferParamsData
{
    public float x, y, z, w;
}

[System.Serializable]
struct Matrix4x4Data
{
    public float m00, m01, m02, m03;
    public float m10, m11, m12, m13;
    public float m20, m21, m22, m23;
    public float m30, m31, m32, m33;

    public Matrix4x4Data(Matrix4x4 m)
    {
        m00 = m.m00; m01 = m.m01; m02 = m.m02; m03 = m.m03;
        m10 = m.m10; m11 = m.m11; m12 = m.m12; m13 = m.m13;
        m20 = m.m20; m21 = m.m21; m22 = m.m22; m23 = m.m23;
        m30 = m.m30; m31 = m.m31; m32 = m.m32; m33 = m.m33;
    }
}

[System.Serializable]
struct DepthMetaData
{
    public float width, height;
}

// Un record per ogni evento "punto chiave" (frame depth nuovo: catturato o scartato).
[System.Serializable]
struct KeyframeLogEntry
{
    public int frame;                 // Time.frameCount — ordine di render
    public double appTime;            // secondi dall'avvio dell'app
    public string systemTimeUtc;      // ora di sistema (UTC) dell'evento
    public string outcome;            // captured | skip_head_fast | skip_spacing
    public int keyframeIndex;         // cartella keyframes/N se catturato, altrimenti -1
    public uint depthTexId;           // handle del buffer swapchain della depth
    public float headAngularSpeed;    // gradi/sec: quanto ruotava la testa nell'istante
    public string rgbTimestampUtc;    // timestamp del frame RGB della camera PCA
    public float rgbAgeMs;            // età del frame RGB (ms) rispetto all'istante dell'evento
    public Vector3 headPos;           // posizione testa (camera PCA sinistra)
    public Vector4 headRot;           // rotazione testa (quaternione x,y,z,w)
    public float translationFromLast; // spostamento dall'ultimo keyframe salvato (m)
    public float rotationFromLast;    // rotazione dall'ultimo keyframe salvato (gradi)

    // --- CONFRONTO POSE RGB vs DEPTH (skew reale) ---
    public bool skewValid;            // false se non è stato possibile estrarre la pose depth
    public Vector3 rgbForward;        // direzione di sguardo della camera RGB (world), al t di cattura RGB
    public Vector3 depthForward;      // direzione di sguardo della camera DEPTH (world), al t di cattura depth
    public Vector3 depthEyePos;       // posizione della camera depth (world), estratta dalla reproj matrix
    public float skewAngleDeg;        // ANGOLO tra le due direzioni di sguardo = disallineamento rotazionale
    public float posDiffM;            // distanza tra camera depth e camera RGB (m)
}

// Contenitore top-level: JsonUtility non serializza una List da sola, serve wrapparla.
[System.Serializable]
class KeyframeLog
{
    public List<KeyframeLogEntry> entries = new List<KeyframeLogEntry>();
}

[System.Serializable]
public struct HeadPose
{
    public double timestamp;
    public Vector3 position;
    public Quaternion rotation;
}

public class HeadPoseList
{
    private readonly int N;
    private readonly HeadPose[] _headPoseHistory;
    private int _count = 0;
    private int head = -1;

    // Minimo 3: CalculateDeviation legge Last/Prev/PrevPrev.
    public HeadPoseList(int capacity)
    {
        N = Mathf.Max(3, capacity);
        _headPoseHistory = new HeadPose[N];
    }

    public void Push(HeadPose h)
    {
        head = (head+1)%N;
        _headPoseHistory[head] = h;
        if (_count < N) _count++;
    }

    public HeadPose Last() => _headPoseHistory[head];
    public HeadPose Prev() => _headPoseHistory[(head - 1 + N) % N];
    public HeadPose PrevPrev() => _headPoseHistory[(head - 2 + N) % N];

    public bool HasThree() => _count >= 3;
}