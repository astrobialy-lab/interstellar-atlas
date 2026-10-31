using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

public class ClusterMarkerGenerator : MonoBehaviour
{
    [Header("Binary Source Data")]
    [Tooltip("Directory inside the Data directory.")]
    public string clusterDataDirectory =
        "Clusters/HuntReffert2024";

    [Header("Marker Root")]
    [Tooltip("All generated cluster markers will be created under this object.")]
    public GameObject markersRoot;

    [Header("Marker Prefab")]
    public GameObject clusterMarkerPrefab;

    [Header("Marker Settings")]
    [Tooltip("Base size of each marker.")]
    [Min(0.001f)]
    public float markerScale = 1.0f;

    [Header("Mass-Based Size")]
    [Tooltip("Mass in solar masses mapped to the minimum size.")]
    [Min(0.001f)]
    public float smallestMassSolar = 100f;

    [Tooltip("Mass in solar masses mapped to the maximum size.")]
    [Min(0.001f)]
    public float largestMassSolar = 10000f;

    [Tooltip("Size multiplier for low-mass clusters.")]
    [Min(0.01f)]
    public float smallestMassScale = 0.65f;

    [Tooltip("Size multiplier for high-mass clusters.")]
    [Min(0.01f)]
    public float largestMassScale = 2f;

    [Tooltip("Size multiplier when mass is unavailable.")]
    [Min(0.01f)]
    public float unknownMassScale = 1f;

    [Header("Filtering")]
    public ClusterFilter clusterFilter;
    private ClusterDatabase database;
    private readonly List<GameObject> generatedMarkers = new List<GameObject>();
    private readonly List<ClusterDatabase.ClusterRecord> generatedRecords = new List<ClusterDatabase.ClusterRecord>();
    private int lastFilterRevision = int.MinValue;

    [Header("Collider Settings")]
    [Tooltip("Multiplier for the automatically fitted collider.")]
    [Min(0.01f)]
    public float colliderSizeMultiplier = 1f;

    [Tooltip("Fallback collider radius when the prefab has no Renderer.")]
    [Min(0.001f)]
    public float fallbackColliderRadius = 0.05f;

    private bool markersVisible = true;

    private void Start()
    {
        if (markersRoot == null)
        {
            Debug.LogError(
                "GaiaClusterMarkerGenerator: No Markers object assigned.");
            return;
        }

        if (clusterMarkerPrefab == null)
        {
            Debug.LogError(
                "GaiaClusterMarkerGenerator: No cluster marker prefab assigned.");
            return;
        }

        markersVisible = markersRoot.activeSelf;

        LoadAndGenerateMarkers();
    }

    private void LoadAndGenerateMarkers()
    {
        ClusterDatabase database;

        try
        {
            // Reuse the existing database to avoid loading a second
            // copy of the membership catalogue into memory.
            database = clusterFilter != null
                ? clusterFilter.Database
                : null;

            if (database == null)
            {
                database = ClusterDatabase.Load(
                    DataPath.Root,
                    clusterDataDirectory);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "ClusterMarkerGenerator: Could not load cluster database.\n" +
                ex.Message);
            return;
        }

        int created = 0;

        for (int i = 0; i < database.ClusterCount; i++)
        {
            ClusterDatabase.ClusterRecord record =
                database.GetClusterAt(i);

            Vector3 position = new Vector3(
                record.x,
                record.y,
                record.z
            );

            GameObject marker = Instantiate(
                clusterMarkerPrefab,
                position,
                Quaternion.identity,
                markersRoot.transform);

            float massScale =
                GetMassScaleMultiplier(record.massTot);

            marker.transform.localScale =
                Vector3.one * markerScale * massScale;

            marker.name = $"ClusterMarker_{record.id}";

            ConfigureCollider(marker);
            ApplyAgeColor(marker, record);
            ConfigureClusterInfo(marker, record);

            generatedMarkers.Add(marker);
            generatedRecords.Add(record);

            created++;
        }

        Debug.Log(
            $"GaiaClusterMarkerGenerator: Created {created:N0} " +
            $"cluster markers from {database.ClusterCount:N0} records.");
    }

    private void ConfigureClusterInfo(
        GameObject marker,
        ClusterDatabase.ClusterRecord record)
    {
        MarkerInfo info =
            marker.GetComponent<MarkerInfo>();

        if (info == null)
        {
            Debug.LogWarning(
                $"{marker.name} is missing a MarkerInfo component.");
            return;
        }

        info.objectName = record.name;
        info.typeCode = "cluster";
        info.shortDescription =
            BuildShortDescription(record);
        info.paragraphDescription =
            BuildParagraphDescription(record);
    }

    private static string BuildShortDescription(
        ClusterDatabase.ClusterRecord record)
    {
        StringBuilder description = new StringBuilder();

        description.AppendLine($"ID: {record.id}");
        description.AppendLine($"Type code: {record.type}");

        description.AppendLine("Catalogue counts:");
        description.AppendLine(
            $"  n: {record.n.ToString("N0", CultureInfo.InvariantCulture)}");
        description.AppendLine(
            $"  nt: {record.nt.ToString("N0", CultureInfo.InvariantCulture)}");

        string distance = FormatDistance(record.dist50);
        if (distance != null)
            description.AppendLine($"Median distance: {distance}");

        string age = FormatAge(record.logAge50);
        if (age != null)
            description.AppendLine($"Median age: {age}");

        if (IsPositive(record.massTot))
        {
            description.AppendLine(
                $"Estimated total mass: {FormatNumber(record.massTot)} solar masses");
        }

        return description.ToString().TrimEnd();
    }

    private static string BuildParagraphDescription(
        ClusterDatabase.ClusterRecord record)
    {
        StringBuilder description = new StringBuilder();

        // General information
        description.AppendLine("Cluster information:");
        description.AppendLine($"  Name: {record.name}");
        description.AppendLine($"  ID: {record.id}");
        description.AppendLine($"  Catalogue type code: {record.type}");

        description.AppendLine("  Catalogue counts:");
        description.AppendLine(
            $"    n: {record.n.ToString("N0", CultureInfo.InvariantCulture)}");
        description.AppendLine(
            $"    nt: {record.nt.ToString("N0", CultureInfo.InvariantCulture)}");

        // Galactic coordinates
        if (IsFinite(record.glon) &&
            record.glon >= 0f &&
            record.glon <= 360f &&
            IsFinite(record.glat) &&
            record.glat >= -90f &&
            record.glat <= 90f)
        {
            description.AppendLine();
            description.AppendLine("Galactic coordinates:");
            description.AppendLine(
                $"  Longitude: {FormatNumber(record.glon)} degrees");
            description.AppendLine(
                $"  Latitude: {FormatNumber(record.glat)} degrees");
        }

        // Galactic Cartesian coordinates (Sun-centered)
        description.AppendLine();
        description.AppendLine("Galactic Cartesian coordinates (Sun-centered):");
        description.AppendLine($"  X: {FormatNumber(record.x)} pc");
        description.AppendLine($"  Y: {FormatNumber(record.z)} pc");
        description.AppendLine($"  Z: {FormatNumber(record.y)} pc");

        // Distance and age
        AppendDistanceEstimates(description, record);
        AppendAgeEstimates(description, record);

        // Mass
        if (IsPositive(record.massTot) ||
            IsPositive(record.massJ))
        {
            description.AppendLine();
            description.AppendLine("Mass estimates:");

            if (IsPositive(record.massTot))
            {
                description.AppendLine(
                    $"  Total mass: {FormatNumber(record.massTot)} solar masses");
            }

            if (IsPositive(record.massJ))
            {
                description.AppendLine(
                    $"  Alternative mass (massJ): {FormatNumber(record.massJ)} solar masses");
            }
        }

        // Radii
        if (IsPositive(record.r50pc) ||
            IsPositive(record.rcpc) ||
            IsPositive(record.rtpc) ||
            IsPositive(record.rtotpc))
        {
            description.AppendLine();
            description.AppendLine("Catalogue radii:");

            AddPositiveValue(description, "r50", record.r50pc, "pc");
            AddPositiveValue(description, "rc", record.rcpc, "pc");
            AddPositiveValue(description, "rt", record.rtpc, "pc");
            AddPositiveValue(description, "rtot", record.rtotpc, "pc");
        }

        // Proper motion
        if (IsFinite(record.pmRA) &&
            IsFinite(record.pmDE) &&
            record.pmRA > -900f &&
            record.pmDE > -900f)
        {
            description.AppendLine();
            description.AppendLine("Proper motion:");
            description.AppendLine(
                $"  Right ascension (pmRA): {FormatNumber(record.pmRA)} mas/year");
            description.AppendLine(
                $"  Declination (pmDE): {FormatNumber(record.pmDE)} mas/year");
        }

        // Parallax
        if (IsFinite(record.plx) && record.plx > 0f)
        {
            description.AppendLine();
            description.AppendLine(
                $"Parallax: {FormatNumber(record.plx)} mas");
        }

        // Radial velocity
        if (IsFinite(record.rv) && record.rv > -900f)
        {
            description.AppendLine();
            description.AppendLine(
                $"Radial velocity: {FormatNumber(record.rv)} km/s");
        }

        // Extinction
        if (IsFinite(record.av50) && record.av50 >= 0f)
        {
            description.AppendLine();
            description.AppendLine(
                $"Median visual extinction (Av): {FormatNumber(record.av50)} magnitudes");
        }

        return description.ToString().TrimEnd();
    }

    private static void AppendDistanceEstimates(
        StringBuilder description,
        ClusterDatabase.ClusterRecord record)
    {
        string d16 = FormatDistance(record.dist16);
        string d50 = FormatDistance(record.dist50);
        string d84 = FormatDistance(record.dist84);

        if (d16 == null && d50 == null && d84 == null)
            return;

        description.AppendLine();
        description.AppendLine("Distance estimates:");

        if (d16 != null)
            description.AppendLine($"  16th percentile: {d16}");

        if (d50 != null)
            description.AppendLine($"  Median: {d50}");

        if (d84 != null)
            description.AppendLine($"  84th percentile: {d84}");
    }

    private static void AppendAgeEstimates(
        StringBuilder description,
        ClusterDatabase.ClusterRecord record)
    {
        string a16 = FormatAge(record.logAge16);
        string a50 = FormatAge(record.logAge50);
        string a84 = FormatAge(record.logAge84);

        if (a16 == null && a50 == null && a84 == null)
            return;

        description.AppendLine();
        description.AppendLine("Age estimates:");

        if (a16 != null)
            description.AppendLine($"  16th percentile: {a16}");

        if (a50 != null)
            description.AppendLine($"  Median: {a50}");

        if (a84 != null)
            description.AppendLine($"  84th percentile: {a84}");
    }

    private static void AddPositiveValue(
        StringBuilder description,
        string name,
        float value,
        string unit)
    {
        if (!IsPositive(value))
            return;

        description.AppendLine(
            $"  {name}: {FormatNumber(value)} {unit}");
    }

    private static string FormatDistance(float distancePc)
    {
        if (!IsPositive(distancePc))
            return null;

        if (distancePc >= 1000f)
        {
            return FormatNumber(distancePc / 1000f) + " kpc";
        }

        return FormatNumber(distancePc) + " pc";
    }

    private static string FormatAge(float logAge)
    {
        // logAge is assumed to be log10(age in years).
        if (!IsFinite(logAge) || logAge <= 0f || logAge > 12.5f)
            return null;

        double ageGyr = Math.Pow(10.0, logAge - 9.0);

        if (double.IsNaN(ageGyr) ||
            double.IsInfinity(ageGyr) ||
            ageGyr <= 0.0)
        {
            return null;
        }

        if (ageGyr < 1.0)
        {
            double ageMyr = ageGyr * 1000.0;

            return ageMyr.ToString(
                "0.##",
                CultureInfo.InvariantCulture) + " Myr";
        }

        return ageGyr.ToString(
            "0.##",
            CultureInfo.InvariantCulture) + " Gyr";
    }

    private static string FormatNumber(float value)
    {
        if (!IsFinite(value))
            return "unavailable";

        return value.ToString(
            "0.##",
            CultureInfo.InvariantCulture);
    }

    private static bool IsPositive(float value)
    {
        return IsFinite(value) && value > 0f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) &&
               !float.IsInfinity(value);
    }

    private void ConfigureCollider(GameObject marker)
    {
        SphereCollider sphereCollider =
            marker.GetComponent<SphereCollider>();

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

    public void ToggleMarkers()
    {
        SetMarkersVisible(!markersVisible);
    }

    public void SetMarkersVisible(bool visible)
    {
        markersVisible = visible;

        if (markersRoot != null)
            markersRoot.SetActive(visible);

        Debug.Log(
            "Cluster markers visible: " + markersVisible);
    }

    private void Update()
    {
        if (clusterFilter == null || database == null)
            return;

        // Refilter only when the settings have changed.
        if (lastFilterRevision != clusterFilter.Revision)
        {
            ApplyMarkerFilter();
        }
    }

    private void ApplyMarkerFilter()
    {
        if (clusterFilter == null || database == null)
            return;

        GaiaClusterFilterSettings settings =
            clusterFilter.CreateSnapshot();

        for (int i = 0; i < generatedMarkers.Count; i++)
        {
            GameObject marker = generatedMarkers[i];

            if (marker == null)
                continue;

            bool visible =
                !settings.enabled ||
                PassesClusterFilter(
                    generatedRecords[i],
                    settings);

            marker.SetActive(visible);
        }

        lastFilterRevision = clusterFilter.Revision;
    }

    private bool PassesClusterFilter(
        ClusterDatabase.ClusterRecord cluster,
        GaiaClusterFilterSettings settings)
    {
        if (settings.useAgeRange)
        {
            if (!IsFinite(cluster.logAge50) ||
                cluster.logAge50 < settings.minLogAge ||
                cluster.logAge50 > settings.maxLogAge)
            {
                return false;
            }
        }

        if (settings.useDistanceRange)
        {
            if (!IsFinite(cluster.dist50) ||
                cluster.dist50 < settings.minDistancePc ||
                cluster.dist50 > settings.maxDistancePc)
            {
                return false;
            }
        }

        if (settings.useTotalMassRange)
        {
            if (!IsFinite(cluster.massTot) ||
                cluster.massTot < settings.minTotalMass ||
                cluster.massTot > settings.maxTotalMass)
            {
                return false;
            }
        }

        if (settings.useTypeCode &&
            cluster.type != settings.typeCode)
        {
            return false;
        }

        // These settings describe a star's membership in a cluster.
        // Keep the marker if at least one member meets the criteria.
        bool usesMembershipFilters =
            settings.useMembershipProbability ||
            settings.requireInRj ||
            settings.requireInRt;

        if (usesMembershipFilters &&
            !database.ClusterHasMatchingMembership(
                cluster.id,
                settings))
        {
            return false;
        }

        return true;
    }

    private float GetMassScaleMultiplier(float massSolar)
    {
        if (!IsPositive(massSolar))
            return unknownMassScale;

        float minMass = Mathf.Max(0.001f, smallestMassSolar);
        float maxMass = Mathf.Max(minMass * 1.001f, largestMassSolar);

        float t = Mathf.InverseLerp(
            Mathf.Log10(minMass),
            Mathf.Log10(maxMass),
            Mathf.Log10(massSolar));

        return Mathf.Lerp(
            smallestMassScale,
            largestMassScale,
            t);
    }

    private static Color GetAgeColor(float logAge)
    {
        // Gray for missing or invalid age data.
        if (!IsFinite(logAge) || logAge <= 0f || logAge > 12.5f)
            return new Color(0.65f, 0.65f, 0.65f);

        Color veryYoung = new Color(1f, 0.20f, 0.18f); // Red
        Color young = new Color(1f, 0.55f, 0.15f); // Orange
        Color middle = new Color(1f, 0.88f, 0.25f); // Yellow
        Color old = new Color(0.25f, 0.85f, 0.70f); // Teal
        Color veryOld = new Color(0.30f, 0.52f, 1f); // Blue

        if (logAge < 7f)
            return Color.Lerp(
                veryYoung, young,
                Mathf.InverseLerp(6f, 7f, logAge));

        if (logAge < 8f)
            return Color.Lerp(
                young, middle,
                Mathf.InverseLerp(7f, 8f, logAge));

        if (logAge < 9f)
            return Color.Lerp(
                middle, old,
                Mathf.InverseLerp(8f, 9f, logAge));

        if (logAge < 9.69897f)
            return Color.Lerp(
                old, veryOld,
                Mathf.InverseLerp(9f, 9.69897f, logAge));

        return veryOld;
    }

    private static void ApplyAgeColor(
        GameObject marker,
        ClusterDatabase.ClusterRecord record)
    {
        Color ageColor = GetAgeColor(record.logAge50);

        SpriteRenderer[] sprites =
            marker.GetComponentsInChildren<SpriteRenderer>(true);

        foreach (SpriteRenderer sprite in sprites)
        {
            Color current = sprite.color;

            sprite.color = new Color(
                ageColor.r,
                ageColor.g,
                ageColor.b,
                current.a);
        }
    }
}