using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class GalacticOrientationCompass : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Usually XR Origin / Camera Offset / Main Camera.")]
    public Transform viewerCamera;

    [Header("HUD Placement")]
    [Tooltip(
        "Position relative to the viewer camera. " +
        "X=0 and Y=0 places the compass at the center of the view."
    )]
    public Vector3 localPosition =
        new Vector3(0f, 0f, 1.0f);

    [Tooltip("Overall size of the compass.")]
    public float compassScale = 0.3f;

    [Header("Axis Appearance")]
    public float axisLength = 0.16f;
    public float axisWidth = 0.012f;

    [Header("Axis Colors")]
    public Color galacticXColor =
        new Color(1.0f, 0.2f, 0.2f, 1.0f);

    public Color galacticYColor =
        new Color(0.2f, 1.0f, 0.3f, 1.0f);

    public Color galacticZColor =
        new Color(0.25f, 0.55f, 1.0f, 1.0f);

    [Header("Overlay Rendering")]
    [Tooltip("Optional unlit material used for the axis sticks.")]
    public Material baseAxisMaterial;

    [Tooltip("Draw the compass over dust and other scene geometry.")]
    public bool alwaysDrawOnTop = true;

    private readonly List<LineRenderer> axisLines =
        new List<LineRenderer>();

    private GameObject compassAnchor;
    private Transform axisRoot;

    private bool isBuilding = false;

    private float lastCompassScale = -1f;

    private void Awake()
    {
        ResolveCamera();
        BuildCompass();
    }

    private void OnEnable()
    {
        if (compassAnchor != null)
        {
            compassAnchor.SetActive(true);
        }
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

        axisLines.Clear();
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

        // Only rebuild if the compass was actually destroyed.
        if (compassAnchor == null || axisRoot == null)
        {
            BuildCompass();

            if (compassAnchor == null || axisRoot == null)
            {
                return;
            }
        }

        compassAnchor.transform.localPosition =
            localPosition;

        compassAnchor.transform.localScale =
            Vector3.one * compassScale;

        // Update line thickness only when compassScale changes.
        if (!Mathf.Approximately(
            lastCompassScale,
            compassScale))
        {
            UpdateAxisWidths();
            lastCompassScale = compassScale;
        }

        axisRoot.localRotation = Quaternion.Inverse(viewerCamera.rotation);
    }

    [ContextMenu("Rebuild Compass")]
    public void RebuildCompass()
    {
        if (isBuilding)
        {
            return;
        }

        isBuilding = true;

        ClearCompass();
        BuildCompass();

        isBuilding = false;
    }

    private void BuildCompass()
    {
        if (compassAnchor != null && axisRoot != null)
        {
            return;
        }

        if (isBuilding)
        {
            return;
        }

        ResolveCamera();

        if (viewerCamera == null)
        {
            Debug.LogWarning(
                "GalacticOrientationCompass could not find the Main Camera."
            );
            return;
        }

        isBuilding = true;

        compassAnchor =
            new GameObject(
                "Galactic Orientation Compass HUD"
            );

        compassAnchor.transform.SetParent(
            viewerCamera,
            false
        );

        compassAnchor.transform.localPosition =
            localPosition;

        compassAnchor.transform.localRotation =
            Quaternion.identity;

        compassAnchor.transform.localScale =
            Vector3.one * compassScale;

        GameObject rootObject =
            new GameObject("Axis Triad");

        axisRoot = rootObject.transform;

        axisRoot.SetParent(
            compassAnchor.transform,
            false
        );

        axisLines.Clear();

        // Standard heliocentric Galactic Cartesian coordinates:
        // +Xgal -> Unity +X
        // +Ygal -> Unity +Z
        // +Zgal -> Unity +Y
        CreateAxisStick(
            "Galactic X",
            Vector3.right,
            galacticXColor
        );

        CreateAxisStick(
            "Galactic Y",
            Vector3.forward,
            galacticYColor
        );

        CreateAxisStick(
            "Galactic Z",
            Vector3.up,
            galacticZColor
        );

        UpdateAxisWidths();

        lastCompassScale = compassScale;

        isBuilding = false;
    }

    private void UpdateAxisWidths()
    {
        float scaledWidth =
            axisWidth * Mathf.Max(0f, compassScale);

        foreach (LineRenderer line in axisLines)
        {
            if (line == null)
            {
                continue;
            }

            line.startWidth = scaledWidth;
            line.endWidth = scaledWidth;
        }
    }

    private void ResolveCamera()
    {
        if (viewerCamera == null && Camera.main != null)
        {
            viewerCamera = Camera.main.transform;
        }
    }

    private void CreateAxisStick(
        string axisName,
        Vector3 direction,
        Color color
    )
    {
        GameObject axisObject =
            new GameObject(axisName);

        axisObject.transform.SetParent(
            axisRoot,
            false
        );

        Material axisMaterial =
            CreateOverlayMaterial(color);

        LineRenderer line =
            axisObject.AddComponent<LineRenderer>();

        line.useWorldSpace = false;
        line.positionCount = 2;

        line.SetPosition(
            0,
            Vector3.zero
        );

        line.SetPosition(
            1,
            direction * axisLength
        );

        // Base width. Actual width is scaled by compassScale.
        line.startWidth = axisWidth * compassScale;
        line.endWidth = axisWidth * compassScale;

        // Flat ends.
        line.numCapVertices = 0;
        line.numCornerVertices = 0;

        line.startColor = color;
        line.endColor = color;

        line.material = axisMaterial;
        line.sortingOrder = short.MaxValue;

        axisLines.Add(line);
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

        ConfigureOverlayMaterial(
            material,
            color
        );

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
        axisLines.Clear();

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