using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Procedurally draws wireframe representations of nearby Galactic structures.
///
/// Project coordinate convention:
///   Heliocentric Galactic (X, Y, Z) pc -> Unity (X, Z, Y)
///   Unity (X, Y, Z) -> Galactic (X, Z, Y)
///   1 Unity unit = 1 pc
///
/// Structures:
///   - Per-Tau Shell: published 78 pc spherical reference model, with optional
///     small illustrative irregularity and editable missing patches.
///   - Tau Ring: published ellipse, including its published 3D orientation.
///   - Radcliffe Wave: Alves et al. (2020) published damped-sinusoid fit.
///
/// Labels are generated as world-space TextMeshPro objects and billboard toward
/// the viewer. They use the same color as their corresponding structure.
/// </summary>
public class GalacticStructureVisualizer : MonoBehaviour
{
    [System.Serializable]
    public class PerTauGap
    {
        [Range(0f, 360f)] public float longitudeDegrees;
        [Range(-90f, 90f)] public float latitudeDegrees;
        [Min(0.1f)] public float longitudeHalfWidthDegrees = 18f;
        [Min(0.1f)] public float latitudeHalfWidthDegrees = 14f;
    }

    [Header("Visibility")]
    [Tooltip("Master visibility switch. You can also call SetVisible(true/false) from another script.")]
    public bool showStructures = true;

    [Tooltip("When enabled, all structures are shown only while the landmark source is visible.")]
    public bool followLandmarkVisibility = true;

    [Tooltip("Assign LandmarkMarkerGenerator if the landmark toggle enables/disables that component.")]
    public LandmarkMarkerGenerator landmarkMarkerGenerator;

    [Tooltip("Alternatively, assign the GameObject controlled by the landmark toggle. This takes priority over Landmark Marker Generator.")]
    public GameObject landmarksVisibilitySource;

    [Header("Labels")]
    [Tooltip("Optional camera used to orient the 3D labels toward the viewer. If empty, Camera.main is used.")]
    public Transform viewerCamera;

    [Tooltip("Optional TMP font asset. If empty, TextMeshPro uses its configured default font.")]
    public TMP_FontAsset labelFont;

    [Min(0.1f)] public float labelFontSize = 80f;
    [Min(0f)] public float labelOffsetAboveStructurePc = 12f;

    public bool showPerTauLabel = true;
    public string perTauLabelText = "Per-Tau Shell";

    public bool showTauRingLabel = true;
    public string tauRingLabelText = "Tau Ring";

    public bool showRadcliffeLabel = true;
    public string radcliffeLabelText = "Radcliffe Wave";

    [Header("Per-Tau Shell")]
    public bool showPerTauShell = true;
    public Color perTauColor = new Color(0.12f, 0.88f, 0.64f, 1f);
    [Min(0.01f)] public float perTauLineWidthPc = 0.45f;
    [Range(4, 32)] public int perTauLongitudeCount = 14;
    [Range(2, 24)] public int perTauLatitudeRingCount = 9;
    [Range(16, 256)] public int perTauSegmentsPerLine = 96;

    [Tooltip("Illustrative radial variation only. The published 78 pc sphere is the reference model; this is NOT a reconstruction of the measured dust isosurface.")]
    [Range(0f, 12f)] public float perTauRadialVariationPc = 4f;

    [Tooltip("Enable hand-placed gaps to suggest the incomplete appearance of the published dust isosurfaces. Their angular positions are illustrative, not digitized measurements.")]
    public bool showIllustrativePerTauGaps = false;

    [Tooltip("Optional angular patches to omit from the wireframe. Longitude is around the Galactic XY plane; latitude is measured from that plane.")]
    public PerTauGap[] perTauMissingPatches = new PerTauGap[0];

    [Header("Tau Ring")]
    public bool showTauRing = true;
    public Color tauRingColor = new Color(1f, 0.72f, 0.18f, 1f);
    [Min(0.01f)] public float tauRingLineWidthPc = 0.55f;
    [Range(32, 256)] public int tauRingSegments = 128;
    [Tooltip("Published semimajor axis (Bialy et al. 2021), in parsecs.")]
    [Min(0.1f)] public float tauRingSemiMajorAxisPc = 39f;
    [Tooltip("Published semiminor axis (Bialy et al. 2021), in parsecs.")]
    [Min(0.1f)] public float tauRingSemiMinorAxisPc = 26f;

    [Header("Radcliffe Wave")]
    public bool showRadcliffeWave = true;
    public Color radcliffeColor = new Color(1f, 0.27f, 0.22f, 1f);
    [Min(0.01f)] public float radcliffeLineWidthPc = 0.65f;
    [Range(64, 2048)] public int radcliffeSamples = 512;

    [Tooltip("Alves et al. (2020) best-fit quadratic centerline anchor 0, heliocentric Galactic XYZ in pc.")]
    public Vector3 radcliffeAnchor0 = new Vector3(-910f, -860f, -30f);
    [Tooltip("Alves et al. (2020) best-fit quadratic centerline anchor 1, heliocentric Galactic XYZ in pc.")]
    public Vector3 radcliffeAnchor1 = new Vector3(-270f, -20f, -10f);
    [Tooltip("Alves et al. (2020) best-fit quadratic centerline anchor 2, heliocentric Galactic XYZ in pc.")]
    public Vector3 radcliffeAnchor2 = new Vector3(290f, 1400f, 30f);

    [Tooltip("Published median amplitude A (Alves et al. 2020), in pc.")]
    [Min(0f)] public float radcliffeAmplitudePc = 160f;
    [Tooltip("Published median period P (Alves et al. 2020), in pc.")]
    [Min(1f)] public float radcliffePeriodPc = 3560f;
    [Tooltip("Published median phase phi (Alves et al. 2020), in radians.")]
    public float radcliffePhaseRadians = 2.89f;
    [Tooltip("Published median period-decay parameter gamma (Alves et al. 2020). Must be positive.")]
    [Min(0.001f)] public float radcliffeGamma = 0.50f;
    [Tooltip("Published median amplitude-damping parameter delta (Alves et al. 2020). Must be non-negative.")]
    [Min(0.001f)] public float radcliffeDelta = 6.68f;

    private const float PerTauCenterGalX = -190f;
    private const float PerTauCenterGalY = 65f;
    private const float PerTauCenterGalZ = -84f;
    private const float PerTauRadiusPc = 78f;

    private const float TauRingCenterGalX = -174f;
    private const float TauRingCenterGalY = 1f;
    private const float TauRingCenterGalZ = -44f;

    // Published Tau Ring basis vectors in heliocentric Galactic Cartesian coordinates.
    // The printed values are rounded; the second vector is orthogonalized against
    // the first so the semimajor and semiminor axes remain perpendicular.
    private static readonly Vector3 TauRingPublishedE1 = new Vector3(0.923f, 0.353f, -0.154f);
    private static readonly Vector3 TauRingPublishedE2 = new Vector3(0.005f, 0.302f, 0.841f);

    private GameObject generatedRoot;
    private GameObject perTauRoot;
    private GameObject tauRingRoot;
    private GameObject radcliffeRoot;

    private Material perTauMaterial;
    private Material tauRingMaterial;
    private Material radcliffeMaterial;

    private readonly List<TextMeshPro> generatedLabels = new List<TextMeshPro>();

    private void OnEnable()
    {
        if (!Application.isPlaying)
            return;

        if (generatedRoot == null)
            GenerateStructures();

        ApplyVisibility();
    }

    private void LateUpdate()
    {
        ApplyVisibility();
        UpdateLabelBillboards();
    }

    private void OnDisable()
    {
        if (generatedRoot != null)
            generatedRoot.SetActive(false);
    }

    private void OnDestroy()
    {
        ClearGeneratedObjects();
    }

    /// <summary>Rebuilds all structures using the current Inspector settings.</summary>
    [ContextMenu("Regenerate Structures")]
    public void GenerateStructures()
    {
        ClearGeneratedObjects();

        generatedRoot = new GameObject("Generated Galactic Structures");
        generatedRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        generatedRoot.transform.localScale = Vector3.one;

        perTauRoot = CreateStructureRoot("Per-Tau Shell");
        tauRingRoot = CreateStructureRoot("Tau Ring");
        radcliffeRoot = CreateStructureRoot("Radcliffe Wave");

        perTauMaterial = CreateLineMaterial(perTauColor, "Per-Tau Wireframe Material");
        tauRingMaterial = CreateLineMaterial(tauRingColor, "Tau Ring Material");
        radcliffeMaterial = CreateLineMaterial(radcliffeColor, "Radcliffe Wave Material");

        // Generate all three once so their Inspector toggles can be changed at runtime.
        GeneratePerTauShell();
        GenerateTauRing();
        GenerateRadcliffeWave();

        ApplyVisibility();
        UpdateLabelBillboards();
    }

    /// <summary>Set the overall visibility from another component or input handler.</summary>
    public void SetVisible(bool visible)
    {
        showStructures = visible;
        ApplyVisibility();
    }

    private GameObject CreateStructureRoot(string objectName)
    {
        GameObject root = new GameObject(objectName);
        root.transform.SetParent(generatedRoot.transform, false);
        return root;
    }

    private void ApplyVisibility()
    {
        if (generatedRoot == null)
            return;

        bool visible = showStructures;

        if (followLandmarkVisibility)
        {
            bool landmarksVisible = true;

            if (landmarksVisibilitySource != null)
            {
                landmarksVisible = landmarksVisibilitySource.activeInHierarchy;
            }
            else if (landmarkMarkerGenerator != null)
            {
                landmarksVisible = landmarkMarkerGenerator.enabled &&
                                   landmarkMarkerGenerator.gameObject.activeInHierarchy;
            }

            visible &= landmarksVisible;
        }

        if (generatedRoot.activeSelf != visible)
            generatedRoot.SetActive(visible);

        // Child roots let each structure be toggled independently at runtime.
        SetActiveIfChanged(perTauRoot, showPerTauShell);
        SetActiveIfChanged(tauRingRoot, showTauRing);
        SetActiveIfChanged(radcliffeRoot, showRadcliffeWave);

        // Labels are children of their structure roots and follow their visibility.
        SetLabelVisibility("Per-Tau Shell", showPerTauLabel);
        SetLabelVisibility("Tau Ring", showTauRingLabel);
        SetLabelVisibility("Radcliffe Wave", showRadcliffeLabel);
    }

    private void SetLabelVisibility(string structureName, bool isVisible)
    {
        for (int i = 0; i < generatedLabels.Count; i++)
        {
            TextMeshPro label = generatedLabels[i];
            if (label == null)
                continue;

            if (label.gameObject.name == structureName + " Label")
                SetActiveIfChanged(label.gameObject, isVisible);
        }
    }

    private static void SetActiveIfChanged(GameObject target, bool active)
    {
        if (target != null && target.activeSelf != active)
            target.SetActive(active);
    }

    private void GeneratePerTauShell()
    {
        if (perTauRoot == null || perTauMaterial == null)
            return;

        Vector3 centerGalactic = new Vector3(
            PerTauCenterGalX,
            PerTauCenterGalY,
            PerTauCenterGalZ
        );

        int segments = Mathf.Max(16, perTauSegmentsPerLine);
        int longitudeCount = Mathf.Max(4, perTauLongitudeCount);
        int latitudeRingCount = Mathf.Max(2, perTauLatitudeRingCount);

        // Meridians: arcs running from the south pole to the north pole.
        for (int longitudeIndex = 0; longitudeIndex < longitudeCount; longitudeIndex++)
        {
            float longitude = 2f * Mathf.PI * longitudeIndex / longitudeCount;
            List<Vector3> points = new List<Vector3>(segments + 1);
            List<bool> visible = new List<bool>(segments + 1);

            for (int i = 0; i <= segments; i++)
            {
                float latitude = Mathf.Lerp(-Mathf.PI * 0.5f, Mathf.PI * 0.5f, i / (float)segments);
                Vector3 point = PerTauShellPoint(centerGalactic, latitude, longitude);
                points.Add(GalacticToUnity(point));
                visible.Add(!IsInsidePerTauGap(latitude * Mathf.Rad2Deg, longitude * Mathf.Rad2Deg));
            }

            CreateVisibleLineRuns("Per-Tau Meridian " + longitudeIndex,
                points, visible, perTauLineWidthPc, perTauMaterial, perTauRoot, false);
        }

        // Latitude rings. The poles are omitted because the meridians meet there.
        for (int ringIndex = 1; ringIndex <= latitudeRingCount; ringIndex++)
        {
            float latitude = Mathf.Lerp(
                -Mathf.PI * 0.5f,
                Mathf.PI * 0.5f,
                ringIndex / (float)(latitudeRingCount + 1)
            );

            List<Vector3> points = new List<Vector3>(segments);
            List<bool> visible = new List<bool>(segments);

            for (int i = 0; i < segments; i++)
            {
                float longitude = 2f * Mathf.PI * i / segments;
                Vector3 point = PerTauShellPoint(centerGalactic, latitude, longitude);
                points.Add(GalacticToUnity(point));
                visible.Add(!IsInsidePerTauGap(latitude * Mathf.Rad2Deg, longitude * Mathf.Rad2Deg));
            }

            bool allVisible = true;
            for (int i = 0; i < visible.Count; i++)
            {
                if (!visible[i])
                {
                    allVisible = false;
                    break;
                }
            }

            if (allVisible)
            {
                CreateLine("Per-Tau Latitude " + ringIndex, points,
                    perTauLineWidthPc, perTauMaterial, perTauRoot, true);
            }
            else
            {
                // Repeat the first sample so a ring whose gap is elsewhere can
                // still connect its final visible run back to the start point.
                points.Add(points[0]);
                visible.Add(visible[0]);
                CreateVisibleLineRuns("Per-Tau Latitude " + ringIndex,
                    points, visible, perTauLineWidthPc, perTauMaterial, perTauRoot, false);
            }
        }

        Vector3 labelGalactic = centerGalactic + new Vector3(
            0f,
            0f,
            PerTauRadiusPc + labelOffsetAboveStructurePc
        );

        CreateStructureLabel("Per-Tau Shell", perTauLabelText, perTauColor,
            GalacticToUnity(labelGalactic), perTauRoot);
    }

    private Vector3 PerTauShellPoint(Vector3 center, float latitude, float longitude)
    {
        // The 78 pc sphere is the published reference model. This low-amplitude,
        // deterministic perturbation is only a visual aid, not measured surface data.
        float variation = perTauRadialVariationPc * (
            0.50f * Mathf.Sin(2f * longitude + 0.45f) * Mathf.Cos(latitude) +
            0.30f * Mathf.Sin(3f * latitude - 0.70f) +
            0.20f * Mathf.Cos(longitude + 2f * latitude)
        );

        float radius = Mathf.Max(1f, PerTauRadiusPc + variation);
        return SpherePoint(center, radius, latitude, longitude);
    }

    private bool IsInsidePerTauGap(float latitudeDegrees, float longitudeDegrees)
    {
        if (!showIllustrativePerTauGaps || perTauMissingPatches == null)
            return false;

        for (int i = 0; i < perTauMissingPatches.Length; i++)
        {
            PerTauGap gap = perTauMissingPatches[i];
            if (gap == null)
                continue;

            float longitudeDistance = Mathf.Abs(Mathf.DeltaAngle(longitudeDegrees, gap.longitudeDegrees));
            float latitudeDistance = Mathf.Abs(latitudeDegrees - gap.latitudeDegrees);
            float x = longitudeDistance / Mathf.Max(0.1f, gap.longitudeHalfWidthDegrees);
            float y = latitudeDistance / Mathf.Max(0.1f, gap.latitudeHalfWidthDegrees);

            if ((x * x) + (y * y) <= 1f)
                return true;
        }

        return false;
    }

    private void GenerateTauRing()
    {
        if (tauRingRoot == null || tauRingMaterial == null)
            return;

        Vector3 centerGalactic = new Vector3(
            TauRingCenterGalX,
            TauRingCenterGalY,
            TauRingCenterGalZ
        );

        GetTauRingAxes(out Vector3 majorAxis, out Vector3 minorAxis);

        int segments = Mathf.Max(32, tauRingSegments);
        List<Vector3> points = new List<Vector3>(segments);

        for (int i = 0; i < segments; i++)
        {
            float angle = 2f * Mathf.PI * i / segments;
            Vector3 galacticPoint = centerGalactic +
                                    (majorAxis * (tauRingSemiMajorAxisPc * Mathf.Cos(angle))) +
                                    (minorAxis * (tauRingSemiMinorAxisPc * Mathf.Sin(angle)));
            points.Add(GalacticToUnity(galacticPoint));
        }

        // The Tau Ring is intentionally one inclined elliptical loop, not a solid torus.
        CreateLine("Tau Ring Ellipse", points, tauRingLineWidthPc,
            tauRingMaterial, tauRingRoot, true);

        // Find the ellipse's highest point along Galactic +Z (Unity +Y),
        // then place the label above that point.
        float verticalHalfExtent = Mathf.Sqrt(
            Mathf.Pow(tauRingSemiMajorAxisPc * majorAxis.z, 2f) +
            Mathf.Pow(tauRingSemiMinorAxisPc * minorAxis.z, 2f)
        );

        Vector3 labelGalactic = centerGalactic + new Vector3(
            0f,
            0f,
            verticalHalfExtent + labelOffsetAboveStructurePc
        );

        CreateStructureLabel("Tau Ring", tauRingLabelText, tauRingColor,
            GalacticToUnity(labelGalactic), tauRingRoot);
    }

    private static void GetTauRingAxes(out Vector3 majorAxis, out Vector3 minorAxis)
    {
        majorAxis = TauRingPublishedE1.normalized;
        minorAxis = TauRingPublishedE2 - Vector3.Dot(TauRingPublishedE2, majorAxis) * majorAxis;
        minorAxis.Normalize();
    }

    private void GenerateRadcliffeWave()
    {
        if (radcliffeRoot == null || radcliffeMaterial == null)
            return;

        int sampleCount = Mathf.Max(64, radcliffeSamples);
        List<Vector3> points = new List<Vector3>(sampleCount + 1);

        float dMax = Vector3.Distance(radcliffeAnchor0, radcliffeAnchor2);
        if (dMax <= 0.001f)
        {
            Debug.LogWarning("Radcliffe Wave anchors 0 and 2 must not be identical.", this);
            return;
        }

        float safePeriod = Mathf.Max(1f, radcliffePeriodPc);
        float safeGamma = Mathf.Max(0.001f, radcliffeGamma);
        Vector3 labelPointGalactic = Vector3.zero;

        for (int i = 0; i <= sampleCount; i++)
        {
            float t = i / (float)sampleCount;
            Vector3 centerPoint = QuadraticBezier(radcliffeAnchor0, radcliffeAnchor1, radcliffeAnchor2, t);

            // Alves et al. (2020): d(t) is the Euclidean distance from the start
            // anchor to the point along the quadratic centerline.
            float distancePc = Vector3.Distance(radcliffeAnchor0, centerPoint);
            float distanceKpc = distancePc / 1000f;

            // Alves et al. (2020), Eq. 1: damped sinusoidal displacement relative
            // to the Galactic XY plane:
            // dz = A * exp[-delta * (d/kpc)^2] *
            //      sin[(2*pi*d/P) * (1 + (d/dMax)*gamma) + phi].
            float amplitude = radcliffeAmplitudePc * Mathf.Exp(
                -radcliffeDelta * distanceKpc * distanceKpc
            );

            float periodFactor = 1f + ((distancePc / dMax) * safeGamma);
            float phase = (2f * Mathf.PI * distancePc / safePeriod) * periodFactor +
                          radcliffePhaseRadians;
            float verticalDisplacement = amplitude * Mathf.Sin(phase);

            centerPoint.z += verticalDisplacement;
            points.Add(GalacticToUnity(centerPoint));

            if (i == sampleCount / 2)
                labelPointGalactic = centerPoint;
        }

        CreateLine("Radcliffe Wave Spine (Alves et al. 2020 fit)", points,
            radcliffeLineWidthPc, radcliffeMaterial, radcliffeRoot, false);

        labelPointGalactic += Vector3.forward * labelOffsetAboveStructurePc;
        CreateStructureLabel("Radcliffe Wave", radcliffeLabelText, radcliffeColor,
            GalacticToUnity(labelPointGalactic), radcliffeRoot);
    }

    private TextMeshPro CreateStructureLabel(
        string structureName,
        string labelText,
        Color color,
        Vector3 worldPosition,
        GameObject parent)
    {
        GameObject labelObject = new GameObject(structureName + " Label");
        labelObject.transform.SetParent(parent.transform, false);
        labelObject.transform.position = worldPosition;

        TextMeshPro label = labelObject.AddComponent<TextMeshPro>();
        label.text = labelText;
        label.color = color;
        label.fontSize = labelFontSize;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.enableAutoSizing = false;
        label.fontStyle = FontStyles.Bold;

        if (labelFont != null)
            label.font = labelFont;

        // Large bounds prevent the world-space label from being clipped or wrapped.
        label.rectTransform.sizeDelta = new Vector2(120f, 20f);

        generatedLabels.Add(label);
        return label;
    }

    private void UpdateLabelBillboards()
    {
        if (generatedLabels.Count == 0)
            return;

        Transform cameraTransform = viewerCamera;
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (cameraTransform == null)
            return;

        for (int i = 0; i < generatedLabels.Count; i++)
        {
            TextMeshPro label = generatedLabels[i];
            if (label == null || !label.gameObject.activeInHierarchy)
                continue;

            Vector3 directionToCamera = cameraTransform.position - label.transform.position;
            if (directionToCamera.sqrMagnitude < 0.0001f)
                continue;

            Vector3 up = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(directionToCamera.normalized, up)) > 0.98f)
                up = Vector3.forward;

            // TextMeshPro's front face points along local +Z.
            label.transform.rotation = Quaternion.LookRotation(directionToCamera, up) * Quaternion.Euler(0f, 180f, 0f);
        }
    }

    private static Vector3 QuadraticBezier(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float oneMinusT = 1f - t;
        return (oneMinusT * oneMinusT * p0) +
               (2f * oneMinusT * t * p1) +
               (t * t * p2);
    }

    private static Vector3 SpherePoint(Vector3 center, float radius, float latitude, float longitude)
    {
        float horizontalRadius = radius * Mathf.Cos(latitude);

        return center + new Vector3(
            horizontalRadius * Mathf.Cos(longitude),
            horizontalRadius * Mathf.Sin(longitude),
            radius * Mathf.Sin(latitude)
        );
    }

    private void CreateVisibleLineRuns(
        string baseName,
        IList<Vector3> points,
        IList<bool> visible,
        float widthPc,
        Material material,
        GameObject parent,
        bool loopIfAllVisible)
    {
        if (points == null || visible == null || points.Count < 2 || points.Count != visible.Count)
            return;

        bool allVisible = true;
        for (int i = 0; i < visible.Count; i++)
        {
            if (!visible[i])
            {
                allVisible = false;
                break;
            }
        }

        if (allVisible && loopIfAllVisible)
        {
            CreateLine(baseName, points, widthPc, material, parent, true);
            return;
        }

        List<Vector3> run = new List<Vector3>();
        int runIndex = 0;

        for (int i = 0; i < points.Count; i++)
        {
            if (visible[i])
            {
                run.Add(points[i]);
            }
            else
            {
                FlushLineRun(baseName, run, runIndex, widthPc, material, parent);
                if (run.Count >= 2)
                    runIndex++;
                run.Clear();
            }
        }

        FlushLineRun(baseName, run, runIndex, widthPc, material, parent);
    }

    private void FlushLineRun(
        string baseName,
        List<Vector3> run,
        int runIndex,
        float widthPc,
        Material material,
        GameObject parent)
    {
        if (run == null || run.Count < 2)
            return;

        CreateLine(baseName + " Segment " + runIndex, run,
            widthPc, material, parent, false);
    }

    private void CreateLine(
        string objectName,
        IList<Vector3> points,
        float widthPc,
        Material material,
        GameObject parent,
        bool loop)
    {
        if (points == null || points.Count < 2 || material == null || parent == null)
            return;

        GameObject lineObject = new GameObject(objectName);
        lineObject.transform.SetParent(parent.transform, false);

        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = loop;
        line.positionCount = points.Count;
        line.SetPositions(ToArray(points));
        line.startWidth = widthPc;
        line.endWidth = widthPc;
        line.startColor = material.color;
        line.endColor = material.color;
        line.sharedMaterial = material;
        line.alignment = LineAlignment.View;
        line.textureMode = LineTextureMode.Stretch;
        line.numCornerVertices = 2;
        line.numCapVertices = 2;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.lightProbeUsage = LightProbeUsage.Off;
        line.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    private static Vector3[] ToArray(IList<Vector3> points)
    {
        Vector3[] result = new Vector3[points.Count];
        for (int i = 0; i < points.Count; i++)
            result[i] = points[i];
        return result;
    }

    private static Vector3 GalacticToUnity(Vector3 galactic)
    {
        // Project standard: Unity X = Galactic X, Unity Y = Galactic Z, Unity Z = Galactic Y.
        // This is the same component swap used by the Galactic-coordinate HUD.
        return new Vector3(galactic.x, galactic.z, galactic.y);
    }

    private static Material CreateLineMaterial(Color color, string materialName)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");

        if (shader == null)
        {
            Debug.LogError("Could not find a shader for GalacticStructureVisualizer LineRenderers.");
            return null;
        }

        Material material = new Material(shader)
        {
            name = materialName,
            color = color
        };

        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);

        return material;
    }

    private void ClearGeneratedObjects()
    {
        if (generatedRoot != null)
        {
            if (Application.isPlaying)
                Destroy(generatedRoot);
            else
                DestroyImmediate(generatedRoot);
            generatedRoot = null;
        }

        perTauRoot = null;
        tauRingRoot = null;
        radcliffeRoot = null;
        generatedLabels.Clear();

        DestroyMaterial(ref perTauMaterial);
        DestroyMaterial(ref tauRingMaterial);
        DestroyMaterial(ref radcliffeMaterial);
    }

    private void DestroyMaterial(ref Material material)
    {
        if (material == null)
            return;

        if (Application.isPlaying)
            Destroy(material);
        else
            DestroyImmediate(material);

        material = null;
    }
}
