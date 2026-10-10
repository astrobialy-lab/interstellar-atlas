using System.Collections.Generic;
using UnityEngine;

public class DustLODBoundsVisualizer : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Dust LOD loader that provides active brick information.")]
    public DustLODLoader dustLODLoader;

    [Header("Display")]
    [Min(1)]
    [Tooltip("Maximum number of requested but unloaded bricks to draw.")]
    public int maxDesiredNodesToDraw = 100;

    [Min(1)]
    [Tooltip("Maximum number of loaded bricks to draw.")]
    public int maxLoadedNodesToDraw = 100;

    [Header("Appearance")]
    [Min(0.001f)]
    [Tooltip("Width of the bounding-box lines, in Unity world units.")]
    public float lineWidth = 0.2f;

    [Tooltip("Optional material for the bounding boxes.")]
    public Material baseMaterial;

    private GameObject generatedRoot;

    private Material desiredMaterial;
    private Material loadedMaterial;


    // --------------------------------------------------------
    // Unity
    // --------------------------------------------------------

    private void Start()
    {
        FindLoader();
    }

    private void FindLoader()
    {
        if (dustLODLoader == null)
        {
            dustLODLoader =
                FindAnyObjectByType<DustLODLoader>();
        }
    }


    // --------------------------------------------------------
    // Toggle
    // --------------------------------------------------------

    public void ToggleBounds()
    {
        RefreshBounds();
    }

    [ContextMenu("Toggle Dust Bounds")]
    public void RefreshBounds()
    {
        // If visible, hide the boxes.
        if (generatedRoot != null)
        {
            ClearGeneratedObjects();
            return;
        }

        FindLoader();

        if (dustLODLoader == null)
        {
            Debug.LogWarning(
                "DustLODBoundsVisualizer: DustLODLoader not found."
            );

            return;
        }

        if (!dustLODLoader.IsInitialized)
        {
            Debug.LogWarning(
                "DustLODBoundsVisualizer: DustLODLoader is not initialized."
            );

            return;
        }

        BuildBounds();
    }


    // --------------------------------------------------------
    // Build
    // --------------------------------------------------------

    private void BuildBounds()
    {
        ClearGeneratedObjects();

        List<DustLODLoader.DustLODNodeInfo> allNodes =
            new List<DustLODLoader.DustLODNodeInfo>();

        dustLODLoader.GetDebugNodeInfo(allNodes);

        if (allNodes.Count == 0)
        {
            Debug.LogWarning(
                "DustLODBoundsVisualizer: No active dust bricks to display. " +
                "Make sure PrepareForCamera has run."
            );

            return;
        }

        List<DustLODLoader.DustLODNodeInfo> loadedNodes =
            new List<DustLODLoader.DustLODNodeInfo>();

        List<DustLODLoader.DustLODNodeInfo> desiredNodes =
            new List<DustLODLoader.DustLODNodeInfo>();

        foreach (DustLODLoader.DustLODNodeInfo node in allNodes)
        {
            if (node.isLoaded)
            {
                loadedNodes.Add(node);
            }
            else if (node.isDesired)
            {
                desiredNodes.Add(node);
            }
        }

        // Prefer finer LOD levels, then bricks nearer the camera.
        Vector3 cameraPosition =
            Camera.main != null
                ? Camera.main.transform.position
                : Vector3.zero;

        loadedNodes.Sort(
            (a, b) => ComparePriority(a, b, cameraPosition)
        );

        desiredNodes.Sort(
            (a, b) => ComparePriority(a, b, cameraPosition)
        );

        generatedRoot = new GameObject(
            "Generated Dust LOD Bounds"
        );

        // Bounds use world coordinates, not the visualizer's local space.
        generatedRoot.transform.SetParent(null, true);

        CreateMaterials();

        // ----------------------------------------------------
        // Loaded bricks: green
        // ----------------------------------------------------

        int loadedDrawn = Mathf.Min(
            maxLoadedNodesToDraw,
            loadedNodes.Count
        );

        for (int i = 0; i < loadedDrawn; i++)
        {
            CreateNodeBounds(
                loadedNodes[i],
                loadedMaterial,
                "Loaded"
            );
        }

        // ----------------------------------------------------
        // Requested but not loaded: red
        // ----------------------------------------------------

        int desiredDrawn = Mathf.Min(
            maxDesiredNodesToDraw,
            desiredNodes.Count
        );

        for (int i = 0; i < desiredDrawn; i++)
        {
            CreateNodeBounds(
                desiredNodes[i],
                desiredMaterial,
                "Desired"
            );
        }

        Debug.Log(
            "DustLODBoundsVisualizer: " +
            $"Loaded={loadedDrawn}/{loadedNodes.Count}, " +
            $"Desired={desiredDrawn}/{desiredNodes.Count}, " +
            $"LOD levels={dustLODLoader.LevelCount}"
        );
    }


    private static int ComparePriority(
        DustLODLoader.DustLODNodeInfo a,
        DustLODLoader.DustLODNodeInfo b,
        Vector3 cameraPosition)
    {
        // Smaller level numbers are finer LODs.
        int levelComparison = a.level.CompareTo(b.level);

        if (levelComparison != 0)
            return levelComparison;

        float distanceA =
            (a.bounds.center - cameraPosition).sqrMagnitude;

        float distanceB =
            (b.bounds.center - cameraPosition).sqrMagnitude;

        return distanceA.CompareTo(distanceB);
    }


    // --------------------------------------------------------
    // Draw one brick
    // --------------------------------------------------------

    private void CreateNodeBounds(
        DustLODLoader.DustLODNodeInfo node,
        Material material,
        string stateName)
    {
        if (material == null)
            return;

        GameObject boundsObject = new GameObject(
            $"{stateName} Dust LOD{node.level} " +
            $"({node.x},{node.y},{node.z})"
        );

        boundsObject.transform.SetParent(
            generatedRoot.transform,
            false
        );

        LineRenderer line =
            boundsObject.AddComponent<LineRenderer>();

        line.useWorldSpace = true;

        Vector3 min = node.bounds.min;
        Vector3 max = node.bounds.max;

        Vector3[] corners =
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(min.x, max.y, min.z),

            new Vector3(min.x, min.y, max.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, max.y, max.z),
            new Vector3(min.x, max.y, max.z)
        };

        // Twelve edges of the box.
        Vector3[] path =
        {
            corners[0], corners[1],
            corners[2], corners[3],
            corners[0],

            corners[4], corners[5],
            corners[6], corners[7],
            corners[4],

            corners[0], corners[4],
            corners[5], corners[1],
            corners[2], corners[6],
            corners[7], corners[3]
        };

        line.positionCount = path.Length;
        line.SetPositions(path);

        line.startWidth = lineWidth;
        line.endWidth = lineWidth;

        line.startColor = material.color;
        line.endColor = material.color;

        line.numCapVertices = 0;
        line.numCornerVertices = 0;

        line.material = material;
        line.sortingOrder = 0;
    }


    // --------------------------------------------------------
    // Materials
    // --------------------------------------------------------

    private void CreateMaterials()
    {
        desiredMaterial = CreateMaterial(Color.red);
        loadedMaterial = CreateMaterial(Color.green);
    }

    private Material CreateMaterial(Color color)
    {
        Material material;

        if (baseMaterial != null)
        {
            material = new Material(baseMaterial);
        }
        else
        {
            Shader shader = Shader.Find(
                "Sprites/Default"
            );

            if (shader == null)
            {
                shader = Shader.Find(
                    "Universal Render Pipeline/Unlit"
                );
            }

            if (shader == null)
            {
                shader = Shader.Find(
                    "Unlit/Color"
                );
            }

            if (shader == null)
            {
                Debug.LogError(
                    "DustLODBoundsVisualizer: No suitable shader found."
                );

                return null;
            }

            material = new Material(shader);
        }

        material.color = color;
        return material;
    }


    // --------------------------------------------------------
    // Cleanup
    // --------------------------------------------------------

    public void ClearBounds()
    {
        ClearGeneratedObjects();
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

        if (desiredMaterial != null)
        {
            if (Application.isPlaying)
                Destroy(desiredMaterial);
            else
                DestroyImmediate(desiredMaterial);

            desiredMaterial = null;
        }

        if (loadedMaterial != null)
        {
            if (Application.isPlaying)
                Destroy(loadedMaterial);
            else
                DestroyImmediate(loadedMaterial);

            loadedMaterial = null;
        }
    }

    private void OnEnable()
    {
        if (generatedRoot != null)
            generatedRoot.SetActive(true);
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
}