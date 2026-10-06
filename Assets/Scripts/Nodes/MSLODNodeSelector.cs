using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class MSLODNodeSelector : MonoBehaviour
{
    [Header("LOD Data")]
    [SerializeField]
    private string metadataDirectory = "Data/Gaia/lod";

    [SerializeField]
    private Camera targetCamera;

    [Header("LOD Selection")]
    [SerializeField]
    [Min(0.0f)]
    private float solidAngleThreshold = 2.4e-4f;

    [SerializeField]
    [Min(0.0f)]
    private float solidAngleFadeRange = 1.2e-4f;

    [SerializeField]
    [Min(0.0f)]
    private float solidAnglePreloadRange = 6.0e-5f;

    [SerializeField]
    private bool frustumCulling = true;

    [Header("Debug")]
    [SerializeField]
    private bool verboseLogging = false;

    private readonly List<LODNode> nodes =
        new List<LODNode>();

    private readonly HashSet<uint> desiredNodeIds =
        new HashSet<uint>();

    private string projectRoot;
    private int rootNodeId = 0;
    private bool isReady = false;

    private int lastLoggedDesiredCount = -1;
    private string lastLoggedDepths = "";

    // --------------------------------------------------------
    // Public information
    // --------------------------------------------------------

    public bool IsReady
    {
        get
        {
            return isReady;
        }
    }

    public int NodeCount
    {
        get
        {
            return nodes.Count;
        }
    }

    public int RootNodeId
    {
        get
        {
            return rootNodeId;
        }
    }

    public Camera TargetCamera
    {
        get
        {
            return targetCamera;
        }
    }

    public IReadOnlyCollection<uint> DesiredNodeIds
    {
        get
        {
            return desiredNodeIds;
        }
    }

    // --------------------------------------------------------
    // Unity
    // --------------------------------------------------------

    private void Awake()
    {
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        if (targetCamera == null)
        {
            Debug.LogError(
                "MSLODNodeSelector: No target camera assigned."
            );

            return;
        }

        projectRoot =
            Directory.GetParent(
                Application.dataPath
            ).FullName;

        if (!LoadMetadata())
        {
            return;
        }

        isReady = true;
    }

    // --------------------------------------------------------
    // Selection
    // --------------------------------------------------------

    public void UpdateSelection()
    {
        if (!isReady)
        {
            desiredNodeIds.Clear();
            return;
        }

        if (targetCamera == null)
        {
            desiredNodeIds.Clear();
            return;
        }

        if (nodes.Count == 0)
        {
            desiredNodeIds.Clear();
            return;
        }

        desiredNodeIds.Clear();

        Plane[] frustumPlanes = null;

        if (frustumCulling)
        {
            frustumPlanes =
                GeometryUtility.CalculateFrustumPlanes(
                    targetCamera
                );
        }

        CollectVisibleNodes(
            rootNodeId,
            frustumPlanes
        );

        if (verboseLogging)
        {
            LogSelectionState();
        }
    }

    private void CollectVisibleNodes(
        int nodeId,
        Plane[] frustumPlanes)
    {
        if (nodeId < 0 ||
            nodeId >= nodes.Count)
        {
            return;
        }

        LODNode node =
            nodes[nodeId];

        Bounds bounds =
            new Bounds(
                node.center,
                node.halfSize * 2.0f
            );

        // ----------------------------------------------------
        // Frustum culling
        // ----------------------------------------------------

        if (frustumPlanes != null)
        {
            if (!GeometryUtility.TestPlanesAABB(
                    frustumPlanes,
                    bounds))
            {
                return;
            }
        }

        // ----------------------------------------------------
        // Apparent angular size
        // ----------------------------------------------------

        double solidAngle =
            ComputeSolidAngle(bounds);

        double fadeStartThreshold =
            Math.Max(
                0.0,
                solidAngleThreshold -
                solidAngleFadeRange
            );

        double preloadThreshold =
            Math.Max(
                0.0,
                fadeStartThreshold -
                solidAnglePreloadRange
            );

        if (solidAngle < preloadThreshold)
        {
            return;
        }

        // ----------------------------------------------------
        // Keep this node if it contains local content.
        //
        // For the current Gaia tree, localContentCount is
        // the number of stars stored directly in this node.
        // ----------------------------------------------------

        if (node.localContentCount > 0)
        {
            desiredNodeIds.Add(
                node.nodeId
            );
        }

        // ----------------------------------------------------
        // Leaf
        // ----------------------------------------------------

        if (node.childCount == 0)
        {
            return;
        }

        // ----------------------------------------------------
        // LOD STOP
        // ----------------------------------------------------

        if (solidAngle < solidAngleThreshold)
        {
            return;
        }

        // ----------------------------------------------------
        // REFINE
        // ----------------------------------------------------

        for (int i = 0; i < 8; i++)
        {
            int childId =
                node.children[i];

            if (childId < 0)
            {
                continue;
            }

            CollectVisibleNodes(
                childId,
                frustumPlanes
            );
        }
    }

    // --------------------------------------------------------
    // Node fade
    // --------------------------------------------------------

    public float ComputeNodeFade(
        uint nodeId)
    {
        if (nodeId >= nodes.Count)
        {
            return 0.0f;
        }

        LODNode node =
            nodes[(int)nodeId];

        double solidAngle =
            ComputeSolidAngle(
                new Bounds(
                    node.center,
                    node.halfSize * 2.0f
                )
            );

        double fadeStartThreshold =
            Math.Max(
                0.0,
                solidAngleThreshold -
                solidAngleFadeRange
            );

        if (solidAngle <= fadeStartThreshold)
        {
            return 0.0f;
        }

        if (solidAngle >= solidAngleThreshold)
        {
            return 1.0f;
        }

        double t =
            (solidAngle - fadeStartThreshold)
            /
            (solidAngleThreshold - fadeStartThreshold);

        t =
            Math.Max(
                0.0,
                Math.Min(
                    1.0,
                    t
                )
            );

        double smoothT =
            t * t * (3.0 - 2.0 * t);

        return (float)smoothT;
    }

    // --------------------------------------------------------
    // Node API
    // --------------------------------------------------------

    public bool TryGetNodeInfo(
        uint nodeId,
        out MSLODNodeInfo info)
    {
        if (nodeId >= nodes.Count)
        {
            info =
                default;

            return false;
        }

        LODNode node =
            nodes[(int)nodeId];

        info =
            new MSLODNodeInfo
            {
                nodeId =
                    node.nodeId,

                center =
                    node.center,

                halfSize =
                    node.halfSize,

                depth =
                    node.depth,

                localContentCount =
                    node.localContentCount,

                cumulativeContentCount =
                    node.cumulativeContentCount,

                childCount =
                    node.childCount
            };

        return true;
    }

    public int GetChildNodeId(
        uint nodeId,
        int childIndex)
    {
        if (nodeId >= nodes.Count)
        {
            return -1;
        }

        if (childIndex < 0 ||
            childIndex >= 8)
        {
            return -1;
        }

        return
            nodes[(int)nodeId]
                .children[childIndex];
    }

    public Bounds GetNodeBounds(
        uint nodeId)
    {
        if (nodeId >= nodes.Count)
        {
            return default;
        }

        LODNode node =
            nodes[(int)nodeId];

        return new Bounds(
            node.center,
            node.halfSize * 2.0f
        );
    }

    // --------------------------------------------------------
    // Metadata
    // --------------------------------------------------------

    private bool LoadMetadata()
    {
        string metadataPath =
            Path.Combine(
                projectRoot,
                metadataDirectory,
                "metadata.bin"
            );

        if (!File.Exists(metadataPath))
        {
            Debug.LogError(
                "MSLODNodeSelector: Metadata file not found:\n" +
                metadataPath
            );

            return false;
        }

        try
        {
            using FileStream file =
                File.OpenRead(metadataPath);

            using BinaryReader reader =
                new BinaryReader(file);

            byte[] magic =
                reader.ReadBytes(8);

            string magicString =
                System.Text.Encoding.ASCII
                    .GetString(magic);

            if (magicString != "MSLOD001")
            {
                Debug.LogError(
                    "MSLODNodeSelector: Invalid metadata magic: " +
                    magicString
                );

                return false;
            }

            uint version =
                reader.ReadUInt32();

            if (version != 1)
            {
                Debug.LogError(
                    "MSLODNodeSelector: Unsupported metadata version: " +
                    version
                );

                return false;
            }

            uint nodeCount =
                reader.ReadUInt32();

            ulong totalContentCount =
                reader.ReadUInt64();

            uint nSigma =
                reader.ReadUInt32();

            Vector3 rootMin =
                new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()
                );

            float rootSize =
                reader.ReadSingle();

            nodes.Clear();

            for (uint i = 0; i < nodeCount; i++)
            {
                LODNode node =
                    new LODNode();

                node.nodeId =
                    reader.ReadUInt32();

                node.center =
                    new Vector3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    );

                node.halfSize =
                    new Vector3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    );

                node.depth =
                    reader.ReadUInt32();

                node.localContentCount =
                    reader.ReadUInt32();

                node.cumulativeContentCount =
                    reader.ReadUInt64();

                node.childCount =
                    reader.ReadUInt32();

                node.children =
                    new int[8];

                for (int c = 0; c < 8; c++)
                {
                    node.children[c] =
                        reader.ReadInt32();
                }

                nodes.Add(node);
            }

            if (nodes.Count != nodeCount)
            {
                Debug.LogError(
                    "MSLODNodeSelector: Node count mismatch."
                );

                return false;
            }

            if (nodes.Count == 0)
            {
                Debug.LogError(
                    "MSLODNodeSelector: Metadata contains no nodes."
                );

                return false;
            }

            Debug.Log(
                $"MSLODNodeSelector: Loaded metadata. " +
                $"Nodes={nodeCount}, " +
                $"Content={totalContentCount}, " +
                $"N_SIGMA={nSigma}, " +
                $"RootSize={rootSize}"
            );

            return true;
        }
        catch (Exception e)
        {
            Debug.LogError(
                "MSLODNodeSelector: Failed to read metadata:\n" +
                e
            );

            return false;
        }
    }

    // --------------------------------------------------------
    // Solid angle
    // --------------------------------------------------------

    private double ComputeSolidAngle(
        Bounds bounds)
    {
        Vector3 camera =
            targetCamera.transform.position;

        if (bounds.Contains(camera))
        {
            return 4.0 * Math.PI;
        }

        Vector3 min =
            bounds.min;

        Vector3 max =
            bounds.max;

        Vector3 v000 =
            new Vector3(
                min.x,
                min.y,
                min.z
            );

        Vector3 v100 =
            new Vector3(
                max.x,
                min.y,
                min.z
            );

        Vector3 v010 =
            new Vector3(
                min.x,
                max.y,
                min.z
            );

        Vector3 v110 =
            new Vector3(
                max.x,
                max.y,
                min.z
            );

        Vector3 v001 =
            new Vector3(
                min.x,
                min.y,
                max.z
            );

        Vector3 v101 =
            new Vector3(
                max.x,
                min.y,
                max.z
            );

        Vector3 v011 =
            new Vector3(
                min.x,
                max.y,
                max.z
            );

        Vector3 v111 =
            new Vector3(
                max.x,
                max.y,
                max.z
            );

        double omega = 0.0;

        omega += TriangleSolidAngle(
            v000,
            v100,
            v110,
            camera
        );

        omega += TriangleSolidAngle(
            v000,
            v110,
            v010,
            camera
        );

        omega += TriangleSolidAngle(
            v001,
            v111,
            v101,
            camera
        );

        omega += TriangleSolidAngle(
            v001,
            v011,
            v111,
            camera
        );

        omega += TriangleSolidAngle(
            v000,
            v010,
            v011,
            camera
        );

        omega += TriangleSolidAngle(
            v000,
            v011,
            v001,
            camera
        );

        omega += TriangleSolidAngle(
            v100,
            v101,
            v111,
            camera
        );

        omega += TriangleSolidAngle(
            v100,
            v111,
            v110,
            camera
        );

        omega += TriangleSolidAngle(
            v000,
            v001,
            v101,
            camera
        );

        omega += TriangleSolidAngle(
            v000,
            v101,
            v100,
            camera
        );

        omega += TriangleSolidAngle(
            v010,
            v110,
            v111,
            camera
        );

        omega += TriangleSolidAngle(
            v010,
            v111,
            v011,
            camera
        );

        return omega;
    }

    private static double TriangleSolidAngle(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 observer)
    {
        double ax =
            a.x - observer.x;

        double ay =
            a.y - observer.y;

        double az =
            a.z - observer.z;

        double bx =
            b.x - observer.x;

        double by =
            b.y - observer.y;

        double bz =
            b.z - observer.z;

        double cx =
            c.x - observer.x;

        double cy =
            c.y - observer.y;

        double cz =
            c.z - observer.z;

        double la =
            Math.Sqrt(
                ax * ax +
                ay * ay +
                az * az
            );

        double lb =
            Math.Sqrt(
                bx * bx +
                by * by +
                bz * bz
            );

        double lc =
            Math.Sqrt(
                cx * cx +
                cy * cy +
                cz * cz
            );

        if (la <= double.Epsilon ||
            lb <= double.Epsilon ||
            lc <= double.Epsilon)
        {
            return 2.0 * Math.PI;
        }

        double triple =
            ax * (by * cz - bz * cy) +
            ay * (bz * cx - bx * cz) +
            az * (bx * cy - by * cx);

        triple =
            Math.Abs(triple);

        double ab =
            ax * bx +
            ay * by +
            az * bz;

        double bc =
            bx * cx +
            by * cy +
            bz * cz;

        double ca =
            cx * ax +
            cy * ay +
            cz * az;

        double denominator =
            la * lb * lc +
            ab * lc +
            bc * la +
            ca * lb;

        return 2.0 *
            Math.Atan2(
                triple,
                denominator
            );
    }

    // --------------------------------------------------------
    // Logging
    // --------------------------------------------------------

    private void LogSelectionState()
    {
        int desiredCount =
            desiredNodeIds.Count;

        int[] depthCounts =
            new int[32];

        foreach (uint nodeId in desiredNodeIds)
        {
            if (nodeId >= nodes.Count)
            {
                continue;
            }

            int depth =
                (int)nodes[(int)nodeId].depth;

            if (depth >= 0 &&
                depth < depthCounts.Length)
            {
                depthCounts[depth]++;
            }
        }

        string depthSummary =
            "";

        for (
            int depth = 0;
            depth < depthCounts.Length;
            depth++)
        {
            if (depthCounts[depth] > 0)
            {
                if (depthSummary.Length > 0)
                {
                    depthSummary += ", ";
                }

                depthSummary +=
                    $"D{depth}={depthCounts[depth]}";
            }
        }

        if (desiredCount != lastLoggedDesiredCount ||
            depthSummary != lastLoggedDepths)
        {
            lastLoggedDesiredCount =
                desiredCount;

            lastLoggedDepths =
                depthSummary;

            Debug.Log(
                $"MSLODNodeSelector: " +
                $"Desired={desiredCount}, " +
                $"Depths=[{depthSummary}]"
            );
        }
    }

    // --------------------------------------------------------
    // Internal node data
    // --------------------------------------------------------

    private class LODNode
    {
        public uint nodeId;

        public Vector3 center;
        public Vector3 halfSize;

        public uint depth;

        public uint localContentCount;
        public ulong cumulativeContentCount;

        public uint childCount;

        public int[] children;
    }

    // --------------------------------------------------------
    // Public node metadata
    // --------------------------------------------------------

    public struct MSLODNodeInfo
    {
        public uint nodeId;

        public Vector3 center;
        public Vector3 halfSize;

        public uint depth;

        public uint localContentCount;
        public ulong cumulativeContentCount;

        public uint childCount;
    }
}