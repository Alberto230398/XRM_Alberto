using System.Collections;
using UnityEngine;

public class VideoCompositor : MonoBehaviour, VideoInterface
{
    [SerializeField] MonoBehaviour sourceA;
    [SerializeField] MonoBehaviour sourceB;
    [SerializeField] Material sideBySideMat;

    private VideoInterface A => sourceA as VideoInterface;
    private VideoInterface B => sourceB as VideoInterface;

    private RenderTexture _rtA;
    private RenderTexture _rtB;
    private Material _blitMat;
    private Coroutine _compositeCoroutine;

    public RenderTexture initVideo(RenderTexture target)
    {
        _blitMat = Instantiate(sideBySideMat);

        _rtA = new RenderTexture(target.width / 2, target.height, 0, RenderTextureFormat.BGRA32);
        _rtB = new RenderTexture(target.width / 2, target.height, 0, RenderTextureFormat.BGRA32);
        _rtA.Create();
        _rtB.Create();

        A.initVideo(_rtA);
        B.initVideo(_rtB);

        _compositeCoroutine = StartCoroutine(Composite(target));
        return target;
    }

    private IEnumerator Composite(RenderTexture target)
    {
        while (true)
        {
            _blitMat.SetTexture("_RightTex", _rtB);
            Graphics.Blit(_rtA, target, _blitMat);
            yield return null;
        }
    }

    public void stop()
    {
        if (_compositeCoroutine != null)
            StopCoroutine(_compositeCoroutine);
        A.stop();
        B.stop();
    }
}