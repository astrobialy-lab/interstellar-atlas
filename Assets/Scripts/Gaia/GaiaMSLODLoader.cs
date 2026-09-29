using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

public class GaiaMSLODLoader : GaiaStarBufferProvider
{
    [Header("LOD Data")]
    [SerializeField]
    private string lodDirectory = "Data/Gaia/lod_test";

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

    [Header("Streaming")]
    [SerializeField]
    private float streamUpdateDistance = 1.0f;

    [SerializeField]
    private float streamUpdateAngle = 2.0f;

    [SerializeField]
    [Min(1)]
    private int maxConcurrentLoads = 3;

    [Header("Debug")]
    [SerializeField]
    private bool verboseLogging = false;

    private int lastLoggedDesiredCount = -1;
    private int lastLoggedLoadedCount = -1;
    private string lastLoggedDepths = "";

    private const int STAR_GPU_SIZE = 20;

    private readonly List<LODNode> nodes =
        new List<LODNode>();

    private readonly Dictionary<uint, LoadedNode> loadedNodes =
        new Dictionary<uint, LoadedNode>();

    private readonly HashSet<uint> loadingNodes =
        new HashSet<uint>();

    private readonly HashSet<uint> desiredNodeIds =
        new HashSet<uint>();

    private readonly List<Task<NodeLoadResult>> loadingTasks =
        new List<Task<NodeLoadResult>>();

    private Vector3 lastStreamPosition;
    private Quaternion lastStreamRotation;

    private string projectRoot;
    private int rootNodeId = 0;

    // --------------------------------------------------------
    // Unity
    // --------------------------------------------------------

    private void Start()
    {
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        if (targetCamera == null)
        {
            Debug.LogError(
                "GaiaMSLODLoader: No target camera assigned."
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

        lastStreamPosition =
            targetCamera.transform.position;

        lastStreamRotation =
            targetCamera.transform.rotation;

        UpdateStreaming();
    }

    private void Update()
    {
        ProcessCompletedLoads();

        if (targetCamera == null)
        {
            return;
        }

        Transform cameraTransform =
            targetCamera.transform;

        Vector3 currentPosition =
            cameraTransform.position;

        Quaternion currentRotation =
            cameraTransform.rotation;

        Vector3 movement =
            currentPosition - lastStreamPosition;

        float movementSquared =
            movement.sqrMagnitude;

        float distanceSquared =
            streamUpdateDistance *
            streamUpdateDistance;

        float angle =
            Quaternion.Angle(
                currentRotation,
                lastStreamRotation
            );

        if (movementSquared >= distanceSquared ||
            angle >= streamUpdateAngle)
        {
            lastStreamPosition =
                currentPosition;

            lastStreamRotation =
                currentRotation;

            UpdateStreaming();
        }
    }

    // --------------------------------------------------------
    // Metadata
    // --------------------------------------------------------

    private bool LoadMetadata()
    {
        string metadataPath =
            Path.Combine(
                projectRoot,
                lodDirectory,
                "metadata.bin"
            );

        if (!File.Exists(metadataPath))
        {
            Debug.LogError(
                "GaiaMSLODLoader: Metadata file not found:\n" +
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
                    "GaiaMSLODLoader: Invalid metadata magic: " +
                    magicString
                );

                return false;
            }

            uint version =
                reader.ReadUInt32();

            if (version != 1)
            {
                Debug.LogError(
                    "GaiaMSLODLoader: Unsupported metadata version: " +
                    version
                );

                return false;
            }

            uint nodeCount =
                reader.ReadUInt32();

            ulong totalStarCount =
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

                node.starCount =
                    reader.ReadUInt32();

                node.cumulativeStarCount =
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
                    "GaiaMSLODLoader: Node count mismatch."
                );

                return false;
            }

            Debug.Log(
                $"GaiaMSLODLoader: Loaded metadata. " +
                $"Nodes={nodeCount}, " +
                $"Stars={totalStarCount}, " +
                $"N_SIGMA={nSigma}, " +
                $"RootSize={rootSize}"
            );

            return true;
        }
        catch (Exception e)
        {
            Debug.LogError(
                "GaiaMSLODLoader: Failed to read metadata:\n" +
                e
            );

            return false;
        }
    }

    // --------------------------------------------------------
    // LOD traversal
    // --------------------------------------------------------

    private void UpdateStreaming()
    {
        if (nodes.Count == 0)
        {
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

        // ----------------------------------------------------
        // Start only a limited number of background loads.
        // ----------------------------------------------------

        int availableLoadSlots =
            Math.Max(
                0,
                maxConcurrentLoads -
                loadingNodes.Count
            );

        if (availableLoadSlots > 0)
        {
            foreach (uint nodeId in desiredNodeIds)
            {
                if (availableLoadSlots <= 0)
                {
                    break;
                }

                if (loadedNodes.ContainsKey(nodeId))
                {
                    continue;
                }

                if (loadingNodes.Contains(nodeId))
                {
                    continue;
                }

                StartBackgroundLoad(nodeId);

                availableLoadSlots--;
            }
        }

        // ----------------------------------------------------
        // Unload nodes that are no longer desired.
        // ----------------------------------------------------

        List<uint> unload =
            new List<uint>();

        foreach (uint nodeId in loadedNodes.Keys)
        {
            if (!desiredNodeIds.Contains(nodeId))
            {
                unload.Add(nodeId);
            }
        }

        foreach (uint nodeId in unload)
        {
            UnloadNode(nodeId);
        }

        // ----------------------------------------------------
        // Optional logging.
        // ----------------------------------------------------

        if (verboseLogging)
        {
            LogStreamingState();
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

        // Too small to contribute anything.
        if (solidAngle < preloadThreshold)
        {
            return;
        }

        // ----------------------------------------------------
        // This node itself is always retained.
        //
        // Nodes contain their own stars, so parents cannot
        // simply be discarded when children are loaded.
        // ----------------------------------------------------

        desiredNodeIds.Add(
            node.nodeId
        );

        // Leaf: nothing below it.
        if (node.childCount == 0)
        {
            return;
        }

        // ----------------------------------------------------
        // LOD STOP
        //
        // The node is already small enough.
        //
        // Do NOT descend further.
        // ----------------------------------------------------

        if (solidAngle < solidAngleThreshold)
        {
            return;
        }

        // ----------------------------------------------------
        // REFINE
        //
        // This node is large enough to justify more detail.
        // Keep this node AND inspect its children.
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

    private float ComputeNodeFade(
        LODNode node)
    {
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
    // Background loading
    // --------------------------------------------------------

    private void StartBackgroundLoad(
        uint nodeId)
    {
        loadingNodes.Add(nodeId);

        Task<NodeLoadResult> task =
            Task.Run(
                () => ReadNodeFromDisk(nodeId)
            );

        loadingTasks.Add(task);
    }

    private NodeLoadResult ReadNodeFromDisk(
        uint nodeId)
    {
        NodeLoadResult result =
            new NodeLoadResult();

        result.nodeId =
            nodeId;

        if (nodeId >= nodes.Count)
        {
            result.success = false;

            result.errorMessage =
                $"Invalid LOD node ID: {nodeId}";

            return result;
        }

        LODNode node =
            nodes[(int)nodeId];

        string filePath =
            Path.Combine(
                projectRoot,
                lodDirectory,
                "particles",
                $"particles_{nodeId:00000000}.bin"
            );

        if (!File.Exists(filePath))
        {
            result.success = false;

            result.errorMessage =
                "LOD particle file not found:\n" +
                filePath;

            return result;
        }

        long fileSize =
            new FileInfo(filePath).Length;

        long expectedSize =
            (long)node.starCount *
            STAR_GPU_SIZE;

        if (fileSize != expectedSize)
        {
            result.success = false;

            result.errorMessage =
                "LOD particle size mismatch.\n" +
                $"Node: {nodeId}\n" +
                $"Expected: {expectedSize} bytes\n" +
                $"Actual: {fileSize} bytes";

            return result;
        }

        byte[] data =
            File.ReadAllBytes(filePath);

        StarGPU[] stars =
            new StarGPU[node.starCount];

        using MemoryStream stream =
            new MemoryStream(data);

        using BinaryReader reader =
            new BinaryReader(stream);

        for (
            int i = 0;
            i < node.starCount;
            i++)
        {
            stars[i] =
                new StarGPU
                {
                    position =
                        new Vector3(
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle()
                        ),

                    magnitude =
                        reader.ReadSingle(),

                    bpRp =
                        reader.ReadSingle()
                };
        }

        result.success = true;
        result.stars = stars;

        result.drawBounds =
            new Bounds(
                node.center,
                node.halfSize * 2.0f
            );

        return result;
    }

    // --------------------------------------------------------
    // Completed loads
    // --------------------------------------------------------

    private void ProcessCompletedLoads()
    {
        for (
            int i = loadingTasks.Count - 1;
            i >= 0;
            i--)
        {
            Task<NodeLoadResult> task =
                loadingTasks[i];

            if (!task.IsCompleted)
            {
                continue;
            }

            loadingTasks.RemoveAt(i);

            NodeLoadResult result;

            try
            {
                result = task.Result;
            }
            catch (Exception e)
            {
                Debug.LogError(
                    "GaiaMSLODLoader: Background load failed:\n" +
                    e
                );

                continue;
            }

            loadingNodes.Remove(
                result.nodeId
            );

            if (!result.success)
            {
                Debug.LogError(
                    result.errorMessage
                );

                continue;
            }

            // Camera may have moved while the file was loading.
            if (!desiredNodeIds.Contains(
                    result.nodeId))
            {
                continue;
            }

            if (loadedNodes.ContainsKey(
                    result.nodeId))
            {
                continue;
            }

            GraphicsBuffer buffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    result.stars.Length,
                    STAR_GPU_SIZE
                );

            buffer.SetData(
                result.stars
            );

            LoadedNode loaded =
                new LoadedNode();

            loaded.nodeId =
                result.nodeId;

            loaded.starCount =
                result.stars.Length;

            loaded.buffer =
                buffer;

            loaded.drawBounds =
                result.drawBounds;

            loadedNodes.Add(
                result.nodeId,
                loaded
            );

            if (verboseLogging)
            {
                Debug.Log(
                    $"Loaded Gaia LOD node " +
                    $"{result.nodeId}: " +
                    $"{result.stars.Length} stars."
                );
            }
        }
    }

    // --------------------------------------------------------
    // Unload
    // --------------------------------------------------------

    private void UnloadNode(
        uint nodeId)
    {
        if (!loadedNodes.TryGetValue(
                nodeId,
                out LoadedNode node))
        {
            return;
        }

        if (node.buffer != null)
        {
            node.buffer.Release();
            node.buffer = null;
        }

        loadedNodes.Remove(
            nodeId
        );

        if (verboseLogging)
        {
            Debug.Log(
                $"Unloaded Gaia LOD node {nodeId}."
            );
        }
    }

    // --------------------------------------------------------
    // Logging
    // --------------------------------------------------------

    private void LogStreamingState()
    {
        int desiredCount =
            desiredNodeIds.Count;

        int loadedCount =
            loadedNodes.Count;

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
            loadedCount != lastLoggedLoadedCount ||
            depthSummary != lastLoggedDepths)
        {
            lastLoggedDesiredCount =
                desiredCount;

            lastLoggedLoadedCount =
                loadedCount;

            lastLoggedDepths =
                depthSummary;

            int totalStars =
                0;

            foreach (
                LoadedNode node
                in loadedNodes.Values)
            {
                if (node != null)
                {
                    totalStars +=
                        node.starCount;
                }
            }

            Debug.Log(
                $"GaiaMSLODLoader: " +
                $"Desired={desiredCount}, " +
                $"Loaded={loadedCount}, " +
                $"Stars={totalStars}, " +
                $"Depths=[{depthSummary}]"
            );
        }
    }

    // --------------------------------------------------------
    // Buffer provider
    // --------------------------------------------------------

    public override void GetLoadedStarBuffers(
        List<GraphicsBuffer> buffers,
        List<int> counts,
        List<float> fades)
    {
        buffers.Clear();
        counts.Clear();
        fades.Clear();

        foreach (
            LoadedNode node
            in loadedNodes.Values)
        {
            if (node == null)
            {
                continue;
            }

            if (node.buffer == null)
            {
                continue;
            }

            if (node.starCount <= 0)
            {
                continue;
            }

            if (node.nodeId >= nodes.Count)
            {
                continue;
            }

            LODNode lodNode =
                nodes[(int)node.nodeId];

            float fade =
                ComputeNodeFade(
                    lodNode
                );

            buffers.Add(
                node.buffer
            );

            counts.Add(
                node.starCount
            );

            fades.Add(
                fade
            );
        }
    }

    // --------------------------------------------------------
    // Cleanup
    // --------------------------------------------------------

    private void OnDestroy()
    {
        foreach (
            LoadedNode node
            in loadedNodes.Values)
        {
            if (node.buffer != null)
            {
                node.buffer.Release();
                node.buffer = null;
            }
        }

        loadedNodes.Clear();
        loadingNodes.Clear();
        desiredNodeIds.Clear();
        loadingTasks.Clear();
        nodes.Clear();
    }

    // --------------------------------------------------------
    // Internal types
    // --------------------------------------------------------

    private class LODNode
    {
        public uint nodeId;

        public Vector3 center;
        public Vector3 halfSize;

        public uint depth;
        public uint starCount;
        public ulong cumulativeStarCount;
        public uint childCount;

        public int[] children;
    }

    private class LoadedNode
    {
        public uint nodeId;
        public int starCount;

        public GraphicsBuffer buffer;
        public Bounds drawBounds;
    }

    private class NodeLoadResult
    {
        public bool success;

        public uint nodeId;

        public StarGPU[] stars;

        public Bounds drawBounds;

        public string errorMessage;
    }

    private struct StarGPU
    {
        public Vector3 position;
        public float magnitude;
        public float bpRp;
    }
}