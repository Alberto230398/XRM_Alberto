using System.Collections;
using UnityEngine;

public class CompositeVideo : MonoBehaviour, VideoInterface
{

    [SerializeField] OldKeyFrameManager KeyFrameManager;
    public RenderTexture rt;
    Coroutine compositeCoroutine;


    public RenderTexture initVideo(RenderTexture target)
    {
        compositeCoroutine = StartCoroutine(Composite(target));
        return target;
    }


    public void stop()
    {
        if (compositeCoroutine!=null)
            StopCoroutine(compositeCoroutine);
    }

    IEnumerator Composite(RenderTexture target)
    {
        while (true)
        {
            Graphics.Blit(KeyFrameManager.GetCompositeRT(), target);
            yield return null;
        }
    }
}
