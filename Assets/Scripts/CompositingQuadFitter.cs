using UnityEngine;

/// <summary>
/// Tiene il Quad del compositing sempre centrato e "a schermo pieno" davanti alla CompositingCam,
/// come uno schermo attaccato davanti agli occhi. Evita che un nudge in editor o uno spostamento
/// a runtime lo mandino fuori quadro: posizione, rotazione e scala vengono ricalcolate dalla camera.
///
/// Uso:
///  - Attacca questo componente al GameObject "Quad".
///  - Assegna CompositingCam (o il KeyFrameManager, da cui legge renderCamera).
///  - In editor: tasto destro sul componente -> "Fit Now" per rimetterlo davanti subito.
///  - A runtime si riallinea ogni frame in LateUpdate.
/// </summary>
[RequireComponent(typeof(MeshRenderer))]
public class CompositingQuadFitter : MonoBehaviour
{
    [Tooltip("La camera che renderizza il Quad (CompositingCam). Se vuota, prova a ricavarla da KeyFrameManager.renderCamera.")]
    [SerializeField] private Camera compositingCamera;

    [Tooltip("Fallback per trovare la camera: se compositingCamera e' vuota, usa KeyFrameManager.renderCamera.")]
    [SerializeField] private OldKeyFrameManager keyFrameManager;

    [Tooltip("Distanza del Quad davanti alla camera, in metri.")]
    [SerializeField] private float distance = 1f;

    [Tooltip("Se ON scala il Quad per riempire tutto il frame della camera. Se OFF mantiene la scala attuale.")]
    [SerializeField] private bool fillFrustum = true;

    [Tooltip("Se ON riallinea ogni frame (utile se la CompositingCam si muove). Se OFF allinea solo all'avvio.")]
    [SerializeField] private bool trackEveryFrame = true;

    private void OnEnable()
    {
        ResolveCamera();
        Fit();
    }

    private void LateUpdate()
    {
        if (trackEveryFrame) Fit();
    }

    private void ResolveCamera()
    {
        if (compositingCamera == null && keyFrameManager != null)
            compositingCamera = keyFrameManager.renderCamera;
    }

    [ContextMenu("Fit Now")]
    private void Fit()
    {
        if (compositingCamera == null)
        {
            ResolveCamera();
            if (compositingCamera == null)
            {
                Debug.LogWarning("[CompositingQuadFitter] Nessuna CompositingCam assegnata (ne' via KeyFrameManager.renderCamera).");
                return;
            }
        }

        var camT = compositingCamera.transform;

        // Posizione: esattamente davanti alla camera, lungo il suo forward.
        transform.position = camT.position + camT.forward * distance;

        // Rotazione: il Quad deve mostrare la faccia frontale alla camera. Allineandolo alla
        // rotazione della camera si riproduce la configurazione "identita' vs identita'" che
        // gia' funzionava (la faccia del Quad guarda verso la camera).
        transform.rotation = camT.rotation;

        if (!fillFrustum) return;

        // Scala per riempire il frustum alla distanza data.
        // fieldOfView e' l'FOV verticale. L'aspect e' quello della RT di destinazione
        // (blendedTexture): e' il frame che viene effettivamente streammato.
        var rt = compositingCamera.targetTexture;
        float aspect = rt != null ? (float)rt.width / rt.height : compositingCamera.aspect;

        float h = 2f * distance * Mathf.Tan(compositingCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float w = h * aspect;
        transform.localScale = new Vector3(w, h, 1f);
    }
}
