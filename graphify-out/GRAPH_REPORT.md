# Graph Report - Assets  (2026-07-10)

## Corpus Check
- Corpus is ~27,683 words - fits in a single context window. You may not need a graph.

## Summary
- 756 nodes · 1063 edges · 39 communities (33 shown, 6 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 14 edges (avg confidence: 0.81)
- Token cost: 27,000 input · 1,800 output

## Community Hubs (Navigation)
- Camera Viewer & WebRTC
- QR Code Detection (MRUK)
- Start Scene & Laser Pointer
- Passthrough Video Pipeline
- Keyframe Capture & Depth
- Camera-to-World Streaming
- Sentis Object Detection
- Detection Marker Spawning
- Hand Input & Spatial Anchors
- Debug UI Controls
- QR Marker Display & Raycast
- Color Picker
- QR Frame Scanning
- Brightness Estimation
- OpenAI Audio & TTS
- Speech-to-Text (OpenAI)
- Detection UI Menu
- Depth Mesh Generation
- Stereo Camera Mapping
- Sentis Inference UI
- Depth Map Retrieval
- Passthrough Manager
- Sentis Inference Runner
- OpenAI Image Vision
- Bounding Box Rendering
- Camera Debug & Permissions
- OpenAI Model Config
- Voice Command Capture
- Sentis Model Editor
- Environment Raycasting
- 2D Bounds Visualizer
- WAV Audio Encoding
- Non-Max Suppression
- Inference Labels
- ZXing Build Config
- WebRTC Build Config
- YOLO Class Set
- MIT License
- Montserrat Font License

## God Nodes (most connected - your core abstractions)
1. `DebugUIBuilder` - 33 edges
2. `KeyFrameManager` - 24 edges
3. `ColorPicker` - 23 edges
4. `DetectionManager` - 21 edges
5. `SentisInferenceRunManager` - 20 edges
6. `SttManager` - 20 edges
7. `CameraToWorldCameraCanvas` - 19 edges
8. `SentisInferenceUiManager` - 19 edges
9. `LaserPointer` - 19 edges
10. `ObjectDetector` - 19 edges

## Surprising Connections (you probably didn't know these)
- `YOLO Object Detection Class Set (SentisInference Model)` --semantically_similar_to--> `YOLO Object Detection Class Set (ObjectDetection Resources)`  [INFERRED] [semantically similar]
  PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Model/SentisYoloClasses.txt → Samples/2 ObjectDetection/Resources/SentisYoloClasses.txt
- `VideoCompositor` --references--> `VideoInterface`  [EXTRACTED]
  VideoCompositor.cs → Scripts/VideoInterface.cs
- `VideoManager` --references--> `VideoInterface`  [EXTRACTED]
  VideoManager.cs → Scripts/VideoInterface.cs
- `DetectionManager` --references--> `DetectionSpawnMarkerAnim`  [EXTRACTED]
  PassthroughCameraApiSamples/MultiObjectDetection/DetectionManager/Scripts/DetectionManager.cs → PassthroughCameraApiSamples/MultiObjectDetection/DetectionManager/Scripts/DetectionSpawnMarkerAnim.cs
- `DetectionManager` --references--> `SentisInferenceUiManager`  [EXTRACTED]
  PassthroughCameraApiSamples/MultiObjectDetection/DetectionManager/Scripts/DetectionManager.cs → PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceUiManager.cs

## Import Cycles
- None detected.

## Communities (39 total, 6 thin omitted)

### Community 0 - "Camera Viewer & WebRTC"
Cohesion: 0.04
Nodes (29): PassthroughCameraSamples.CameraViewer, QuestCameraKit.WebRTC, PassthroughCameraSamples.ShaderSample, MeshRenderer, MonoBehaviour, PassthroughCameraAccess, RenderTexture, PassthroughBridge (+21 more)

### Community 1 - "QR Code Detection (MRUK)"
Cohesion: 0.06
Nodes (24): Canvas, Meta.XR.MRUtilityKitSamples.QRCodeDetection, Image, MRUK, MRUKTrackable, RectTransform, TMP_Text, QRCode (+16 more)

### Community 2 - "Start Scene & Laser Pointer"
Cohesion: 0.05
Nodes (23): PassthroughCameraSamples.StartScene, LaserBeamBehaviorEnum, OVRCursor, OVRInputModule, OVROverlay, OVRCameraRig, HandedInputSelector, bool (+15 more)

### Community 3 - "Passthrough Video Pipeline"
Cohesion: 0.06
Nodes (21): bool, Coroutine, IEnumerator, Material, PassthroughCameraAccess, RenderTexture, PassthroughVideo, RenderTexture (+13 more)

### Community 4 - "Keyframe Capture & Depth"
Cohesion: 0.09
Nodes (29): BeforeRenderOrder, DateTime, Matrix4x4, CapturedKeyframe, bool, Camera, CameraIntrinsics, EnvironmentDepthManager (+21 more)

### Community 5 - "Camera-to-World Streaming"
Cohesion: 0.07
Nodes (22): byte, PassthroughCameraSamples.CameraToWorld, HttpListener, object, OVRPose, IEnumerator, int, PassthroughCameraAccess (+14 more)

### Community 6 - "Sentis Object Detection"
Cohesion: 0.07
Nodes (23): Model, BackendType, Coroutine, float, IEnumerator, int, ModelAsset, PassthroughCameraAccess (+15 more)

### Community 7 - "Detection Marker Spawning"
Cohesion: 0.06
Nodes (18): PassthroughCameraSamples.MultiObjectDetection, OVRCameraRig, TextMesh, Transform, Vector3, DetectionSpawnMarkerAnim, Color, float (+10 more)

### Community 8 - "Hand Input & Spatial Anchors"
Cohesion: 0.09
Nodes (15): Hand, HandFingerPinch, HandState, OVRSpatialAnchor, AudioSource, bool, IEnumerator, List (+7 more)

### Community 9 - "Debug UI Controls"
Cohesion: 0.11
Nodes (16): OnClick, OnSlider, OnToggleValueChange, bool, Dictionary, float, GameObject, int (+8 more)

### Community 10 - "QR Marker Display & Raycast"
Cohesion: 0.10
Nodes (22): Plane, BuildWorldRay(), CleanupInactiveMarkers(), ComputeSensorCrop(), Pose, Ray, Rect, Vector2 (+14 more)

### Community 11 - "Color Picker"
Cohesion: 0.12
Nodes (16): Color32, NativeArray, Renderer, ColorPicker, Camera, Color, EnvironmentRaycastManager, float (+8 more)

### Community 12 - "QR Frame Scanning"
Cohesion: 0.14
Nodes (23): height, Result, AcquireFrameAsync(), CaptureFrame, CameraIntrinsics, Pose, RenderTexture, string (+15 more)

### Community 13 - "Brightness Estimation"
Cohesion: 0.10
Nodes (14): PassthroughCameraSamples.BrightnessEstimation, float, int, string, Text, UnityEvent, BrightnessEstimationDebugger, float (+6 more)

### Community 14 - "OpenAI Audio & TTS"
Cohesion: 0.11
Nodes (10): QuestCameraKit.OpenAI, AudioSource, bool, IEnumerator, AudioPlayer, Action, IEnumerator, TtsVoice (+2 more)

### Community 15 - "Speech-to-Text (OpenAI)"
Cohesion: 0.14
Nodes (11): Dropdown, AudioClip, bool, Button, float, int, string, Task (+3 more)

### Community 16 - "Detection UI Menu"
Cohesion: 0.16
Nodes (8): AudioSource, bool, GameObject, IEnumerator, int, Text, UnityEvent, DetectionUiMenuManager

### Community 17 - "Depth Mesh Generation"
Cohesion: 0.19
Nodes (6): EnvironmentDepthManager, PassthroughCameraAccess, Quaternion, Texture, Vector3, MeshGenerator

### Community 18 - "Stereo Camera Mapping"
Cohesion: 0.22
Nodes (8): CameraPositionType, QuestCameraKit.CameraMapping, float, IEnumerator, int, Material, PassthroughCameraAccess, StereoCameraMappingController

### Community 19 - "Sentis Inference UI"
Cohesion: 0.19
Nodes (9): float, int, PassthroughCameraAccess, RectTransform, string, Transform, UnityEvent, BoundingBoxData (+1 more)

### Community 20 - "Depth Map Retrieval"
Cohesion: 0.17
Nodes (7): EnvironmentDepthManager, Material, RawImage, RenderTexture, Text, Texture, GetDepthMaps

### Community 21 - "Passthrough Manager"
Cohesion: 0.17
Nodes (9): bool, Coroutine, IEnumerator, Material, PassthroughCameraAccess, RawImage, RenderTexture, Text (+1 more)

### Community 22 - "Sentis Inference Runner"
Cohesion: 0.18
Nodes (8): BackendType, float, ModelAsset, PassthroughCameraAccess, TextAsset, Vector2Int, Worker, SentisInferenceRunManager

### Community 23 - "OpenAI Image Vision"
Cohesion: 0.27
Nodes (4): AudioSource, Texture2D, UnityEvent, ImageOpenAIConnector

### Community 24 - "Bounding Box Rendering"
Cohesion: 0.22
Nodes (8): BoundingBoxData, boundingBox, classId, List, Pose, Vector2, Vector3, Vector4

### Community 25 - "Camera Debug & Permissions"
Cohesion: 0.18
Nodes (7): PassthroughCameraSamples, DebuglevelEnum, LogType, DebuglevelEnum, PassthroughCameraDebugger, RuntimeInitializeOnLoadMethod, RequestPermissionsOnce

### Community 26 - "OpenAI Model Config"
Cohesion: 0.22
Nodes (8): float, string, OpenAICommandMode, OpenAIVisionModel, OpenAIVisionModelExtensions, TtsModel, TtsModelExtensions, TtsPayload

### Community 27 - "Voice Command Capture"
Cohesion: 0.33
Nodes (4): IEnumerator, PassthroughCameraAccess, Texture2D, VoiceCommandHandler

### Community 28 - "Sentis Model Editor"
Cohesion: 0.24
Nodes (6): PassthroughCameraSamples.MultiObjectDetection.Editor, Editor, float, string, SentisModelEditorConverter, SentisInferenceRunManager

### Community 29 - "Environment Raycasting"
Cohesion: 0.22
Nodes (5): EnvironmentRaycastManager, Ray, string, Vector3, EnvironmentRayCastSampleManager

### Community 30 - "2D Bounds Visualizer"
Cohesion: 0.31
Nodes (6): LineRenderer, MRUKTrackable, Rect, RectTransform, Vector3, Bounded2DVisualizer

### Community 31 - "WAV Audio Encoding"
Cohesion: 0.62
Nodes (3): MemoryStream, AudioClip, SaveWav

### Community 32 - "Non-Max Suppression"
Cohesion: 0.33
Nodes (5): boundingBox, classId, List, Tensor, Vector4

## Knowledge Gaps
- **13 isolated node(s):** `PassthroughCameraSamples.CameraViewer`, `PassthroughCameraSamples.MultiObjectDetection.Editor`, `DebuglevelEnum`, `PassthroughCameraSamples.ShaderSample`, `LaserBeamBehaviorEnum` (+8 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **6 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `DebugUIBuilder` connect `Debug UI Controls` to `Camera Viewer & WebRTC`, `Start Scene & Laser Pointer`?**
  _High betweenness centrality (0.126) - this node is a cross-community bridge._
- **Why does `KeyFrameManager` connect `Keyframe Capture & Depth` to `Camera Viewer & WebRTC`?**
  _High betweenness centrality (0.095) - this node is a cross-community bridge._
- **Why does `ColorPicker` connect `Color Picker` to `Camera Viewer & WebRTC`?**
  _High betweenness centrality (0.064) - this node is a cross-community bridge._
- **What connects `PassthroughCameraSamples.CameraViewer`, `PassthroughCameraSamples.MultiObjectDetection.Editor`, `DebuglevelEnum` to the rest of the system?**
  _13 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Camera Viewer & WebRTC` be split into smaller, more focused modules?**
  _Cohesion score 0.041666666666666664 - nodes in this community are weakly interconnected._
- **Should `QR Code Detection (MRUK)` be split into smaller, more focused modules?**
  _Cohesion score 0.05656565656565657 - nodes in this community are weakly interconnected._
- **Should `Start Scene & Laser Pointer` be split into smaller, more focused modules?**
  _Cohesion score 0.05094130675526024 - nodes in this community are weakly interconnected._