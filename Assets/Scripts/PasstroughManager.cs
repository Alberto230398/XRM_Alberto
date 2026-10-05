using System.Collections;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;


public class PassthroughManager : MonoBehaviour
{
    [SerializeField] PassthroughCameraAccess passthroughCameraLeft;
    [SerializeField] PassthroughCameraAccess passthroughCameraRight;
    [SerializeField] Material sideBySideMat;
    [SerializeField] bool useStereoPassthrough;

    private RenderTexture target;

    [SerializeField] Text timestampText;

    [SerializeField] private RawImage rawImage;
    Coroutine blitCoroutine;
    private Material _blitMat;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        StartCoroutine(BlitPassthrough());
        timestampText.text = "Timestamp: ";
    }

    // Update is called once per frame
    void Update()
    {
    }

    /*public RenderTexture ShowPasstrough(RenderTexture target)
    {
        StartCoroutine(BlitPassthrough(target));
        return target;
    }*/

    private IEnumerator BlitPassthrough()
    {
        _blitMat = Instantiate(sideBySideMat);

        while (true)
        {
            if (passthroughCameraLeft.IsPlaying)
            {
                Texture left = passthroughCameraLeft.GetTexture();

                if (left != null)
                {
                    if (target==null)
                    {
                        target = new RenderTexture(left.width, left.height, 0, RenderTextureFormat.BGRA32);
                        target.Create();
                    }
                    rawImage.texture = target;
                    Graphics.Blit(left, target);
                    //timestampText.text = "Timestamp: " + passthroughCameraLeft.Timestamp.ToString("HH:mm:ss");
                    //timestampText.text = "Pose: " + passthroughCameraLeft.GetCameraPose().position;
                }
            }
            yield return null;
        }
    }
}
