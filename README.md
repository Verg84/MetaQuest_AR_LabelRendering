# Meta Quest AR Label Rendering

A mixed reality application that streams Meta Quest camera images to a Python YOLO detector and displays object labels at estimated real-world positions in Unity.

The project combines object detection with capture-time camera metadata and environment depth. Detection results are projected into world space using the camera pose associated with the processed image, helping compensate for headset movement while inference runs.

## Contents

1. [Technologies](#1-technologies)
2. [Server side](#2-server-side)
3. [Client side](#3-client-side)
4. [Computing World Space Positions](#4-computing-world-space-positions)

## 1. Technologies

| Technology | Purpose |
|---|---|
| Meta Quest 3 / Quest 3S | RGB camera access and mixed reality display |
| Unity 6 and C# | Headset application and world-space label rendering |
| Meta XR SDK and MRUK | Passthrough camera access and environment raycasting |
| OpenXR | XR runtime integration |
| NativeWebSocket | Bidirectional communication between Unity and Python |
| Python and FastAPI | WebSocket server |
| Uvicorn | ASGI server hosting FastAPI |
| Ultralytics YOLO11 | Object detection using `yolo11n.pt` by default |
| OpenCV and NumPy | JPEG decoding and annotated camera display |
| TextMeshPro | Readable 3D text labels |

## 2. Server side

The Python server receives camera frames through the `/camera_detector` WebSocket endpoint. Each frame is sent as JSON text containing a base64-encoded JPEG, a frame ID, and the RGB camera pose.

For each processed frame, the server:

1. Decodes the JPEG into an image.
2. Runs YOLO object detection.
3. Displays the annotated image in an OpenCV window.
4. Returns object classes, confidence scores, bounding boxes, image dimensions, and the original frame ID and camera pose to Unity.

Inference runs in a single background worker so it does not block WebSocket communication. When frames arrive faster than inference can process them, the newest waiting frame replaces the older one.

### Run the server

Use Python 3.10 or newer. From the repository root:

```powershell
cd Server
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install fastapi "uvicorn[standard]" ultralytics opencv-python numpy "websockets>=14,<16"
.\.venv\Scripts\python.exe server.py
```

The configured server port is **8765**. The default YOLO weights download on first launch if they are not already available. Use a desktop environment with a graphical display for the OpenCV window. Keep one server worker because camera and display state are held in one process.

For a custom Ultralytics detection model, set its path before launching:

```powershell
$env:YOLO_MODEL = "C:\path\to\best.pt"
.\.venv\Scripts\python.exe server.py
```

### Message format

Unity sends:

```json
{
  "id": 42,
  "image": "<base64-encoded JPEG>",
  "camPos": {"x": 0.0, "y": 1.5, "z": 0.0},
  "camRot": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}
}
```

The server responds:

```json
{
  "id": 42,
  "camPos": {"x": 0.0, "y": 1.5, "z": 0.0},
  "camRot": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0},
  "imageWidth": 640,
  "imageHeight": 480,
  "detections": [
    {
      "label": "cup",
      "confidence": 0.92,
      "x1": 100.0,
      "y1": 120.0,
      "x2": 200.0,
      "y2": 250.0,
      "cx": 150.0,
      "cy": 185.0
    }
  ]
}
```

Bounding-box coordinates are pixels in the transmitted image with a top-left origin. A successful response with no detected objects contains an empty `detections` array.

## 3. Client side

The Unity application uses one bidirectional WebSocket connection for camera frames and detection responses. Labels are computed and rendered locally on the headset.

### Camera streaming: `QuestYoloClient`

The client reads the real-world RGB texture from `PassthroughCameraAccess`, resizes it while preserving its aspect ratio, encodes it as JPEG, and sends the JSON message to Python.

For every sent frame, it also saves a local snapshot indexed by the frame ID:

- RGB camera position and rotation associated with the image timestamp.
- Image timestamp and local capture time.
- Transmitted image width and height.
- Camera projection rays.
- Whether vertical flipping was applied.

Capture occurs at the end of the rendered frame. When a response arrives, the client checks that its image dimensions match the saved snapshot and exposes the detections to the label renderer.

### Label rendering: `QuestYoloLabels`

The renderer retrieves the matching frame snapshot, reconstructs a world-space ray for each detection, and queries `EnvironmentRaycastManager` for a physical surface intersection.

Labels are created only when a raycast succeeds. They face the viewer, move smoothly as their estimated positions update, and expire when detections stop. Nearby detections of the same class are matched to existing labels to reduce repeated creation. This is basic spatial matching rather than persistent object tracking.

### Unity setup

1. Open the project with the Unity version specified in `ProjectSettings/ProjectVersion.txt`.
2. Allow Unity to restore the package dependencies.
3. Ensure the scene contains Camera Rig, Passthrough, and Passthrough Camera Access components.
4. Enable camera access and configure runtime camera and spatial-data permissions. Use one permission-request mechanism.
5. Assign `PassthroughCameraAccess` to `QuestYoloClient`.
6. Set the client URL to `ws://YOUR_PC_IP:8765/camera_detector`. Use the server PC's reachable local IPv4 address, not `localhost` on Quest.
7. Add one enabled `EnvironmentRaycastManager`.
8. Assign the client, raycast manager, and `CenterEyeAnchor` viewer transform to `QuestYoloLabels`.
9. Import TMP Essential Resources. A label prefab is optional; the renderer can create TextMeshPro labels automatically.
10. Check Android settings with Meta's Project Setup Tool, then build and run on the headset. Grant the requested permissions.

The PC and headset must be able to reach each other on the local network. Allow Python through the appropriate Windows Firewall profile. Start with 5 FPS, 640-pixel image width, and JPEG quality 70. Rebuild the installed app after changing the client URL in Unity.

## 4. Computing World Space Positions

### A. Detection center to normalized image coordinates

For a bounding box `(x1, y1, x2, y2)`, compute its center:

```text
cx = (x1 + x2) / 2
cy = (y1 + y2) / 2
```

Normalize using the width and height saved for that exact transmitted frame:

```text
u = cx / capturedImageWidth
v = 1 - cy / capturedImageHeight
```

The vertical conversion changes YOLO's top-left origin to Unity's bottom-left origin. If the transmitted image was flipped vertically, reverse that flip using `v = 1 - v`.

### B. Reconstruct the camera-space direction

At capture time, the client obtains projection rays at viewport coordinates `(0,0)`, `(1,0)`, and `(0,1)` using the camera API with an identity pose. Each direction is divided by its Z component to put it on the `z = 1` projection plane.

For normalized detection coordinates `(u,v)`, the camera-space direction is:

```text
localDirection = ray00
               + u * (ray10 - ray00)
               + v * (ray01 - ray00)
```

Saving these rays preserves the calibration and crop mapping used by the captured image, even if camera settings change before the response returns.

### C. Apply the capture-time camera pose

Transform the direction into world space using the saved RGB camera rotation, and use its saved position as the ray origin:

```csharp
Ray ray = new Ray(
    snapshot.cameraPose.position,
    snapshot.cameraPose.rotation * localDirection.normalized
);
```

The response's frame ID selects the snapshot. The calculation uses the pose associated with the processed image, rather than the headset pose when YOLO finishes. This helps compensate for head movement during network and inference delay.

### D. Resolve depth and position the label

Environment raycasting supplies the missing distance along the ray:

```csharp
if (raycastManager.Raycast(ray, out EnvironmentRaycastHit hit))
{
    Vector3 position = hit.point
        + hit.normal * surfaceOffset
        + Vector3.up * labelHeight;
}
```

The label is placed near the surface intersection, with offsets to lift it above and away from the surface. Its rotation follows the current viewer position for readability; its placement ray still uses the captured frame's pose.

### Accuracy and limitations

- YOLO's bounding box gives a direction, not a 3D distance. Environment depth provides the estimated surface position.
- Raycasting uses depth available when the result is processed; it does not retrieve a historical depth map for the image timestamp.
- Moving objects can shift during inference, even when head movement is compensated.
- A bounding-box center can intersect background geometry rather than the object itself.
- Unavailable depth, expired capture metadata, or overly delayed responses can prevent a label from appearing.
- Labels are temporary world-space estimates, not persistent spatial anchors. Similar nearby objects can swap labels with basic class-and-distance matching.

The prototype uses unencrypted WebSockets without authentication on a trusted local network. Do not commit credentials, virtual environments, generated Unity files, or downloaded model weights.
