using System.Collections;
using Meta.XR.EnvironmentDepth;
using Unity.XR.Oculus;
using UnityEngine;
using UnityEngine.UI;
using static Unity.XR.Oculus.Utils;

public class GetDepthMaps : MonoBehaviour
{
    [SerializeField] private EnvironmentDepthManager _environmentDepthManager;
    [SerializeField] private RawImage rawImage;
    private RenderTexture _previewRT;
    [SerializeField] Material depthMaterial;

    [SerializeField] Text timestampText;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
    }

    // Update is called once per frame
    void Update()
    {
        UpdateDepthPreview();
        var depthPose = OVRPlugin.GetNodePoseStateImmediate(OVRPlugin.Node.Head).Pose.ToOVRPose();
        //timestampText.text = "Pose: " + depthPose.position;
    }

    private Texture GetDepthTexture()
    {
        if (!_environmentDepthManager.IsDepthAvailable) return null;
        return Shader.GetGlobalTexture("_EnvironmentDepthTexture");

    }

    private void UpdateDepthPreview()
    {
        if (!_environmentDepthManager.IsDepthAvailable) return;

        var depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
        if (depthTex == null) return;

        if (_previewRT == null)
        {
            _previewRT = new RenderTexture(depthTex.width, depthTex.height, 0, RenderTextureFormat.BGRA32);
            _previewRT.Create();
            rawImage.texture = _previewRT;
        }

        Graphics.Blit(depthTex, _previewRT, depthMaterial);
    }

    private void RetrieveDepthData()
    {
        //Utils.SetupEnvironmentDepth(EnvironmentDepthCreateParams createParams)
        //Utils.ShutdownEnvironmentDepth()

        var data = Utils.GetEnvironmentDepthFrameDesc(0);

        Debug.Log("FOV Data: " + data.fovDownAngle + " " + data.fovLeftAngle + " " + data.fovRightAngle);

    }
}
