using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// HttpClient lifecycle management best practices:
// https://learn.microsoft.com/dotnet/fundamentals/networking/http/httpclient-guidelines#recommended-use

public class HttpManager : MonoBehaviour
{
    public static HttpManager httpMng { get; private set; }

    void Awake()
    {
        if (httpMng != null && httpMng != this)
        {
            Destroy(this);
            return;
        }
        httpMng = this;
    }

    [SerializeField] private string baseUrl = "https://jsonplaceholder.typicode.com";
    static readonly HttpClient client = new HttpClient(); // UNO, statico, riusato

    CancellationTokenSource cts;   // per fermare il worker in modo pulito
    Task workerTask;               // riferimento al task, per aspettarlo alla chiusura

    int keyframeIndex = 0;

    // I byte[] arrivano come argomenti: ogni task possiede i propri dati,
    // niente campi condivisi che la cattura successiva possa sovrascrivere.
    async Task UploadKeyframe(int index, byte[] rgb, byte[] rgbDepthRes, byte[] depth, byte[] DepthLowRes, byte[] sobelLowRes, string LeftPose, string RGBInstrinsics, string reprojMatrix, string zBuf, string depthRes)
    {
        try
        {
            // Sistemo le foto
            HttpContent textureContent = new ByteArrayContent(rgb);
            HttpContent rgbDepthResContent = new ByteArrayContent(rgbDepthRes);
            HttpContent depthContent = new ByteArrayContent(depth); // Se hai anche una texture di profondità
            HttpContent DepthLowResContent = new ByteArrayContent(DepthLowRes); // Se hai anche una texture di profondità
            HttpContent sobelLowResContent = new ByteArrayContent(sobelLowRes); // Se hai anche una texture di profondità


            // Sistemo i JSON
            HttpContent poseContent = new StringContent(LeftPose, System.Text.Encoding.UTF8, "application/json");
            HttpContent rgbIntrinsicsContent = new StringContent(RGBInstrinsics, System.Text.Encoding.UTF8, "application/json");
            HttpContent reprojMatrixContent = new StringContent(reprojMatrix, System.Text.Encoding.UTF8, "application/json");
            HttpContent zBufContent = new StringContent(zBuf, System.Text.Encoding.UTF8, "application/json");
            HttpContent depthResContent = new StringContent(depthRes, System.Text.Encoding.UTF8, "application/json");

            MultipartFormDataContent multipartContent = new MultipartFormDataContent();
            multipartContent.Add(textureContent, "files", "LeftRGB.png");
            multipartContent.Add(rgbDepthResContent, "files", "RGBDepthResolution.exr");
            multipartContent.Add(depthContent, "files", "Depth.exr");
            multipartContent.Add(DepthLowResContent, "files", "DepthLowRes.exr");
            multipartContent.Add(sobelLowResContent, "files", "SobelLowRes.exr");
            multipartContent.Add(poseContent, "files", "LeftPose.json");
            multipartContent.Add(rgbIntrinsicsContent, "files", "RGBIntrinsics.json");
            multipartContent.Add(reprojMatrixContent, "files", "Reprojection.json");
            multipartContent.Add(zBufContent, "files", "ZBufferParams.json");
            multipartContent.Add(depthResContent, "files", "DepthResolution.json");

            var textureResp = await client.PostAsync($"{baseUrl}/keyframe/{index}", multipartContent);
            textureResp.EnsureSuccessStatusCode();

            string textureBody = await textureResp.Content.ReadAsStringAsync();
            Debug.Log($"[HTTP] keyframe {index} OK {(int)textureResp.StatusCode} — risposta: {textureBody}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[HTTP] keyframe {index} Errore: {e.Message}");
        }
    }

    void Start()
    {
        cts = new CancellationTokenSource();
    }

    public void SetRGBTexture(byte[] rgbText, byte[] rgbDepthResText, byte[] depthText, byte[] DepthLowRes, byte[] sobelLowRes, string LeftPose, string RGBInstrinsics, string reprojMatrix, string zBuf, string depthRes)
    {
        int index = keyframeIndex;   // fotografa l'indice ADESSO, in una locale
        keyframeIndex++;

        // indice e byte[] passati come argomenti: la lambda cattura valori congelati
        Task.Run(() => UploadKeyframe(index, rgbText, rgbDepthResText, depthText, DepthLowRes, sobelLowRes, LeftPose, RGBInstrinsics, reprojMatrix, zBuf, depthRes));
    }

    // Riceve il path della cartella keyframe da KeyFrameManager, la zippa e la
    // invia in streaming all'endpoint /scan.
    // async void: e' un "fire and forget" innescato dalla pressione del tasto A;
    // il chiamante (RetrieveAndSendData) non lo awaita. Il try/catch interno cattura
    // tutte le eccezioni, cosi' l'async void non lascia errori non osservati.
    public async void SendKeyframesFolderAsync(string folderPath)
    {
        Debug.Log($"[HTTP] SendKeyframesFolderAsync ricevuto path: {folderPath}");

        // Letto QUI, sul main thread, prima di qualsiasi await (Application.* va toccato dal main).
        string zipPath = $"{Application.persistentDataPath}/keyframes.zip";

        try
        {
            // Zip pesante (ordine del GB): eseguito su un thread di background.
            // "await Task.Run(...)" cede subito il main thread -> niente freeze del visore.
            await Task.Run(() =>
            {
                // CreateFromDirectory lancia se il file esiste gia': cancella lo zip precedente.
                if (System.IO.File.Exists(zipPath))
                    System.IO.File.Delete(zipPath);

                System.IO.Compression.ZipFile.CreateFromDirectory(folderPath, zipPath);
            });

            Debug.Log($"[HTTP] zip creato: {zipPath}");

            // StreamContent legge lo zip dal disco a blocchi mentre lo invia:
            // NON carica l'intero GB in RAM (a differenza di ByteArrayContent).
            using (var stream = System.IO.File.OpenRead(zipPath))
            using (var content = new StreamContent(stream))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

                var resp = await client.PostAsync($"{baseUrl}/scan", content);
                resp.EnsureSuccessStatusCode();

                string body = await resp.Content.ReadAsStringAsync();
                Debug.Log($"[HTTP] scan inviata OK {(int)resp.StatusCode} — risposta: {body}");
            }

            // Pulizia: non accumulare uno zip da GB a ogni scan.
            System.IO.File.Delete(zipPath);
        }
        catch (Exception e)
        {
            Debug.LogError($"[HTTP] SendKeyframesFolderAsync errore: {e.Message}");
        }
    }

}