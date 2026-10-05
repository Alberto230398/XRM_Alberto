using System.Collections.Generic;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class DebugTexts : MonoBehaviour
{
    public Text poseText;
    public Text transformText;

    public PassthroughCameraAccess leftRGB;
    public Camera leftAnchor;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    private List<Vector3> offsets = new List<Vector3>();

void Update()
{
    var camPose = leftRGB.GetCameraPose();
    Vector3 offset = camPose.position - leftAnchor.transform.position;
    offsets.Add(offset);

    if (offsets.Count == 100)
    {
        Vector3 avg = Vector3.zero;
        foreach (var o in offsets) avg += o;
        avg /= offsets.Count;
        poseText.text = "Offset medio: " + avg;
        offsets.Clear();
    }
}
}
