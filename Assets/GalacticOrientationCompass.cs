using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

public class GalacticOrientationCompass : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Usually XR Origin / Camera Offset / Main Camera.")]
    public Transform viewerCamera;

    [Tooltip(
        "Optional transform defining the Galactic grid orientation. " +
        "Leave empty when the grid uses world rotation."
    )]
    public Transform coordinateFrame;

    [Header("HUD Placement")]
    [Tooltip("Position relative to the viewer camera.")]
    public Vector3 localPosition =
        new Vector3(-0.8f, -0.5f, 1.0f);

    [Tooltip("Overall size of the compass.")]
    public float compassScale = 1.0f;

    [Header("Startup Visibility")]
    [Tooltip(
        "Assign the Technion logo or splash-screen object. " +
        "The compass remains hidden while this object is active."
    )]
    public GameObject technionLogo;

    [Tooltip(
        "Wait until the assigned Technion logo is disabled or destroyed."
    )]
    public bool waitForTechnionLogo = true;

    [Header("Arrow Appearance")]
    public float axisLength = 0.16f;
    public float axisWidth = 0.012f;
    public float arrowHeadLength = 0.045f;
    public float arrowHeadRadius = 0.022f;
    public float originSize = 0.025f;
    [Range(6, 32)]
    public int arrowHeadSegments = 12;

    [Header("Labels")]
    public float labelOffset = 0.025f;
    public float labelFontSize = 1.5f;
    public float labelScale = 0.06f;
    public Vector2 labelBoxSize = new Vector2(1.0f, 0.6f);

    [Header("Axis Colors")]
    public Color galacticXColor =
        new Color(1.0f, 0.2f, 0.2f, 1.0f);

    public Color galacticYColor =
        new Color(0.2f, 1.0f, 0.3f, 1.0f);

    public Color galacticZColor =
        new Color(0.25f, 0.55f, 1.0f, 1.0f);

    public Color originColor = Color.white;

    [Header("Overlay Rendering")]
    [Tooltip("Optional unlit material used for the arrows.")]
    public Material baseAxisMaterial;

    [Tooltip("Draw the compass over dust and other scene geometry.")]
    public bool alwaysDrawOnTop = true;

    private readonly List<TextMeshPro> labels =
        new List<TextMeshPro>();

    private readonly List<Material> generatedMaterials =
        new List<Material>();

    private GameObject compassAnchor;
    private Transform axisRoot;

    private void Awake()
    {
        ResolveCamera();
        BuildCompass();
        UpdateStartupVisibility();
    }

    private void OnEnable()
    {
        UpdateStartupVisibility();
    }

    private void OnDisable()
    {
        if (compassAnchor != null)
        {
            compassAnchor.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (compassAnchor != null)
        {
            Destroy(compassAnchor);
        }

        foreach (Material material in generatedMaterials)
        {
            if (material != null)
            {
                Destroy(material);
            }
        }
    }

    private void LateUpdate()
    {
        if (viewerCamera == null)
        {
            ResolveCamera();

            if (viewerCamera == null)
            {
                return;
            }
        }

        if (compassAnchor == null || axisRoot == null)
        {
            BuildCompass();

            if (compassAnchor == null || axisRoot == null)
            {
                return;
            }
        }

        compassAnchor.transform.localPosition = localPosition;
        compassAnchor.transform.localScale =
            Vector3.one * compassScale;

        UpdateStartupVisibility();

        Quaternion frameRotation = coordinateFrame != null
            ? coordinateFrame.rotation
            : Quaternion.identity;

        // The anchor follows the camera, while the axis root remains
        // aligned with the fixed Galactic coordinate frame.
        axisRoot.rotation = frameRotation;

        foreach (TextMeshPro label in labels)
        {
            if (label != null)
            {
                label.transform.rotation = viewerCamera.rotation;
            }
        }
    }

    [ContextMenu("Rebuild Compass")]
    public void BuildCompass()
    {
        ResolveCamera();

        if (viewerCamera == null)
        {
            Debug.LogWarning(
                "GalacticOrientationCompass could not find the Main Camera."
            );
            return;
        }

        ClearCompass();

        compassAnchor =
            new GameObject("Galactic Orientation Compass HUD");

        compassAnchor.transform.SetParent(viewerCamera, false);
        compassAnchor.transform.localPosition = localPosition;
        compassAnchor.transform.localRotation = Quaternion.identity;
        compassAnchor.transform.localScale =
            Vector3.one * compassScale;

        GameObject rootObject = new GameObject("Axis Triad");
        axisRoot = rootObject.transform;
        axisRoot.SetParent(compassAnchor.transform, false);

        // Standard heliocentric Galactic Cartesian coordinates:
        // +Xgal -> Unity +X
        // +Ygal -> Unity +Z
        // +Zgal -> Unity +Y
        CreateArrow(
            "Galactic X",
            Vector3.right,
            galacticXColor,
            "X"
        );

        CreateArrow(
            "Galactic Y",
            Vector3.forward,
            galacticYColor,
            "Y"
        );

        CreateArrow(
            "Galactic Z",
            Vector3.up,
            galacticZColor,
            "Z"
        );

        CreateOriginMarker();
        UpdateStartupVisibility();
    }

    private void UpdateStartupVisibility()
    {
        if (compassAnchor == null)
        {
            return;
        }

        bool logoIsVisible =
            technionLogo != null &&
            technionLogo.activeInHierarchy;

        bool compassShouldBeVisible =
            !waitForTechnionLogo ||
            !logoIsVisible;

        if (
            compassAnchor.activeSelf !=
            compassShouldBeVisible
        )
        {
            compassAnchor.SetActive(
                compassShouldBeVisible
            );
        }
    }

    private void ResolveCamera()
    {
        if (viewerCamera == null && Camera.main != null)
        {
            viewerCamera = Camera.main.transform;
        }
    }

    private void CreateArrow(
        string arrowName,
        Vector3 direction,
        Color color,
        string labelText
    )
    {
        GameObject arrowObject = new GameObject(arrowName);
        arrowObject.transform.SetParent(axisRoot, false);

        Material arrowMaterial = CreateOverlayMaterial(color);

        LineRenderer shaft =
            arrowObject.AddComponent<LineRenderer>();

        shaft.useWorldSpace = false;
        shaft.positionCount = 2;
        shaft.SetPosition(0, Vector3.zero);
        shaft.SetPosition(
            1,
            direction * Mathf.Max(
                0.001f,
                axisLength - arrowHeadLength
            )
        );
        shaft.startWidth = axisWidth;
        shaft.endWidth = axisWidth;
        shaft.numCapVertices = 6;
        shaft.startColor = color;
        shaft.endColor = color;
        shaft.material = arrowMaterial;
        shaft.sortingOrder = short.MaxValue;

        CreateArrowHead(
            arrowName + " Arrow Head",
            direction,
            color,
            arrowMaterial
        );

        CreateLabel(
            arrowName + " Label",
            labelText,
            direction * (axisLength + labelOffset),
            color
        );
    }

    private void CreateArrowHead(
        string objectName,
        Vector3 direction,
        Color color,
        Material material
    )
    {
        GameObject headObject = new GameObject(objectName);
        headObject.transform.SetParent(axisRoot, false);
        headObject.transform.localPosition =
            direction * (axisLength - arrowHeadLength);
        headObject.transform.localRotation =
            Quaternion.FromToRotation(Vector3.up, direction);

        MeshFilter meshFilter =
            headObject.AddComponent<MeshFilter>();

        MeshRenderer meshRenderer =
            headObject.AddComponent<MeshRenderer>();

        meshFilter.sharedMesh = CreateConeMesh();
        if (material != null)
        {
            meshRenderer.material = material;
            meshRenderer.material.color = color;
        }
        meshRenderer.sortingOrder = short.MaxValue;
    }

    private Mesh CreateConeMesh()
    {
        int segments = Mathf.Max(6, arrowHeadSegments);

        Vector3[] vertices = new Vector3[segments + 2];
        int[] triangles = new int[segments * 6];

        vertices[0] = Vector3.zero;
        vertices[segments + 1] =
            Vector3.up * arrowHeadLength;

        for (int i = 0; i < segments; i++)
        {
            float angle =
                2f * Mathf.PI * i / segments;

            vertices[i + 1] = new Vector3(
                Mathf.Cos(angle) * arrowHeadRadius,
                0f,
                Mathf.Sin(angle) * arrowHeadRadius
            );
        }

        int triangleIndex = 0;

        for (int i = 0; i < segments; i++)
        {
            int current = i + 1;
            int next = ((i + 1) % segments) + 1;

            // Base.
            triangles[triangleIndex++] = 0;
            triangles[triangleIndex++] = next;
            triangles[triangleIndex++] = current;

            // Side.
            triangles[triangleIndex++] = segments + 1;
            triangles[triangleIndex++] = current;
            triangles[triangleIndex++] = next;
        }

        Mesh mesh = new Mesh();
        mesh.name = "Compass Arrow Head";
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }

    private void CreateOriginMarker()
    {
        GameObject marker =
            GameObject.CreatePrimitive(PrimitiveType.Sphere);

        marker.name = "Compass Origin";
        marker.transform.SetParent(axisRoot, false);
        marker.transform.localPosition = Vector3.zero;
        marker.transform.localScale =
            Vector3.one * originSize;

        Collider collider = marker.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        Renderer renderer = marker.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material =
                CreateOverlayMaterial(originColor);
            renderer.sortingOrder = short.MaxValue;
        }
    }

    private void CreateLabel(
        string objectName,
        string text,
        Vector3 localPosition,
        Color color
    )
    {
        GameObject labelObject = new GameObject(objectName);
        labelObject.transform.SetParent(axisRoot, false);
        labelObject.transform.localPosition = localPosition;
        labelObject.transform.localScale =
            Vector3.one * labelScale;

        TextMeshPro label =
            labelObject.AddComponent<TextMeshPro>();

        label.text = text;
        label.fontSize = labelFontSize;
        label.color = color;
        label.alignment = TextAlignmentOptions.Center;
        label.rectTransform.sizeDelta = labelBoxSize;

        Renderer labelRenderer =
            label.GetComponent<Renderer>();

        if (labelRenderer != null)
        {
            labelRenderer.sortingOrder = short.MaxValue;
        }

        Material sourceMaterial = label.fontSharedMaterial;
        if (sourceMaterial != null)
        {
            Material labelMaterial =
                ConfigureOverlayMaterial(
                    new Material(sourceMaterial),
                    color
                );

            generatedMaterials.Add(labelMaterial);
            label.fontSharedMaterial = labelMaterial;
        }

        labels.Add(label);
    }

    private Material CreateOverlayMaterial(Color color)
    {
        Material material;

        if (baseAxisMaterial != null)
        {
            material = new Material(baseAxisMaterial);
        }
        else
        {
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
                    "No suitable compass shader was found. " +
                    "Assign Base Axis Material."
                );
                return null;
            }

            material = new Material(shader);
        }

        ConfigureOverlayMaterial(material, color);
        generatedMaterials.Add(material);
        return material;
    }

    private Material ConfigureOverlayMaterial(
        Material material,
        Color color
    )
    {
        material.color = color;

        if (!alwaysDrawOnTop)
        {
            return material;
        }

        material.renderQueue = 5000;

        if (material.HasProperty("_ZTestMode"))
        {
            material.SetFloat(
                "_ZTestMode",
                (float)CompareFunction.Always
            );
        }

        if (material.HasProperty("_ZTest"))
        {
            material.SetFloat(
                "_ZTest",
                (float)CompareFunction.Always
            );
        }

        if (material.HasProperty("unity_GUIZTestMode"))
        {
            material.SetFloat(
                "unity_GUIZTestMode",
                (float)CompareFunction.Always
            );
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetFloat("_ZWrite", 0f);
        }

        return material;
    }

    private void ClearCompass()
    {
        labels.Clear();

        if (compassAnchor != null)
        {
            if (Application.isPlaying)
            {
                Destroy(compassAnchor);
            }
            else
            {
                DestroyImmediate(compassAnchor);
            }
        }

        compassAnchor = null;
        axisRoot = null;
    }
}