using System.Collections.Generic;
using UnityEngine;

public class GaiaMSLODBoundsVisualizer : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Gaia MS-LOD loader providing the desired, loaded, and node metadata.")]
    public GaiaMSLODLoader msLODLoader;

    [Header("Display")]
    [Tooltip("Maximum number of desired-but-not-loaded nodes to draw.")]
    [Min(1)]
    public int maxDesiredNodesToDraw = 100;

    [Tooltip("Maximum number of loaded nodes to draw.")]
    [Min(1)]
    public int maxLoadedNodesToDraw = 100;

    [Header("Appearance")]
    [Tooltip("Width of the node bounding-box lines.")]
    [Min(0.001f)]
    public float lineWidth = 0.2f;

    [Tooltip("Optional material used for the bounds.")]
    public Material baseMaterial;

    private GameObject generatedRoot;

    private Material desiredMaterial;
    private Material loadedMaterial;


    // --------------------------------------------------------
    // Unity
    // --------------------------------------------------------

    private void Start()
    {
        if (msLODLoader == null)
        {
            msLODLoader =
                FindAnyObjectByType<GaiaMSLODLoader>();
        }
    }


    // --------------------------------------------------------
    // Toggle refresh
    // --------------------------------------------------------

    public void RefreshBounds()
    {
        // Already visible -> hide them.
        if (generatedRoot != null)
        {
            ClearGeneratedObjects();
            return;
        }

        // Not visible -> create them.
        if (msLODLoader == null)
        {
            msLODLoader =
                FindAnyObjectByType<GaiaMSLODLoader>();
        }

        if (msLODLoader == null)
        {
            Debug.LogWarning(
                "GaiaMSLODBoundsVisualizer: " +
                "GaiaMSLODLoader not found."
            );

            return;
        }

        if (!msLODLoader.IsReady)
        {
            Debug.LogWarning(
                "GaiaMSLODBoundsVisualizer: " +
                "GaiaMSLODLoader is not ready yet."
            );

            return;
        }

        BuildBounds();
    }

    [ContextMenu("Toggle Bounds")]
    public void ToggleBounds()
    {
        RefreshBounds();
    }


    // --------------------------------------------------------
    // Build
    // --------------------------------------------------------

    private void BuildBounds()
    {
        ClearGeneratedObjects();

        generatedRoot =
            new GameObject(
                "Generated MSLOD Bounds"
            );

        // Keep everything in world space.
        generatedRoot.transform.SetParent(
            null,
            true
        );

        CreateMaterials();

        // ----------------------------------------------------
        // Get actual MS-LOD state.
        // ----------------------------------------------------

        IReadOnlyCollection<uint> desiredNodes =
            msLODLoader.DesiredNodeIds;

        IReadOnlyCollection<uint> loadedNodes =
            msLODLoader.LoadedNodeIds;

        HashSet<uint> loadedSet =
            new HashSet<uint>(
                loadedNodes
            );


        // ----------------------------------------------------
        // Draw loaded nodes first.
        //
        // Green = actually loaded.
        // ----------------------------------------------------

        int loadedDrawn = 0;

        foreach (uint nodeId in loadedNodes)
        {
            if (loadedDrawn >= maxLoadedNodesToDraw)
            {
                break;
            }

            if (!msLODLoader.TryGetNodeInfo(
                    nodeId,
                    out GaiaMSLODLoader.GaiaLODNodeInfo node))
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
        // Draw desired-but-not-loaded nodes.
        //
        // Red = desired but not loaded.
        // ----------------------------------------------------

        int desiredDrawn = 0;

        foreach (uint nodeId in desiredNodes)
        {
            if (desiredDrawn >= maxDesiredNodesToDraw)
            {
                break;
            }

            // Already loaded, so display it only as green.
            if (loadedSet.Contains(nodeId))
            {
                continue;
            }

            if (!msLODLoader.TryGetNodeInfo(
                    nodeId,
                    out GaiaMSLODLoader.GaiaLODNodeInfo node))
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
            $"GaiaMSLODBoundsVisualizer: " +
            $"Displayed Loaded={loadedDrawn}/{loadedNodes.Count}, " +
            $"Displayed Desired={desiredDrawn}/{desiredNodes.Count}"
        );
    }


    // --------------------------------------------------------
    // Node bounds
    // --------------------------------------------------------

    private void CreateNodeBounds(
        GaiaMSLODLoader.GaiaLODNodeInfo node,
        Material material,
        string stateName)
    {
        if (material == null)
        {
            return;
        }

        GameObject boundsObject =
            new GameObject(
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
            node.center -
            node.halfSize;

        Vector3 max =
            node.center +
            node.halfSize;

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

        // 12 cube edges.
        Vector3[] path =
        {
            corners[0],
            corners[1],
            corners[2],
            corners[3],
            corners[0],

            corners[4],
            corners[5],
            corners[6],
            corners[7],
            corners[4],

            corners[0],
            corners[1],
            corners[5],
            corners[6],
            corners[2],
            corners[3],
            corners[7]
        };

        line.positionCount =
            path.Length;

        line.SetPositions(path);

        line.startWidth =
            lineWidth;

        line.endWidth =
            lineWidth;

        line.startColor =
            material.color;

        line.endColor =
            material.color;

        line.numCapVertices = 0;
        line.numCornerVertices = 0;

        line.material =
            material;

        line.sortingOrder = 0;
    }


    // --------------------------------------------------------
    // Materials
    // --------------------------------------------------------

    private void CreateMaterials()
    {
        desiredMaterial =
            CreateMaterial(
                Color.red
            );

        loadedMaterial =
            CreateMaterial(
                Color.green
            );
    }

    private Material CreateMaterial(
        Color color)
    {
        Material material;

        if (baseMaterial != null)
        {
            material =
                new Material(
                    baseMaterial
                );
        }
        else
        {
            Shader shader =
                Shader.Find(
                    "Sprites/Default"
                );

            if (shader == null)
            {
                shader =
                    Shader.Find(
                        "Universal Render Pipeline/Unlit"
                    );
            }

            if (shader == null)
            {
                shader =
                    Shader.Find(
                        "Unlit/Color"
                    );
            }

            if (shader == null)
            {
                Debug.LogError(
                    "GaiaMSLODBoundsVisualizer: " +
                    "No suitable shader found."
                );

                return null;
            }

            material =
                new Material(shader);
        }

        material.color =
            color;

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
            {
                Destroy(generatedRoot);
            }
            else
            {
                DestroyImmediate(
                    generatedRoot
                );
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
                DestroyImmediate(
                    desiredMaterial
                );
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
                DestroyImmediate(
                    loadedMaterial
                );
            }

            loadedMaterial = null;
        }
    }


    // --------------------------------------------------------
    // Enable / Disable
    // --------------------------------------------------------

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