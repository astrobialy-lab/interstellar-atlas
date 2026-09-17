using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class GalacticCoordinateSystem : MonoBehaviour
{
    [Header("Axis Size")]
    [Tooltip("Length of each positive and negative axis in Unity units.")]
    public float axisLength = 20.0f;

    [Tooltip("Width of each axis line.")]
    public float axisWidth = 0.04f;

    [Tooltip("Size of the sphere marking each positive direction.")]
    public float positiveEndSize = 0.14f;

    [Header("Galactic Plane Grid")]
    [Tooltip("Outer radius of the polar grid in Unity units.")]
    public float gridRadius = 20.0f;

    [Tooltip("Distance between circular rings in Unity units.")]
    public float ringSpacing = 5.0f;

    [Tooltip("Angular separation of radial longitude spokes.")]
    [Range(5f, 90f)]
    public float longitudeStepDegrees = 30.0f;

    public int circleSegments = 128;
    public float gridLineWidth = 0.018f;
    public Color gridColor = new Color(0.35f, 0.85f, 1.0f, 0.38f);

    [Tooltip("Physical distance represented by one Unity unit.")]
    public float parsecsPerUnityUnit = 50.0f;

    [Header("Labels")]
    [Tooltip("Font size for distance, longitude, and origin labels.")]
    public float labelFontSize = 0.35f;

    [Tooltip("Larger font size used for the six axis-end labels.")]
    public float axisLabelFontSize = 1.0f;

    public Vector2 labelBoxSize = new Vector2(4.5f, 0.8f);
    public float labelOffset = 0.35f;

    [Tooltip("Use this if TextMeshPro labels face away from the camera.")]
    public bool reverseLabelFacing = false;

    [Header("Axis Colors")]
    public Color galacticXColor = new Color(1.0f, 0.2f, 0.2f, 1.0f);
    public Color galacticYColor = new Color(0.2f, 1.0f, 0.3f, 1.0f);
    public Color galacticZColor = new Color(0.25f, 0.55f, 1.0f, 1.0f);
    public Color originColor = Color.white;

    [Header("Optional Material")]
    [Tooltip(
        "Optional base material. If empty, the script creates an unlit material."
    )]
    public Material baseAxisMaterial;

    private readonly List<TextMeshPro> labels =
        new List<TextMeshPro>();

    private Transform generatedRoot;

    private void Awake()
    {
        BuildCoordinateSystem();
    }

    [ContextMenu("Rebuild Coordinate System")]
    public void BuildCoordinateSystem()
    {
        ClearGeneratedObjects();

        GameObject rootObject =
            new GameObject("Generated Galactic Coordinates");

        generatedRoot = rootObject.transform;
        generatedRoot.SetParent(transform, false);

        CreateGalacticPlaneGrid();

        // Standard heliocentric Galactic Cartesian coordinates:
        // +Xgal -> Galactic Center       -> Unity +X
        // +Ygal -> l = 90 degrees        -> Unity +Z
        // +Zgal -> North Galactic Pole   -> Unity +Y
        CreateAxis(
            "Galactic X Axis",
            Vector3.right,
            galacticXColor,
            "+X",
            "-X"
        );

        CreateAxis(
            "Galactic Y Axis",
            Vector3.forward,
            galacticYColor,
            "+Y",
            "-Y"
        );

        CreateAxis(
            "Galactic Z Axis",
            Vector3.up,
            galacticZColor,
            "+Z",
            "-Z"
        );

        CreateLabel(
            "Sun / Origin",
            "Sun\nOrigin (0, 0, 0)",
            Vector3.zero,
            originColor
        );
    }

    private void CreateGalacticPlaneGrid()
    {
        Material gridMaterial = CreateAxisMaterial(gridColor);

        if (ringSpacing > 0f)
        {
            for (
                float radius = ringSpacing;
                radius <= gridRadius + 0.001f;
                radius += ringSpacing
            )
            {
                CreateRing(radius, gridMaterial);

                float diagonal = radius * 0.70710678f;
                CreateLabel(
                    $"Distance {radius}",
                    $"{radius * parsecsPerUnityUnit:0} pc",
                    new Vector3(diagonal, 0f, diagonal),
                    gridColor
                );
            }
        }

        float step = Mathf.Max(5f, longitudeStepDegrees);

        for (float longitude = 0f; longitude < 360f; longitude += step)
        {
            float radians = longitude * Mathf.Deg2Rad;

            // Galactic +X is Unity +X; Galactic +Y is Unity +Z.
            Vector3 direction = new Vector3(
                Mathf.Cos(radians),
                0f,
                Mathf.Sin(radians)
            );

            CreateGridLine(
                $"Longitude {longitude:0}",
                Vector3.zero,
                direction * gridRadius,
                gridMaterial
            );

            CreateLabel(
                $"Longitude Label {longitude:0}",
                $"l={longitude:0} deg",
                direction * (gridRadius + labelOffset),
                gridColor
            );
        }
    }

    private void CreateRing(float radius, Material gridMaterial)
    {
        GameObject ringObject =
            new GameObject($"Distance Ring {radius:0.##}");

        ringObject.transform.SetParent(generatedRoot, false);

        LineRenderer ring = ringObject.AddComponent<LineRenderer>();
        ring.useWorldSpace = false;
        ring.loop = true;
        ring.positionCount = Mathf.Max(16, circleSegments);
        ring.startWidth = gridLineWidth;
        ring.endWidth = gridLineWidth;
        ring.startColor = gridColor;
        ring.endColor = gridColor;
        ring.material = gridMaterial;

        for (int i = 0; i < ring.positionCount; i++)
        {
            float angle =
                2f * Mathf.PI * i / ring.positionCount;

            ring.SetPosition(
                i,
                new Vector3(
                    Mathf.Cos(angle) * radius,
                    0f,
                    Mathf.Sin(angle) * radius
                )
            );
        }
    }

    private void CreateGridLine(
        string lineName,
        Vector3 start,
        Vector3 end,
        Material gridMaterial
    )
    {
        GameObject lineObject = new GameObject(lineName);
        lineObject.transform.SetParent(generatedRoot, false);

        LineRenderer line =
            lineObject.AddComponent<LineRenderer>();

        line.useWorldSpace = false;
        line.positionCount = 2;
        line.SetPosition(0, start);
        line.SetPosition(1, end);
        line.startWidth = gridLineWidth;
        line.endWidth = gridLineWidth;
        line.startColor = gridColor;
        line.endColor = gridColor;
        line.material = gridMaterial;
    }

    private void CreateAxis(
        string axisName,
        Vector3 unityDirection,
        Color axisColor,
        string positiveLabel,
        string negativeLabel
    )
    {
        GameObject axisObject = new GameObject(axisName);
        axisObject.transform.SetParent(generatedRoot, false);

        LineRenderer line =
            axisObject.AddComponent<LineRenderer>();

        line.useWorldSpace = false;
        line.positionCount = 2;
        line.SetPosition(0, -unityDirection * axisLength);
        line.SetPosition(1, unityDirection * axisLength);
        line.startWidth = axisWidth;
        line.endWidth = axisWidth;
        line.numCapVertices = 4;
        line.startColor = axisColor;
        line.endColor = axisColor;
        line.material = CreateAxisMaterial(axisColor);

        CreatePositiveEndMarker(
            axisName + " Positive End",
            unityDirection * axisLength,
            axisColor,
            line.material
        );

        CreateLabel(
            axisName + " Positive Label",
            positiveLabel,
            unityDirection * (axisLength + labelOffset),
            axisColor,
            axisLabelFontSize
        );

        CreateLabel(
            axisName + " Negative Label",
            negativeLabel,
            -unityDirection * (axisLength + labelOffset),
            axisColor,
            axisLabelFontSize
        );
    }

    private Material CreateAxisMaterial(Color color)
    {
        if (baseAxisMaterial != null)
        {
            Material copiedMaterial =
                new Material(baseAxisMaterial);

            copiedMaterial.color = color;
            return copiedMaterial;
        }

        Shader shader = Shader.Find("Sprites/Default");

        if (shader == null)
        {
            shader = Shader.Find(
                "Universal Render Pipeline/Unlit"
            );
        }

        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }

        if (shader == null)
        {
            Debug.LogError(
                "No suitable unlit shader was found for the " +
                "Galactic coordinate system. Assign Base Axis Material."
            );

            return null;
        }

        Material material = new Material(shader);
        material.color = color;
        return material;
    }

    private void CreatePositiveEndMarker(
        string markerName,
        Vector3 localPosition,
        Color markerColor,
        Material markerMaterial
    )
    {
        GameObject marker =
            GameObject.CreatePrimitive(PrimitiveType.Sphere);

        marker.name = markerName;
        marker.transform.SetParent(generatedRoot, false);
        marker.transform.localPosition = localPosition;
        marker.transform.localScale =
            Vector3.one * positiveEndSize;

        Collider markerCollider = marker.GetComponent<Collider>();
        if (markerCollider != null)
        {
            Destroy(markerCollider);
        }

        Renderer markerRenderer = marker.GetComponent<Renderer>();
        if (markerRenderer != null)
        {
            markerRenderer.material = markerMaterial;
            markerRenderer.material.color = markerColor;
        }
    }

    private void CreateLabel(
        string objectName,
        string labelText,
        Vector3 localPosition,
        Color labelColor,
        float customFontSize = -1f
    )
    {
        GameObject labelObject = new GameObject(objectName);
        labelObject.transform.SetParent(generatedRoot, false);
        labelObject.transform.localPosition = localPosition;

        TextMeshPro label = labelObject.AddComponent<TextMeshPro>();
        label.text = labelText;
        label.fontSize = customFontSize > 0f
            ? customFontSize
            : labelFontSize;
        label.color = labelColor;
        label.alignment = TextAlignmentOptions.Center;
        label.rectTransform.sizeDelta = labelBoxSize;

        labels.Add(label);
    }

    private void LateUpdate()
    {
        Camera activeCamera = Camera.main;
        if (activeCamera == null)
        {
            return;
        }

        Quaternion labelRotation = Quaternion.LookRotation(
            activeCamera.transform.position - transform.position,
            activeCamera.transform.up
        );

        if (reverseLabelFacing)
        {
            labelRotation *= Quaternion.Euler(0f, 180f, 0f);
        }

        foreach (TextMeshPro label in labels)
        {
            if (label != null)
            {
                label.transform.rotation = labelRotation;
            }
        }
    }

    private void ClearGeneratedObjects()
    {
        labels.Clear();

        Transform existingRoot =
            transform.Find("Generated Galactic Coordinates");

        if (existingRoot == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(existingRoot.gameObject);
        }
        else
        {
            DestroyImmediate(existingRoot.gameObject);
        }
    }
}