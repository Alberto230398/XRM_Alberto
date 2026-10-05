using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class OldKeyFrameManager : MonoBehaviour
{
    public Material blendMat;
    public Texture2D keyFrameTexture;
    public RenderTexture blendedTexture;

    public Camera renderCamera;

    public RawImage blendedRawImage;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        StartCoroutine(BlendAndSaveKeyFrame());
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    IEnumerator BlendAndSaveKeyFrame()
    {
        yield return new WaitForSeconds(3f);
        SaveKeyFrame();
    }


    void SaveKeyFrame()
    {

        Debug.Log("Saving Keyframe...");
        string dir = $"{Application.persistentDataPath}/keyframes/";
        //string dir = "/Users/albertomerletti/Desktop/keyframes/";
        //System.IO.Directory.CreateDirectory(dir);

        renderCamera.Render();
        blendedRawImage.texture = blendedTexture;

        // 2. Poi imposta l'active per la lettura
        Texture2D blendedTexture2D = new Texture2D(blendedTexture.width, blendedTexture.height, TextureFormat.RGBA32, false);
        RenderTexture.active = blendedTexture;
        blendedTexture2D.ReadPixels(new Rect(0, 0, blendedTexture.width, blendedTexture.height), 0, 0);
        blendedTexture2D.Apply();

        // 3. Ripristina sempre l'active (importante!)
        RenderTexture.active = null;

        //byte[] bytes = blendedTexture2D.EncodeToPNG();
        //System.IO.File.WriteAllBytes($"{dir}/KeyFrame_{System.DateTime.Now:yyyyMMdd_HHmmss}.png", bytes);
    }

    public RenderTexture GetCompositeRT()
    {
        return blendedTexture;
    }
}
