using System.Collections.Generic;
using Meta.XR;
using TMPro;
using UnityEngine;

public class QuestYoloLabels : MonoBehaviour
{
    [Header("Scene references")]
    public QuestYoloClient client;
    public EnvironmentRaycastManager raycastManager;
    public Transform viewer; // Camera Rig / TrackingSpace / CenterEyeAnchor.
    public TMP_Text labelPrefab; // Optional world-space text prefab.

    [Header("Placement")]
    [Range(0, 1)] public float minimumConfidence = 0.3f;
    public float maximumDistance = 5f;
    public float maximumResponseAge = 1.5f;
    public float labelLifetime = 1.5f;
    public float matchingDistance = 0.35f;
    public float surfaceOffset = 0.025f;
    public float labelHeight = 0.08f;
    public float smoothing = 12f;
    public int maximumLabels = 20;
    public bool showConfidence = true;
    public bool debugRays;

    private class Label
    {
        public TMP_Text text;
        public string objectClass;
        public Vector3 anchor, target;
        public float lastSeen;
        public int matchedFrame;
    }

    private readonly List<Label> labels = new List<Label>();
    private int lastFrame = -1;
    private QuestYoloClient subscribedClient;

    private void OnEnable()
    {
        if (client == null || raycastManager == null || viewer == null)
        {
            Debug.LogError("QuestYoloLabels: assign Client, Raycast Manager and Viewer.", this);
            return;
        }
        subscribedClient = client;
        subscribedClient.DetectionsReceived += OnDetections;
    }

    private void OnDisable()
    {
        if (subscribedClient != null)
            subscribedClient.DetectionsReceived -= OnDetections;
        subscribedClient = null;
        foreach (Label label in labels)
            if (label.text != null) Destroy(label.text.gameObject);
        labels.Clear();
        lastFrame = -1;
    }

    private void OnDetections(QuestYoloClient.YoloResponse response)
    {
        if (response.id <= lastFrame || !EnvironmentRaycastManager.IsSupported ||
            !raycastManager.isActiveAndEnabled ||
            !client.TryGetFrameSnapshot(response.id, out QuestYoloClient.FrameSnapshot snapshot)) return;
        lastFrame = response.id;
        float now = Time.realtimeSinceStartup;
        if (now - snapshot.capturedAt > maximumResponseAge) return;

        foreach (QuestYoloClient.Detection detection in response.detections ??
                 System.Array.Empty<QuestYoloClient.Detection>())
        {
            if (detection == null || string.IsNullOrEmpty(detection.label) ||
                detection.confidence < minimumConfidence) continue;
            float x = (detection.x1 + detection.x2) * 0.5f;
            float y = (detection.y1 + detection.y2) * 0.5f;
            if (float.IsNaN(x) || float.IsNaN(y) || x < 0 || x > snapshot.width ||
                y < 0 || y > snapshot.height) continue;

            // This uses the saved image projection and capture-time RGB camera pose.
            Ray ray = snapshot.PixelToWorldRay(x, y);
            if (debugRays) Debug.DrawRay(ray.origin, ray.direction * maximumDistance, Color.cyan, 0.5f);
            if (!raycastManager.Raycast(ray, out EnvironmentRaycastHit hit, maximumDistance))
                continue; // No depth hit: do not invent a world position.

            Label label = FindMatch(detection.label, hit.point, response.id);
            Vector3 position = hit.point + hit.normal * surfaceOffset + Vector3.up * labelHeight;
            if (label == null)
            {
                if (labels.Count >= maximumLabels) continue;
                TMP_Text text = CreateText();
                if (text == null) continue;
                label = new Label { text = text, objectClass = detection.label };
                label.text.transform.position = position;
                labels.Add(label);
            }
            label.anchor = hit.point;
            label.target = position;
            label.lastSeen = now;
            label.matchedFrame = response.id;
            label.text.text = showConfidence
                ? $"{detection.label} {detection.confidence:P0}" : detection.label;
        }
    }

    private Label FindMatch(string objectClass, Vector3 point, int frame)
    {
        Label best = null;
        float closest = matchingDistance * matchingDistance;
        foreach (Label label in labels)
        {
            if (label.objectClass != objectClass || label.matchedFrame == frame ||
                Time.realtimeSinceStartup - label.lastSeen > labelLifetime) continue;
            float distance = (label.anchor - point).sqrMagnitude;
            if (distance < closest) { closest = distance; best = label; }
        }
        return best;
    }

    private TMP_Text CreateText()
    {
        if (labelPrefab != null)
        {
            TMP_Text text = Instantiate(labelPrefab);
            text.gameObject.SetActive(true);
            text.richText = false;
            return text;
        }
        if (TMP_Settings.defaultFontAsset == null)
        {
            Debug.LogError("Import TMP Essential Resources, or assign a Label Prefab.", this);
            return null;
        }
        var go = new GameObject("YOLO 3D Label");
        var generated = go.AddComponent<TextMeshPro>();
        generated.font = TMP_Settings.defaultFontAsset;
        generated.fontSize = 3;
        generated.alignment = TextAlignmentOptions.Center;
        generated.color = Color.white;
        generated.richText = false;
        generated.rectTransform.sizeDelta = new Vector2(3, 0.6f);
        go.transform.localScale = Vector3.one * 0.1f;
        return generated;
    }

    private void LateUpdate()
    {
        float now = Time.realtimeSinceStartup;
        for (int i = labels.Count - 1; i >= 0; i--)
        {
            Label label = labels[i];
            if (label.text == null || now - label.lastSeen > labelLifetime)
            {
                if (label.text != null) Destroy(label.text.gameObject);
                labels.RemoveAt(i);
                continue;
            }
            Transform t = label.text.transform;
            t.position = Vector3.Lerp(t.position, label.target, 1f - Mathf.Exp(-smoothing * Time.unscaledDeltaTime));
            if (viewer != null)
            {
                Vector3 direction = t.position - viewer.position;
                if (direction.sqrMagnitude > 0.0001f)
                    t.rotation = Quaternion.LookRotation(direction, Vector3.up);
            }
        }
    }
}
