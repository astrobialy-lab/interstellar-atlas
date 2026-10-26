using System.Collections.Generic;
using UnityEngine;

public class StarsLODNodesVisualizer : MonoBehaviour
{
    // ========================================================
    // REFERENCES
    // ========================================================

    [Header("References")]

    [Tooltip("Stars LOD loader providing desired, loaded, and node metadata.")]
    public StarsLODLoader starsLODLoader;

    // ========================================================
    // DISPLAY
    // ========================================================

    [Header("Display")]

    [Tooltip("Maximum number of desired-but-not-loaded nodes to draw.")]
    [Min(1)]
    public int maxDesiredNodesToDraw = 100;

    [Tooltip("Maximum number of loaded nodes to draw.")]
    [Min(1)]
    public int maxLoadedNodesToDraw = 100;

    // ========================================================
    // APPEARANCE
    // ========================================================

    [Header("Appearance")]

    [Tooltip("Width of the node bounding-box lines, in world units.")]
    [Min(0.001f)]
    public float lineWidth = 0.2f;

    [Tooltip("Optional material used for the bounds.")]
    public Material baseMaterial;

    [Tooltip("Color for desired nodes that are not loaded yet.")]
    public Color desiredColor = Color.red;

    [Tooltip("Color for nodes currently loaded into GPU buffers.")]
    public Color loadedColor = Color.green;

    // ========================================================
    // INTERNAL STATE
    // ========================================================

    private GameObject generatedRoot;

    private Material desiredMaterial;
    private Material loadedMaterial;

    // ========================================================
    // UNITY
    // ========================================================

    private void Start()
    {
        FindLoader();
    }

    private void FindLoader()
    {
        if (starsLODLoader == null)
        {
            starsLODLoader =
                FindAnyObjectByType<StarsLODLoader>();
        }
    }

    // ========================================================
    // TOGGLE REFRESH
    // ========================================================

    [ContextMenu("Toggle Bounds")]
    public void ToggleBounds()
    {
        RefreshBounds();
    }

    public void RefreshBounds()
    {
        // If bounds are already visible, toggle them off.
        if (generatedRoot != null)
        {
            ClearGeneratedObjects();
            return;
        }

        FindLoader();

        if (starsLODLoader == null)
        {
            Debug.LogWarning(
                "StarsLODNodesVisualizer: StarsLODLoader not found."
            );

            return;
        }

        if (!starsLODLoader.IsReady)
        {
            Debug.LogWarning(
                "StarsLODNodesVisualizer: StarsLODLoader is not ready."
            );

            return;
        }

        BuildBounds();
    }

    // ========================================================
    // BUILD
    // ========================================================

    private void BuildBounds()
    {
        ClearGeneratedObjects();

        generatedRoot = new GameObject(
            "Generated Stars LOD Bounds"
        );

        // Keep bounds at their absolute world coordinates.
        generatedRoot.transform.SetParent(null, true);

        CreateMaterials();

        if (desiredMaterial == null || loadedMaterial == null)
        {
            ClearGeneratedObjects();
            return;
        }

        // ----------------------------------------------------
        // Get current loader state
        // ----------------------------------------------------

        IReadOnlyCollection<uint> desiredNodes =
            starsLODLoader.DesiredNodeIds;

        IReadOnlyCollection<uint> loadedNodes =
            starsLODLoader.LoadedNodeIds;

        HashSet<uint> loadedSet =
            new HashSet<uint>(loadedNodes);

        // Sort IDs to make visualization deterministic.
        List<uint> sortedLoadedNodes =
            new List<uint>(loadedNodes);

        sortedLoadedNodes.Sort();

        List<uint> sortedDesiredNodes =
            new List<uint>(desiredNodes);

        sortedDesiredNodes.Sort();

        // ----------------------------------------------------
        // Loaded nodes
        //
        // Green = loaded and ready for rendering.
        // ----------------------------------------------------

        int loadedDrawn = 0;

        foreach (uint nodeId in sortedLoadedNodes)
        {
            if (loadedDrawn >= maxLoadedNodesToDraw)
            {
                break;
            }

            if (!starsLODLoader.TryGetNodeInfo(
                    nodeId,
                    out StarsLODLoader.StarsLODNodeInfo node))
            {
                continue;
            }

            CreateNodeBounds(
                node,
                loadedMaterial,
                "Loaded"
            );

            loadedDrawn++;
        }

        // ----------------------------------------------------
        // Desired but not loaded nodes
        //
        // Red = selected for loading, but not loaded yet.
        // ----------------------------------------------------

        int desiredDrawn = 0;

        foreach (uint nodeId in sortedDesiredNodes)
        {
            if (desiredDrawn >= maxDesiredNodesToDraw)
            {
                break;
            }

            // Already drawn in green.
            if (loadedSet.Contains(nodeId))
            {
                continue;
            }

            if (!starsLODLoader.TryGetNodeInfo(
                    nodeId,
                    out StarsLODLoader.StarsLODNodeInfo node))
            {
                continue;
            }

            CreateNodeBounds(
                node,
                desiredMaterial,
                "Desired"
            );

            desiredDrawn++;
        }

        Debug.Log(
            $"StarsLODNodesVisualizer: " +
            $"Layer={starsLODLoader.LayerIndex}, " +
            $"Loaded={loadedDrawn}/{loadedNodes.Count}, " +
            $"Desired={desiredDrawn}/{desiredNodes.Count}"
        );
    }

    // ========================================================
    // NODE BOUNDS
    // ========================================================

    private void CreateNodeBounds(
        StarsLODLoader.StarsLODNodeInfo node,
        Material material,
        string stateName)
    {
        if (material == null)
        {
            return;
        }

        GameObject boundsObject = new GameObject(
            $"{stateName} Node {node.nodeId}"
        );

        boundsObject.transform.SetParent(
            generatedRoot.transform,
            false
        );

        LineRenderer line =
            boundsObject.AddComponent<LineRenderer>();

        line.useWorldSpace = true;

        Vector3 min =
            node.center - node.halfSize;

        Vector3 max =
            node.center + node.halfSize;

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

        // Twelve cube edges, connected into one line path.
        Vector3[] path =
        {
            // Bottom rectangle.
            corners[0],
            corners[1],
            corners[2],
            corners[3],
            corners[0],

            // Connect to the top rectangle.
            corners[4],

            // Top rectangle.
            corners[5],
            corners[6],
            corners[7],
            corners[4],

            // Four vertical edges.
            corners[0],
            corners[1],
            corners[5],
            corners[6],
            corners[2],
            corners[3],
            corners[7]
        };

        line.positionCount = path.Length;
        line.SetPositions(path);

        line.startWidth = lineWidth;
        line.endWidth = lineWidth;

        line.startColor = material.color;
        line.endColor = material.color;

        line.numCapVertices = 0;
        line.numCornerVertices = 0;

        // Reuse the material shared by all bounds of this state.
        line.sharedMaterial = material;

        line.sortingOrder = 0;
    }

    // ========================================================
    // MATERIALS
    // ========================================================

    private void CreateMaterials()
    {
        desiredMaterial = CreateMaterial(desiredColor);
        loadedMaterial = CreateMaterial(loadedColor);
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
                    "StarsLODNodesVisualizer: No suitable shader found."
                );

                return null;
            }

            material = new Material(shader);
        }

        material.color = color;

        return material;
    }

    // ========================================================
    // PUBLIC CLEANUP
    // ========================================================

    public void ClearBounds()
    {
        ClearGeneratedObjects();
    }

    private void ClearGeneratedObjects()
    {
        if (generatedRoot != null)
        {
            if (Application.isPlaying)
            {
                Destroy(generatedRoot);
            }
            else
            {
                DestroyImmediate(generatedRoot);
            }

            generatedRoot = null;
        }

        if (desiredMaterial != null)
        {
            if (Application.isPlaying)
            {
                Destroy(desiredMaterial);
            }
            else
            {
                DestroyImmediate(desiredMaterial);
            }

            desiredMaterial = null;
        }

        if (loadedMaterial != null)
        {
            if (Application.isPlaying)
            {
                Destroy(loadedMaterial);
            }
            else
            {
                DestroyImmediate(loadedMaterial);
            }

            loadedMaterial = null;
        }
    }

    // ========================================================
    // ENABLE / DISABLE
    // ========================================================

    private void OnEnable()
    {
        if (generatedRoot != null)
        {
            generatedRoot.SetActive(true);
        }
    }

    private void OnDisable()
    {
        if (generatedRoot != null)
        {
            generatedRoot.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        ClearGeneratedObjects();
    }
}