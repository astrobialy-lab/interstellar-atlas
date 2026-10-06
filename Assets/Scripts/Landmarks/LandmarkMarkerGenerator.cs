using System.Globalization;
using UnityEngine;

public class LandmarkMarkerGenerator : MonoBehaviour
{
    [Header("Source Data")]
    public TextAsset landmarkCsv;

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

    [Header("Coordinate System")]
    [Tooltip(
        "Optional heliocentric Galactic coordinate system toggled with landmarks."
    )]
    public GameObject coordinateSystemRoot;

    [Tooltip(
        "Optional Galactic orientation compass toggled with landmarks."
    )]
    public GameObject orientationCompassRoot;

    [Header("Marker Settings")]
    [Tooltip("Visible size of each marker.")]
    public float markerScale = 0.1f;

    [Tooltip("Local size of each marker's Box Collider.")]
    public Vector3 markerColliderSize =
        new Vector3(5f, 5f, 5f);

    [Header("Nearby Star Separation")]
    [Tooltip(
        "Stars closer than this distance to the origin are nudged outward."
    )]
    public float nearbyStarThreshold = 0.5f;

    [Tooltip(
        "Extra distance added to nearby star markers so they do not overlap the Sun."
    )]
    public float nearbyStarOffset = 0.35f;

    private bool markersVisible = true;

    private void Start()
    {
        if (markersRoot == null)
        {
            Debug.LogError(
                "LandmarkMarkerGenerator: " +
                "No Markers object assigned."
            );

            return;
        }

        markersVisible =
            markersRoot.activeSelf;

        GenerateLandmarkMarkers();
    }

    private void GenerateLandmarkMarkers()
    {
        if (landmarkCsv == null)
        {
            Debug.LogError(
                "No landmark CSV assigned."
            );

            return;
        }

        string[] lines =
            landmarkCsv.text.Split('\n');

        int created = 0;

        for (int i = 1; i < lines.Length; i++)
        {
            string line =
                lines[i].Trim();

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            string[] parts =
                line.Split(',');

            if (parts.Length < 8)
            {
                Debug.LogWarning(
                    $"Skipping malformed landmark row {i + 1}: {line}"
                );

                continue;
            }

            string name =
                parts[0].Trim();

            string typeCode =
                parts[2].Trim().ToLowerInvariant();

            // The Local Bubble is already represented
            // by the separate translucent ellipsoid.
            if (typeCode == "bubble")
            {
                continue;
            }

            GameObject markerPrefab =
                GetMarkerPrefab(typeCode);

            if (markerPrefab == null)
            {
                Debug.LogWarning(
                    $"No marker prefab assigned for type " +
                    $"'{typeCode}' on landmark '{name}'."
                );

                continue;
            }

            bool validX = float.TryParse(
                parts[3],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float x
            );

            bool validY = float.TryParse(
                parts[4],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float y
            );

            bool validZ = float.TryParse(
                parts[5],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float z
            );

            if (!validX || !validY || !validZ)
            {
                Debug.LogWarning(
                    $"Invalid coordinates for landmark '{name}'."
                );

                continue;
            }

            string shortDescription =
                parts[6].Trim();

            string paragraphDescription =
                parts[7].Trim();

            Vector3 markerPosition =
                new Vector3(
                    x,
                    y,
                    z
                );

            markerPosition =
                AdjustNearbyStarPosition(
                    markerPosition,
                    typeCode
                );

            GameObject marker =
                Instantiate(
                    markerPrefab,
                    markerPosition,
                    Quaternion.identity,
                    markersRoot.transform
                );

            marker.transform.localScale =
                Vector3.one * markerScale;

            marker.name =
                $"{GetMarkerNamePrefix(typeCode)}_{name}";

            ConfigureCollider(marker);

            ConfigureLandmarkInfo(
                marker,
                name,
                typeCode,
                shortDescription,
                paragraphDescription
            );

            created++;
        }

        //Debug.Log(
        //    $"Created landmark markers: {created}"
        //);
    }

    public void ToggleMarkers()
    {
        SetMarkersVisible(
            !markersVisible
        );
    }

    public void SetMarkersVisible(
        bool visible
    )
    {
        markersVisible =
            visible;

        // Toggle all generated markers at once.
        if (markersRoot != null)
        {
            markersRoot.SetActive(
                visible
            );
        }

        if (coordinateSystemRoot != null)
        {
            coordinateSystemRoot.SetActive(
                visible
            );
        }

        if (orientationCompassRoot != null)
        {
            orientationCompassRoot.SetActive(
                visible
            );
        }

        Debug.Log(
            "Landmark markers visible: " +
            markersVisible
        );
    }

    private Vector3 AdjustNearbyStarPosition(
        Vector3 originalPosition,
        string typeCode
    )
    {
        if (typeCode != "star")
        {
            return originalPosition;
        }

        if (
            originalPosition.magnitude >=
            nearbyStarThreshold
        )
        {
            return originalPosition;
        }

        if (
            originalPosition.sqrMagnitude <
            0.0001f
        )
        {
            return Vector3.right *
                nearbyStarOffset;
        }

        return originalPosition +
            originalPosition.normalized *
            nearbyStarOffset;
    }

    private GameObject GetMarkerPrefab(
        string typeCode
    )
    {
        switch (typeCode)
        {
            case "cloud":
                return cloudMarkerPrefab;

            case "star":
                return starMarkerPrefab;

            case "nebula":
                return nebulaMarkerPrefab;

            case "cluster":
                return clusterMarkerPrefab;

            case "association":
                return associationMarkerPrefab;

            default:
                return null;
        }
    }

    private string GetMarkerNamePrefix(
        string typeCode
    )
    {
        switch (typeCode)
        {
            case "cloud":
                return "CloudMarker";

            case "star":
                return "StarMarker";

            case "nebula":
                return "NebulaMarker";

            case "cluster":
                return "ClusterMarker";

            case "association":
                return "AssociationMarker";

            default:
                return "LandmarkMarker";
        }
    }

    private void ConfigureCollider(
        GameObject marker
    )
    {
        BoxCollider boxCollider =
            marker.GetComponent<BoxCollider>();

        if (boxCollider == null)
        {
            boxCollider =
                marker.AddComponent<BoxCollider>();
        }

        boxCollider.size =
            markerColliderSize;

        boxCollider.isTrigger = false;
    }

    private void ConfigureLandmarkInfo(
        GameObject marker,
        string objectName,
        string typeCode,
        string shortDescription,
        string paragraphDescription
    )
    {
        LandmarkInfo info =
            marker.GetComponent<LandmarkInfo>();

        if (info == null)
        {
            Debug.LogWarning(
                $"{marker.name} is missing a LandmarkInfo component."
            );

            return;
        }

        info.objectName =
            objectName;

        info.typeCode =
            typeCode;

        info.shortDescription =
            shortDescription;

        info.paragraphDescription =
            paragraphDescription;
    }
}