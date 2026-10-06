using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class GaiaMSLODLoader : GaiaStarBufferProvider
{
    [Header("LOD")]
    [SerializeField]
    private MSLODNodeSelector nodeSelector;

    [Header("Cluster Filtering")]
    [SerializeField]
    private GaiaClusterFilter clusterFilter;

    [Header("Streaming")]
    [SerializeField]
    private float streamUpdateDistance = 1.0f;

    [SerializeField]
    private float streamUpdateAngle = 2.0f;

    [SerializeField]
    [Min(1)]
    private int maxConcurrentLoads = 10;

    [Header("Debug")]
    [SerializeField]
    private bool verboseLogging = false;

    private int lastLoggedDesiredCount = -1;
    private int lastLoggedLoadedCount = -1;
    private string lastLoggedDepths = "";

    private const int STAR_GPU_SIZE = 20;
    private const int SOURCE_ID_SIZE = 8;

    private readonly Dictionary<uint, LoadedNode> loadedNodes =
        new Dictionary<uint, LoadedNode>();

    private readonly HashSet<uint> loadingNodes =
        new HashSet<uint>();

    private readonly List<Task<NodeLoadResult>> loadingTasks =
        new List<Task<NodeLoadResult>>();

    private Vector3 lastStreamPosition;
    private Quaternion lastStreamRotation;

    private string projectRoot;

    private int activeFilterRevision = 0;

    // --------------------------------------------------------
    // Public information
    // --------------------------------------------------------

    public int NodeCount
    {
        get
        {
            if (nodeSelector == null)
            {
                return 0;
            }

            return nodeSelector.NodeCount;
        }
    }

    public int RootNodeId
    {
        get
        {
            if (nodeSelector == null)
            {
                return 0;
            }

            return nodeSelector.RootNodeId;
        }
    }

    public int FilterRevision
    {
        get
        {
            return activeFilterRevision;
        }
    }

    public IReadOnlyCollection<uint> DesiredNodeIds
    {
        get
        {
            if (nodeSelector == null)
            {
                return Array.Empty<uint>();
            }

            return nodeSelector.DesiredNodeIds;
        }
    }

    public IReadOnlyCollection<uint> LoadedNodeIds
    {
        get
        {
            return loadedNodes.Keys;
        }
    }

    public MSLODNodeSelector NodeSelector
    {
        get
        {
            return nodeSelector;
        }
    }

    // --------------------------------------------------------
    // Unity
    // --------------------------------------------------------

    private void Start()
    {
        if (nodeSelector == null)
        {
            nodeSelector =
                GetComponent<MSLODNodeSelector>();
        }

        if (nodeSelector == null)
        {
            nodeSelector =
                FindAnyObjectByType<MSLODNodeSelector>();
        }

        if (nodeSelector == null)
        {
            Debug.LogError(
                "GaiaMSLODLoader: No MSLODNodeSelector found."
            );

            return;
        }

        if (!nodeSelector.IsReady)
        {
            Debug.LogError(
                "GaiaMSLODLoader: MSLODNodeSelector is not ready."
            );

            return;
        }

        if (nodeSelector.TargetCamera == null)
        {
            Debug.LogError(
                "GaiaMSLODLoader: MSLODNodeSelector has no target camera."
            );

            return;
        }

        if (clusterFilter == null)
        {
            clusterFilter =
                GetComponent<GaiaClusterFilter>();
        }

        if (clusterFilter == null)
        {
            clusterFilter =
                FindAnyObjectByType<GaiaClusterFilter>();
        }

        projectRoot =
            Directory.GetParent(
                Application.dataPath
            ).FullName;

        activeFilterRevision =
            GetCurrentFilterRevision();

        lastStreamPosition =
            nodeSelector.TargetCamera
                .transform.position;

        lastStreamRotation =
            nodeSelector.TargetCamera
                .transform.rotation;

        UpdateStreaming();
    }

    private void Update()
    {
        if (nodeSelector == null ||
            !nodeSelector.IsReady)
        {
            return;
        }

        Camera targetCamera =
            nodeSelector.TargetCamera;

        if (targetCamera == null)
        {
            return;
        }

        HandleFilterChanges();

        // Install finished nodes and free their load slots.
        ProcessCompletedLoads();

        Transform cameraTransform =
            targetCamera.transform;

        Vector3 currentPosition =
            cameraTransform.position;

        Quaternion currentRotation =
            cameraTransform.rotation;

        Vector3 movement =
            currentPosition -
            lastStreamPosition;

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

        // Recalculate the desired node set only when
        // the camera has moved/rotated enough.
        if (movementSquared >= distanceSquared ||
            angle >= streamUpdateAngle)
        {
            lastStreamPosition =
                currentPosition;

            lastStreamRotation =
                currentRotation;

            UpdateStreaming();
        }

        // Completed loads may have freed slots.
        StartPendingLoads();
    }

    // --------------------------------------------------------
    // Filter changes
    // --------------------------------------------------------

    private void HandleFilterChanges()
    {
        int currentRevision =
            GetCurrentFilterRevision();

        if (currentRevision ==
            activeFilterRevision)
        {
            return;
        }

        activeFilterRevision =
            currentRevision;

        if (verboseLogging)
        {
            Debug.Log(
                "GaiaMSLODLoader: Cluster filter changed. " +
                "Reloading loaded nodes."
            );
        }

        InvalidateLoadedNodes();

        UpdateStreaming();
    }

    private int GetCurrentFilterRevision()
    {
        if (clusterFilter == null)
        {
            return 0;
        }

        return clusterFilter.Revision;
    }

    private GaiaClusterFilterSettings GetCurrentFilterSettings()
    {
        if (clusterFilter == null)
        {
            return default;
        }

        GaiaClusterFilterSettings settings =
            clusterFilter.CreateSnapshot();

        // Do not filter anything when the database failed
        // to load. This keeps the normal Gaia renderer usable.
        if (clusterFilter.Database == null)
        {
            settings.enabled = false;
        }

        return settings;
    }

    private bool ShouldKeepStar(
        ulong sourceId,
        GaiaClusterFilterSettings settings)
    {
        if (!settings.enabled)
        {
            return true;
        }

        if (clusterFilter == null ||
            clusterFilter.Database == null)
        {
            return true;
        }

        return clusterFilter.Database
            .StarHasMatchingCluster(
                sourceId,
                settings
            );
    }

    // --------------------------------------------------------
    // Public node API
    // --------------------------------------------------------

    public bool TryGetNodeInfo(
        uint nodeId,
        out GaiaLODNodeInfo info)
    {
        if (nodeSelector == null ||
            !nodeSelector.TryGetNodeInfo(
                nodeId,
                out MSLODNodeSelector.MSLODNodeInfo node))
        {
            info =
                default;

            return false;
        }

        info =
            new GaiaLODNodeInfo
            {
                nodeId =
                    node.nodeId,

                center =
                    node.center,

                halfSize =
                    node.halfSize,

                depth =
                    node.depth,

                starCount =
                    node.localContentCount,

                childCount =
                    node.childCount
            };

        return true;
    }

    public int GetChildNodeId(
        uint nodeId,
        int childIndex)
    {
        if (nodeSelector == null)
        {
            return -1;
        }

        return nodeSelector.GetChildNodeId(
            nodeId,
            childIndex
        );
    }

    public Bounds GetNodeBounds(
        uint nodeId)
    {
        if (nodeSelector == null)
        {
            return default;
        }

        return nodeSelector.GetNodeBounds(
            nodeId
        );
    }

    // --------------------------------------------------------
    // LOD / streaming
    // --------------------------------------------------------

    private void UpdateStreaming()
    {
        if (nodeSelector == null ||
            !nodeSelector.IsReady)
        {
            return;
        }

        // Recalculate which nodes are currently desired.
        nodeSelector.UpdateSelection();

        // Start as many loads as the concurrency limit allows.
        StartPendingLoads();

        // Unload nodes that are no longer desired.
        IReadOnlyCollection<uint> desiredNodeIds =
            nodeSelector.DesiredNodeIds;

        List<uint> unload =
            new List<uint>();

        foreach (uint nodeId in loadedNodes.Keys)
        {
            if (!desiredNodeIds.Contains(
                    nodeId))
            {
                unload.Add(
                    nodeId
                );
            }
        }

        foreach (uint nodeId in unload)
        {
            UnloadNode(
                nodeId
            );
        }

        if (verboseLogging)
        {
            LogStreamingState();
        }
    }

    private void StartPendingLoads()
    {
        if (nodeSelector == null ||
            !nodeSelector.IsReady)
        {
            return;
        }

        IReadOnlyCollection<uint> desiredNodeIds =
            nodeSelector.DesiredNodeIds;

        int availableLoadSlots =
            Math.Max(
                0,
                maxConcurrentLoads -
                loadingNodes.Count
            );

        if (availableLoadSlots <= 0)
        {
            return;
        }

        foreach (uint nodeId in desiredNodeIds)
        {
            if (availableLoadSlots <= 0)
            {
                break;
            }

            if (loadedNodes.ContainsKey(
                    nodeId))
            {
                continue;
            }

            if (loadingNodes.Contains(
                    nodeId))
            {
                continue;
            }

            StartBackgroundLoad(
                nodeId
            );

            availableLoadSlots--;
        }
    }

    // --------------------------------------------------------
    // Node fade
    // --------------------------------------------------------

    private float ComputeNodeFade(
        uint nodeId)
    {
        if (nodeSelector == null)
        {
            return 0.0f;
        }

        return nodeSelector.ComputeNodeFade(
            nodeId
        );
    }

    // --------------------------------------------------------
    // Background loading
    // --------------------------------------------------------

    private void StartBackgroundLoad(
        uint nodeId)
    {
        loadingNodes.Add(
            nodeId
        );

        GaiaClusterFilterSettings filterSettings =
            GetCurrentFilterSettings();

        int filterRevision =
            GetCurrentFilterRevision();

        Task<NodeLoadResult> task =
            Task.Run(
                () =>
                    ReadNodeFromDisk(
                        nodeId,
                        filterSettings,
                        filterRevision
                    )
            );

        loadingTasks.Add(
            task
        );
    }

    private NodeLoadResult ReadNodeFromDisk(
        uint nodeId,
        GaiaClusterFilterSettings filterSettings,
        int filterRevision)
    {
        NodeLoadResult result =
            new NodeLoadResult();

        result.nodeId =
            nodeId;

        result.filterRevision =
            filterRevision;

        if (nodeSelector == null ||
            !nodeSelector.TryGetNodeInfo(
                nodeId,
                out MSLODNodeSelector.MSLODNodeInfo node))
        {
            result.success = false;

            result.errorMessage =
                $"Invalid LOD node ID: {nodeId}";

            return result;
        }

        uint starCount =
            node.localContentCount;

        // Valid internal node with no stars directly stored
        // in it. There is intentionally no particle file.
        if (starCount == 0)
        {
            result.success = true;

            result.stars =
                Array.Empty<GaiaStarGPU>();

            result.drawBounds =
                new Bounds(
                    node.center,
                    node.halfSize * 2.0f
                );

            return result;
        }

        string particlePath =
            Path.Combine(
                projectRoot,
                "Data/Gaia/lod",
                "particles",
                $"particles_{nodeId:00000000}.bin"
            );

        if (!File.Exists(
                particlePath))
        {
            result.success = false;

            result.errorMessage =
                "LOD particle file not found:\n" +
                particlePath;

            return result;
        }

        long particleFileSize =
            new FileInfo(
                particlePath
            ).Length;

        long expectedParticleSize =
            (long)starCount *
            STAR_GPU_SIZE;

        if (particleFileSize !=
            expectedParticleSize)
        {
            result.success = false;

            result.errorMessage =
                "LOD particle size mismatch.\n" +
                $"Node: {nodeId}\n" +
                $"Expected: {expectedParticleSize} bytes\n" +
                $"Actual: {particleFileSize} bytes";

            return result;
        }

        bool useFilter =
            filterSettings.enabled;

        byte[] particleData =
            File.ReadAllBytes(
                particlePath
            );

        byte[] sourceIdData = null;

        if (useFilter)
        {
            string sourceIdPath =
                Path.Combine(
                    projectRoot,
                    "Data/Gaia/lod",
                    "source_ids",
                    $"source_ids_{nodeId:00000000}.bin"
                );

            if (!File.Exists(
                    sourceIdPath))
            {
                result.success = false;

                result.errorMessage =
                    "LOD source ID sidecar not found:\n" +
                    sourceIdPath;

                return result;
            }

            long sourceIdFileSize =
                new FileInfo(
                    sourceIdPath
                ).Length;

            long expectedSourceIdSize =
                (long)starCount *
                SOURCE_ID_SIZE;

            if (sourceIdFileSize !=
                expectedSourceIdSize)
            {
                result.success = false;

                result.errorMessage =
                    "LOD source ID size mismatch.\n" +
                    $"Node: {nodeId}\n" +
                    $"Expected: {expectedSourceIdSize} bytes\n" +
                    $"Actual: {sourceIdFileSize} bytes";

                return result;
            }

            sourceIdData =
                File.ReadAllBytes(
                    sourceIdPath
                );
        }

        if (!useFilter)
        {
            GaiaStarGPU[] stars =
                new GaiaStarGPU[starCount];

            using MemoryStream stream =
                new MemoryStream(
                    particleData
                );

            using BinaryReader reader =
                new BinaryReader(
                    stream
                );

            for (
                int i = 0;
                i < starCount;
                i++)
            {
                stars[i] =
                    ReadStar(
                        reader
                    );
            }

            result.success = true;

            result.stars =
                stars;

            result.drawBounds =
                new Bounds(
                    node.center,
                    node.halfSize * 2.0f
                );

            return result;
        }

        List<GaiaStarGPU> filteredStars =
            new List<GaiaStarGPU>(
                (int)starCount
            );

        using MemoryStream particleStream =
            new MemoryStream(
                particleData
            );

        using BinaryReader particleReader =
            new BinaryReader(
                particleStream
            );

        using MemoryStream sourceIdStream =
            new MemoryStream(
                sourceIdData
            );

        using BinaryReader sourceIdReader =
            new BinaryReader(
                sourceIdStream
            );

        for (
            int i = 0;
            i < starCount;
            i++)
        {
            GaiaStarGPU star =
                ReadStar(
                    particleReader
                );

            ulong sourceId =
                sourceIdReader.ReadUInt64();

            if (ShouldKeepStar(
                    sourceId,
                    filterSettings))
            {
                filteredStars.Add(
                    star
                );
            }
        }

        result.success = true;

        result.stars =
            filteredStars.ToArray();

        result.drawBounds =
            new Bounds(
                node.center,
                node.halfSize * 2.0f
            );

        return result;
    }

    private static GaiaStarGPU ReadStar(
        BinaryReader reader)
    {
        return new GaiaStarGPU
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

    // --------------------------------------------------------
    // Completed loads
    // --------------------------------------------------------

    private void ProcessCompletedLoads()
    {
        for (
            int i = loadingTasks.Count - 1;
            i >= 0;
            i--
        )
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

            // The filter changed while this node was loading.
            if (result.filterRevision !=
                activeFilterRevision)
            {
                continue;
            }

            // Camera may have moved while the file was loading.
            // If it is no longer desired, throw the loaded result away.
            if (!DesiredNodeIds.Contains(
                    result.nodeId))
            {
                continue;
            }

            if (loadedNodes.ContainsKey(
                    result.nodeId))
            {
                continue;
            }

            GraphicsBuffer buffer = null;

            if (result.stars.Length > 0)
            {
                buffer =
                    new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured,
                        result.stars.Length,
                        STAR_GPU_SIZE
                    );

                buffer.SetData(
                    result.stars
                );
            }

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

    private void InvalidateLoadedNodes()
    {
        foreach (
            LoadedNode node
            in loadedNodes.Values)
        {
            if (node == null)
            {
                continue;
            }

            if (node.buffer != null)
            {
                node.buffer.Release();
                node.buffer = null;
            }
        }

        loadedNodes.Clear();
    }

    // --------------------------------------------------------
    // Logging
    // --------------------------------------------------------

    private void LogStreamingState()
    {
        int desiredCount =
            DesiredNodeIds.Count;

        int loadedCount =
            loadedNodes.Count;

        int[] depthCounts =
            new int[32];

        foreach (uint nodeId in DesiredNodeIds)
        {
            if (!TryGetNodeInfo(
                    nodeId,
                    out GaiaLODNodeInfo node))
            {
                continue;
            }

            int depth =
                (int)node.depth;

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
            depth++
        )
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
                $"Loading={loadingNodes.Count}, " +
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

            if (node.nodeId >= NodeCount)
            {
                continue;
            }

            float fade =
                ComputeNodeFade(
                    node.nodeId
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
        loadingTasks.Clear();
    }

    // --------------------------------------------------------
    // Internal types
    // --------------------------------------------------------

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

        public GaiaStarGPU[] stars;

        public Bounds drawBounds;

        public string errorMessage;

        public int filterRevision;
    }

    // --------------------------------------------------------
    // Public Gaia node metadata
    // --------------------------------------------------------

    public struct GaiaLODNodeInfo
    {
        public uint nodeId;

        public Vector3 center;
        public Vector3 halfSize;

        public uint depth;
        public uint starCount;
        public uint childCount;
    }

    // --------------------------------------------------------
    // Public GPU star format
    // --------------------------------------------------------

    public struct GaiaStarGPU
    {
        public Vector3 position;
        public float magnitude;
        public float bpRp;
    }
}