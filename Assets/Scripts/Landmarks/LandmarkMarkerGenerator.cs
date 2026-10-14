using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class LandmarkMarkerGenerator : MonoBehaviour
{
    [Header("Binary Source Data")]
    [Tooltip("Path relative to the project root in the Editor, or beside the built game's data folder in desktop builds.")]
    public string landmarkBinaryRelativePath =
        "Data/Landmarks/local_neighborhood_landmarks.bin";

    [Header("Marker Root")]
    [Tooltip("All generated landmark markers will be created under this object.")]
    public GameObject markersRoot;

    [Header("Marker Prefabs")]
    public GameObject cloudMarkerPrefab;
    public GameObject starMarkerPrefab;
    public GameObject nebulaMarkerPrefab;
    public GameObject clusterMarkerPrefab;
    public GameObject associationMarkerPrefab;

    [Header("Popup")]
    public LandmarkPopup landmarkPopup;

    [Header("Marker Settings")]
    [Tooltip("Visible size of each marker.")]
    public float markerScale = 0.1f;

    [Header("Marker Facing")]
    [Tooltip("XR headset camera transform. If empty, Camera.main is used.")]
    public Transform playerCamera;

    [Header("Collider Settings")]
    [Tooltip("Multiplier for the automatically fitted collider. 1 = fit the marker's maximum rendered dimension.")]
    [Min(0.01f)]
    public float colliderSizeMultiplier = 1f;

    [Tooltip("Fallback collider radius in world units when the prefab has no Renderer.")]
    [Min(0.001f)]
    public float fallbackColliderRadius = 0.05f;

    [Header("Nearby Star Separation")]
    [Tooltip("Stars closer than this distance to the origin are nudged outward.")]
    public float nearbyStarThreshold = 0.5f;

    [Tooltip("Extra distance added to nearby star markers so they do not overlap the Sun.")]
    public float nearbyStarOffset = 0.35f;

    private bool markersVisible = true;

    private void Start()
    {
        if (markersRoot == null)
        {
            Debug.LogError(
                "LandmarkMarkerGenerator: No Markers object assigned.");
            return;
        }

        markersVisible = markersRoot.activeSelf;

        if (playerCamera == null && Camera.main != null)
            playerCamera = Camera.main.transform;

        LoadBinaryAndGenerateMarkers();
    }

    private void LoadBinaryAndGenerateMarkers()
    {
        string binaryPath = ResolveBinaryPath();

        if (string.IsNullOrEmpty(binaryPath))
            return;

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(binaryPath);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"LandmarkMarkerGenerator: Could not read landmark binary:\n" +
                $"{binaryPath}\n{ex.Message}");
            return;
        }

        List<LandmarkBinaryRecord> records;

        try
        {
            records = LandmarkBinaryReader.Read(bytes);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"LandmarkMarkerGenerator: Invalid landmark binary: {ex.Message}");
            return;
        }

        int created = 0;

        foreach (LandmarkBinaryRecord record in records)
        {
            string typeCode = NormalizeTypeCode(record.Type);

            // The Local Bubble is represented by a separate translucent ellipsoid.
            if (typeCode == "bubble" ||
                record.Name.IndexOf(
                    "Local Bubble",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            GameObject markerPrefab = GetMarkerPrefab(typeCode);

            if (markerPrefab == null)
            {
                Debug.LogWarning(
                    $"LandmarkMarkerGenerator: No marker prefab assigned for " +
                    $"type '{record.Type}' (mapped to '{typeCode}') on " +
                    $"landmark '{record.Name}'.");
                continue;
            }

            Vector3 markerPosition =
                AdjustNearbyStarPosition(record.UnityPosition, typeCode);

            GameObject marker = Instantiate(
                markerPrefab,
                markerPosition,
                Quaternion.identity,
                markersRoot.transform);

            marker.transform.localScale = Vector3.one * markerScale;
            marker.name = $"{GetMarkerNamePrefix(typeCode)}_{record.Name}";

            ConfigureCollider(marker);

            ConfigureLandmarkInfo(
                marker,
                record.Name,
                typeCode,
                record.ShortDescription,
                record.ParagraphDescription);

            created++;
        }

        Debug.Log(
            $"LandmarkMarkerGenerator: Created {created:N0} landmark markers " +
            $"from {records.Count:N0} binary records.");
    }

    private string ResolveBinaryPath()
    {
#if UNITY_EDITOR
        string projectRoot = Path.GetFullPath(
            Path.Combine(Application.dataPath, ".."));

        return Path.GetFullPath(
            Path.Combine(projectRoot, landmarkBinaryRelativePath));
#else
        return Path.Combine(
            Application.persistentDataPath,
            landmarkBinaryRelativePath);
#endif
    }

    private static string NormalizeTypeCode(string type)
    {
        string value = (type ?? string.Empty).Trim().ToLowerInvariant();

        if (value.Contains("bubble") ||
            value.Contains("cavity in the ism"))
            return "bubble";

        if (value.Contains("association"))
            return "association";

        if (value == "star" ||
            value.Contains(" star") ||
            value.EndsWith("star") ||
            value.Contains("main-sequence") ||
            value.Contains("white dwarf") ||
            value.Contains("stellar") ||
            value.Contains("binary"))
        {
            return "star";
        }

        if (value.Contains("nebula") ||
            value.Contains("h ii") ||
            value.Contains("hii region"))
            return "nebula";

        if (value.Contains("cloud"))
            return "cloud";

        if (value.Contains("cluster"))
            return "cluster";

        return value;
    }

    public void ToggleMarkers()
    {
        SetMarkersVisible(!markersVisible);
    }

    public void SetMarkersVisible(bool visible)
    {
        markersVisible = visible;

        if (markersRoot != null)
            markersRoot.SetActive(visible);

        Debug.Log("Landmark markers visible: " + markersVisible);
    }

    private Vector3 AdjustNearbyStarPosition(
        Vector3 originalPosition,
        string typeCode)
    {
        if (typeCode != "star" ||
            originalPosition.magnitude >= nearbyStarThreshold)
        {
            return originalPosition;
        }

        if (originalPosition.sqrMagnitude < 0.0001f)
            return Vector3.right * nearbyStarOffset;

        return originalPosition +
               originalPosition.normalized * nearbyStarOffset;
    }

    private GameObject GetMarkerPrefab(string typeCode)
    {
        switch (typeCode)
        {
            case "cloud": return cloudMarkerPrefab;
            case "star": return starMarkerPrefab;
            case "nebula": return nebulaMarkerPrefab;
            case "cluster": return clusterMarkerPrefab;
            case "association": return associationMarkerPrefab;
            default: return null;
        }
    }

    private string GetMarkerNamePrefix(string typeCode)
    {
        switch (typeCode)
        {
            case "cloud": return "CloudMarker";
            case "star": return "StarMarker";
            case "nebula": return "NebulaMarker";
            case "cluster": return "ClusterMarker";
            case "association": return "AssociationMarker";
            default: return "LandmarkMarker";
        }
    }

    private void ConfigureCollider(GameObject marker)
    {
        SphereCollider sphereCollider =
            marker.GetComponent<SphereCollider>();

        // Remove other colliders so old, oversized colliders cannot
        // register selection hits.
        Collider[] existingColliders =
            marker.GetComponentsInChildren<Collider>(true);

        foreach (Collider existing in existingColliders)
        {
            if (existing == sphereCollider)
                continue;

            existing.enabled = false;
            Destroy(existing);
        }

        if (sphereCollider == null)
            sphereCollider = marker.AddComponent<SphereCollider>();

        Renderer[] renderers =
            marker.GetComponentsInChildren<Renderer>(true);

        bool hasBounds = false;
        Bounds combinedBounds = new Bounds();

        foreach (Renderer rendererComponent in renderers)
        {
            if (!hasBounds)
            {
                combinedBounds = rendererComponent.bounds;
                hasBounds = true;
            }
            else
            {
                combinedBounds.Encapsulate(rendererComponent.bounds);
            }
        }

        float worldRadius;

        if (hasBounds)
        {
            float largestDimension = Mathf.Max(
                combinedBounds.size.x,
                combinedBounds.size.y,
                combinedBounds.size.z);

            worldRadius =
                0.5f * largestDimension *
                Mathf.Max(0.01f, colliderSizeMultiplier);
        }
        else
        {
            worldRadius = fallbackColliderRadius;
        }

        // Convert the desired world-space radius to the collider's
        // local radius, accounting for marker and parent transforms.
        Vector3 scale = marker.transform.lossyScale;

        float maxScale = Mathf.Max(
            Mathf.Abs(scale.x),
            Mathf.Abs(scale.y),
            Mathf.Abs(scale.z));

        sphereCollider.radius =
            worldRadius / Mathf.Max(maxScale, 0.000001f);

        sphereCollider.center = Vector3.zero;
        sphereCollider.isTrigger = false;
        sphereCollider.enabled = true;
    }

    private void ConfigureLandmarkInfo(
        GameObject marker,
        string objectName,
        string typeCode,
        string shortDescription,
        string paragraphDescription)
    {
        LandmarkInfo info = marker.GetComponent<LandmarkInfo>();

        if (info == null)
        {
            Debug.LogWarning(
                $"{marker.name} is missing a LandmarkInfo component.");
            return;
        }

        info.objectName = objectName;
        info.typeCode = typeCode;
        info.shortDescription = shortDescription;
        info.paragraphDescription = paragraphDescription;
    }
}