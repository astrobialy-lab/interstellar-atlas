using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class StarsLODLoader : GaiaStarBufferProvider
{
    // ========================================================
    // LOD DATA
    // ========================================================

    [Header("LOD Data")]

    [SerializeField]
    private string metadataDirectoryDataPath = "Gaia/lod";

    [SerializeField]
    [Min(0)]
    [Tooltip("0 selects the first layer, 1 the second, and so on. " +
         "The available layers are read from manifest.json.")]
    private int layerIndex = 0;

    [SerializeField]
    private Camera targetCamera;

    private string selectedLayerDirectory;
    private string selectedLayerRootPath;

    private const string LayerIndexPreferenceKey = "StarsLODLoader.LayerIndex.ZeroBased";

    private int availableLayerCount;
    private bool layerSwitchPending;
    private int pendingLayerIndex = -1;

    // ========================================================
    // NODE SELECTION
    // ========================================================

    [Header("Node Selection")]

    [SerializeField]
    private bool frustumCulling = true;

    // ========================================================
    // CLUSTER FILTERING
    // ========================================================

    [Header("Cluster Filtering")]

    [SerializeField]
    private ClusterFilter clusterFilter;

    // ========================================================
    // STAR FILTERING
    // ========================================================

    [Header("Star Property Filtering")]

    [SerializeField]
    private GaiaStarFilter starFilter;

    // ========================================================
    // STREAMING
    // ========================================================

    [Header("Streaming")]

    [SerializeField]
    private float streamUpdateDistance = 1.0f;

    [SerializeField]
    private float streamUpdateAngle = 2.0f;

    [SerializeField]
    [Min(1)]
    private int maxConcurrentLoads = 10;

    private const string MaxConcurrentLoadsPreferenceKey =
        "StarsLODLoader.MaxConcurrentLoads";

    public const int MaxAllowedConcurrentLoads = 64;

    // ========================================================
    // DEBUG
    // ========================================================

    [Header("Debug")]

    [SerializeField]
    private bool verboseLogging = false;

    // ========================================================
    // CONSTANTS
    // ========================================================

    private const int STAR_GPU_SIZE = 20;
    private const int STAR_DISK_SIZE = 36;
    private const int SOURCE_ID_SIZE = 8;

    private const int METADATA_HEADER_SIZE = 44;
    private const int METADATA_NODE_SIZE = 80;

    private const string METADATA_MAGIC = "MSLOD001";
    private const uint METADATA_VERSION = 1;

    // ========================================================
    // NODE METADATA
    // ========================================================

    private readonly List<LODNode> nodes =
        new List<LODNode>();

    private readonly HashSet<uint> desiredNodeIds =
        new HashSet<uint>();

    private const int ROOT_NODE_ID = 0;

    private bool isReady;

    private uint totalLayerStarCount;

    // ========================================================
    // LOADED / LOADING NODES
    // ========================================================

    private readonly Dictionary<uint, LoadedNode> loadedNodes =
        new Dictionary<uint, LoadedNode>();

    private readonly HashSet<uint> loadingNodes =
        new HashSet<uint>();

    private readonly List<PendingLoad> loadingTasks =
        new List<PendingLoad>();

    // ========================================================
    // STREAMING STATE
    // ========================================================

    private Vector3 lastStreamPosition;
    private Quaternion lastStreamRotation;

    private int activeFilterRevision;
    private int observedClusterFilterRevision;
    private int observedStarFilterRevision;

    // ========================================================
    // DEBUG LOG STATE
    // ========================================================

    private int lastLoggedDesiredCount = -1;
    private int lastLoggedLoadedCount = -1;
    private string lastLoggedDepths = "";

    // ========================================================
    // PUBLIC INFORMATION
    // ========================================================

    public bool IsReady => isReady;

    public int NodeCount => nodes.Count;

    public int RootNodeId => ROOT_NODE_ID;

    public int LayerIndex => layerSwitchPending ? pendingLayerIndex : layerIndex;

    public int AvailableLayerCount => availableLayerCount;

    public string SelectedLayerDirectory =>
        selectedLayerDirectory;

    public uint TotalLayerStarCount =>
        totalLayerStarCount;

    public int FilterRevision =>
        activeFilterRevision;

    public Camera TargetCamera =>
        targetCamera;

    public IReadOnlyCollection<uint> DesiredNodeIds =>
        desiredNodeIds;

    public IReadOnlyCollection<uint> LoadedNodeIds =>
        loadedNodes.Keys;

    // ========================================================
    // UNITY
    // ========================================================

    private void Awake()
    {
        int savedMaxConcurrentLoads =
            PlayerPrefs.GetInt(
                MaxConcurrentLoadsPreferenceKey,
                maxConcurrentLoads
            );

        maxConcurrentLoads = Mathf.Clamp(
            savedMaxConcurrentLoads,
            1,
            MaxAllowedConcurrentLoads
        );

        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        if (targetCamera == null)
        {
            Debug.LogError(
                "StarsLODLoader: No target camera assigned."
            );

            return;
        }

        layerIndex = Mathf.Max(
            0,
            PlayerPrefs.GetInt(
                LayerIndexPreferenceKey,
                layerIndex
            )
        );

        if (!LoadMetadata())
        {
            return;
        }

        isReady = true;
    }

    private void Start()
    {
        if (!isReady)
        {
            Debug.LogError(
                "StarsLODLoader: Loader is not ready."
            );

            return;
        }

        if (targetCamera == null)
        {
            Debug.LogError(
                "StarsLODLoader: No target camera assigned."
            );

            return;
        }

        if (clusterFilter == null)
        {
            clusterFilter = GetComponent<ClusterFilter>();
        }

        if (clusterFilter == null)
        {
            clusterFilter =
                FindAnyObjectByType<ClusterFilter>();
        }

        if (starFilter == null)
        {
            starFilter = GetComponent<GaiaStarFilter>();
        }

        if (starFilter == null)
        {
            starFilter =
                FindAnyObjectByType<GaiaStarFilter>();
        }

        observedClusterFilterRevision =
            clusterFilter != null
                ? clusterFilter.Revision
                : 0;

        observedStarFilterRevision =
            starFilter != null
                ? starFilter.Revision
                : 0;

        activeFilterRevision = 0;

        lastStreamPosition =
            targetCamera.transform.position;

        lastStreamRotation =
            targetCamera.transform.rotation;

        UpdateStreaming();
    }

    private void Update()
    {
        if (!isReady || targetCamera == null)
        {
            return;
        }

        if (layerSwitchPending)
        {
            ProcessPendingLayerSwitch();

            if (layerSwitchPending)
                return;
        }

        HandleFilterChanges();

        ProcessCompletedLoads();

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
            lastStreamPosition = currentPosition;
            lastStreamRotation = currentRotation;

            UpdateStreaming();
        }

        StartPendingLoads();
    }

    // ========================================================
    // LAYER MANIFEST
    // ========================================================

    [Serializable]
    private class LayerManifest
    {
        public LayerManifestEntry[] layers;
    }

    [Serializable]
    private class LayerManifestEntry
    {
        public int layer_index;
        public string directory;
        public int actual_stars;
        public int requested_top_stars;
    }

    // ========================================================
    // METADATA
    // ========================================================

    private bool LoadMetadata()
    {
        string lodRootPath =
            Path.Combine(
                DataPath.Root,
                metadataDirectoryDataPath
            );

        string manifestPath =
            Path.Combine(
                lodRootPath,
                "manifest.json"
            );

        if (!File.Exists(manifestPath))
        {
            Debug.LogError(
                "StarsLODLoader: Layer manifest not found:\n" +
                manifestPath
            );

            return false;
        }

        try
        {
            LayerManifest manifest =
                JsonUtility.FromJson<LayerManifest>(
                    File.ReadAllText(manifestPath)
                );

            if (manifest == null ||
                manifest.layers == null ||
                manifest.layers.Length == 0)
            {
                Debug.LogError(
                    "StarsLODLoader: Layer manifest is invalid."
                );

                return false;
            }

            availableLayerCount = manifest.layers.Length;

            if (availableLayerCount < 1)
            {
                Debug.LogError(
                    "StarsLODLoader: Manifest contains no layers."
                );

                return false;
            }

            layerIndex = Mathf.Clamp(
                layerIndex,
                0,
                availableLayerCount - 1
            );

            LayerManifestEntry selectedLayer = null;

            foreach (LayerManifestEntry entry in manifest.layers)
            {
                if (entry.layer_index == layerIndex)
                {
                    selectedLayer = entry;
                    break;
                }
            }

            if (selectedLayer == null)
            {
                Debug.LogError(
                    $"StarsLODLoader: Layer {layerIndex} does not exist.\n" +
                    $"Available layers: {manifest.layers.Length}"
                );

                return false;
            }

            if (string.IsNullOrWhiteSpace(selectedLayer.directory))
            {
                Debug.LogError(
                    $"StarsLODLoader: Layer {layerIndex} has no directory."
                );

                return false;
            }

            selectedLayerDirectory =
                selectedLayer.directory;

            selectedLayerRootPath =
                Path.Combine(
                    lodRootPath,
                    selectedLayerDirectory
                );

            string metadataPath =
                Path.Combine(
                    selectedLayerRootPath,
                    "metadata.bin"
                );

            if (!File.Exists(metadataPath))
            {
                Debug.LogError(
                    "StarsLODLoader: Metadata file not found:\n" +
                    metadataPath
                );

                return false;
            }

            using FileStream file =
                File.OpenRead(metadataPath);

            using BinaryReader reader =
                new BinaryReader(file);

            if (file.Length < METADATA_HEADER_SIZE)
            {
                Debug.LogError(
                    "StarsLODLoader: Metadata header is incomplete."
                );

                return false;
            }

            // ------------------------------------------------
            // Header
            // ------------------------------------------------

            string magic =
                Encoding.ASCII.GetString(
                    reader.ReadBytes(8)
                );

            if (magic != METADATA_MAGIC)
            {
                Debug.LogError(
                    "StarsLODLoader: Invalid metadata magic: " +
                    magic
                );

                return false;
            }

            uint version =
                reader.ReadUInt32();

            if (version != METADATA_VERSION)
            {
                Debug.LogError(
                    "StarsLODLoader: Unsupported metadata version: " +
                    version
                );

                return false;
            }

            uint nodeCount =
                reader.ReadUInt32();

            ulong metadataTotalStars =
                reader.ReadUInt64();

            uint maxStarsPerNode =
                reader.ReadUInt32();

            // Root minimum coordinates.
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();

            float rootSize =
                reader.ReadSingle();

            if (nodeCount == 0 ||
                nodeCount > int.MaxValue)
            {
                Debug.LogError(
                    $"StarsLODLoader: Invalid node count: {nodeCount}."
                );

                return false;
            }

            long expectedMetadataLength =
                METADATA_HEADER_SIZE +
                (long)nodeCount * METADATA_NODE_SIZE;

            if (file.Length != expectedMetadataLength)
            {
                Debug.LogError(
                    "StarsLODLoader: Metadata file size mismatch.\n" +
                    $"Expected: {expectedMetadataLength:N0} bytes\n" +
                    $"Actual: {file.Length:N0} bytes"
                );

                return false;
            }

            List<LODNode> loadedMetadata =
                new List<LODNode>((int)nodeCount);

            // ------------------------------------------------
            // Nodes
            // ------------------------------------------------

            for (uint i = 0; i < nodeCount; i++)
            {
                LODNode node = new LODNode();

                node.nodeId =
                    reader.ReadUInt32();

                node.center = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()
                );

                node.halfSize = new Vector3(
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

                node.children = new int[8];

                int actualChildCount = 0;

                for (int c = 0; c < 8; c++)
                {
                    int childId =
                        reader.ReadInt32();

                    node.children[c] = childId;

                    if (childId >= 0)
                    {
                        if ((uint)childId >= nodeCount)
                        {
                            Debug.LogError(
                                $"StarsLODLoader: Node {i} references " +
                                $"invalid child ID {childId}."
                            );

                            return false;
                        }

                        actualChildCount++;
                    }
                    else if (childId != -1)
                    {
                        Debug.LogError(
                            $"StarsLODLoader: Node {i} has an invalid " +
                            $"child ID {childId}."
                        );

                        return false;
                    }
                }

                if (node.nodeId != i)
                {
                    Debug.LogError(
                        $"StarsLODLoader: Expected node ID {i}, " +
                        $"found {node.nodeId}."
                    );

                    return false;
                }

                if (node.childCount != actualChildCount)
                {
                    Debug.LogError(
                        $"StarsLODLoader: Node {i} child-count mismatch."
                    );

                    return false;
                }

                // This builder stores particles only in leaves.
                if (node.childCount > 0 &&
                    node.localContentCount > 0)
                {
                    Debug.LogError(
                        $"StarsLODLoader: Internal node {i} unexpectedly " +
                        "contains local particle records."
                    );

                    return false;
                }

                loadedMetadata.Add(node);
            }

            // ------------------------------------------------
            // Metadata validation
            // ------------------------------------------------

            if (loadedMetadata.Count == 0)
            {
                Debug.LogError(
                    "StarsLODLoader: Metadata contains no nodes."
                );

                return false;
            }

            if (loadedMetadata[ROOT_NODE_ID].cumulativeContentCount
                != metadataTotalStars)
            {
                Debug.LogError(
                    "StarsLODLoader: Root cumulative star count does " +
                    "not match the metadata header."
                );

                return false;
            }

            if (selectedLayer.actual_stars > 0 &&
                (ulong)selectedLayer.actual_stars != metadataTotalStars)
            {
                Debug.LogError(
                    "StarsLODLoader: Layer star count in manifest does " +
                    "not match metadata."
                );

                return false;
            }

            nodes.Clear();
            nodes.AddRange(loadedMetadata);

            totalLayerStarCount =
                metadataTotalStars > uint.MaxValue
                    ? uint.MaxValue
                    : (uint)metadataTotalStars;

            Debug.Log(
                $"StarsLODLoader: Loaded layer {layerIndex} " +
                $"({selectedLayerDirectory}). " +
                $"Stars={metadataTotalStars:N0}, " +
                $"Nodes={nodeCount:N0}, " +
                $"MaxStarsPerLeaf={maxStarsPerNode:N0}, " +
                $"RootSize={rootSize:F3} pc"
            );

            return true;
        }
        catch (Exception e)
        {
            Debug.LogError(
                "StarsLODLoader: Failed to read metadata:\n" +
                e
            );

            return false;
        }
    }

    // ========================================================
    // NODE SELECTION
    // ========================================================

    public void UpdateSelection()
    {
        if (!isReady ||
            targetCamera == null ||
            nodes.Count == 0)
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
            ROOT_NODE_ID,
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

        LODNode node = nodes[nodeId];

        Bounds bounds = new Bounds(
            node.center,
            node.halfSize * 2.0f
        );

        // ----------------------------------------------------
        // Frustum culling
        // ----------------------------------------------------

        if (frustumPlanes != null &&
            !GeometryUtility.TestPlanesAABB(
                frustumPlanes,
                bounds))
        {
            return;
        }

        // ----------------------------------------------------
        // Leaf node
        //
        // Only leaves have particle files. Do not stop
        // traversal early based on apparent angular size.
        // ----------------------------------------------------

        if (node.childCount == 0)
        {
            if (node.localContentCount > 0)
            {
                desiredNodeIds.Add(node.nodeId);
            }

            return;
        }

        // ----------------------------------------------------
        // Traverse every visible child
        // ----------------------------------------------------

        for (int i = 0; i < 8; i++)
        {
            int childId = node.children[i];

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

    // ========================================================
    // NODE API
    // ========================================================

    public bool TryGetNodeInfo(
        uint nodeId,
        out StarsLODNodeInfo info)
    {
        if (nodeId >= (uint)nodes.Count)
        {
            info = default;
            return false;
        }

        LODNode node = nodes[(int)nodeId];

        info = new StarsLODNodeInfo
        {
            nodeId = node.nodeId,
            center = node.center,
            halfSize = node.halfSize,
            depth = node.depth,
            starCount = node.localContentCount,
            cumulativeStarCount = node.cumulativeContentCount,
            childCount = node.childCount
        };

        return true;
    }

    public int GetChildNodeId(
        uint nodeId,
        int childIndex)
    {
        if (nodeId >= (uint)nodes.Count ||
            childIndex < 0 ||
            childIndex >= 8)
        {
            return -1;
        }

        return nodes[(int)nodeId].children[childIndex];
    }

    public Bounds GetNodeBounds(uint nodeId)
    {
        if (nodeId >= (uint)nodes.Count)
        {
            return default;
        }

        LODNode node = nodes[(int)nodeId];

        return new Bounds(
            node.center,
            node.halfSize * 2.0f
        );
    }

    // ========================================================
    // FILTER CHANGES
    // ========================================================

    private void HandleFilterChanges()
    {
        int currentClusterRevision =
            clusterFilter != null
                ? clusterFilter.Revision
                : 0;

        int currentStarRevision =
            starFilter != null
                ? starFilter.Revision
                : 0;

        if (currentClusterRevision ==
                observedClusterFilterRevision &&
            currentStarRevision ==
                observedStarFilterRevision)
        {
            return;
        }

        observedClusterFilterRevision =
            currentClusterRevision;

        observedStarFilterRevision =
            currentStarRevision;

        unchecked
        {
            activeFilterRevision++;
        }

        if (verboseLogging)
        {
            Debug.Log(
                "StarsLODLoader: Filter settings changed. " +
                "Reloading loaded nodes."
            );
        }

        InvalidateLoadedNodes();
        UpdateStreaming();
    }

    private GaiaClusterFilterSettings GetCurrentFilterSettings()
    {
        if (clusterFilter == null)
        {
            return default;
        }

        GaiaClusterFilterSettings settings =
            clusterFilter.CreateSnapshot();

        if (clusterFilter.Database == null)
        {
            settings.enabled = false;
        }

        return settings;
    }

    private GaiaStarFilterSettings GetCurrentStarFilterSettings()
    {
        if (starFilter == null)
        {
            return default;
        }

        return starFilter.CreateSnapshot();
    }

    private bool ShouldKeepStar(
        ulong sourceId,
        GaiaClusterFilterSettings settings)
    {
        if (!settings.enabled ||
            clusterFilter == null ||
            clusterFilter.Database == null)
        {
            return true;
        }

        return clusterFilter.Database.StarHasMatchingCluster(
            sourceId,
            settings
        );
    }

    // ========================================================
    // STREAMING
    // ========================================================

    private void UpdateStreaming()
    {
        if (!isReady)
        {
            return;
        }

        UpdateSelection();

        List<uint> unload = new List<uint>();

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

        StartPendingLoads();

        if (verboseLogging)
        {
            LogStreamingState();
        }
    }

    private void StartPendingLoads()
    {
        if (!isReady)
        {
            return;
        }

        if (layerSwitchPending)
            return;

        int availableLoadSlots = Math.Max(
            0,
            maxConcurrentLoads - loadingNodes.Count
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

            if (loadedNodes.ContainsKey(nodeId) ||
                loadingNodes.Contains(nodeId))
            {
                continue;
            }

            StartBackgroundLoad(nodeId);
            availableLoadSlots--;
        }
    }

    // ========================================================
    // BACKGROUND LOADING
    // ========================================================

    private void StartBackgroundLoad(uint nodeId)
    {
        loadingNodes.Add(nodeId);

        GaiaClusterFilterSettings clusterSettings =
            GetCurrentFilterSettings();

        GaiaStarFilterSettings starSettings =
            GetCurrentStarFilterSettings();

        int filterRevision = activeFilterRevision;

        Task<NodeLoadResult> task = Task.Run(
            () => ReadNodeFromDisk(
                nodeId,
                clusterSettings,
                starSettings,
                filterRevision
            )
        );

        loadingTasks.Add(new PendingLoad
        {
            nodeId = nodeId,
            task = task
        });
    }

    private NodeLoadResult ReadNodeFromDisk(
        uint nodeId,
        GaiaClusterFilterSettings filterSettings,
        GaiaStarFilterSettings starFilterSettings,
        int filterRevision)
    {
        NodeLoadResult result = new NodeLoadResult
        {
            nodeId = nodeId,
            filterRevision = filterRevision
        };

        try
        {
            if (!TryGetNodeInfo(
                nodeId,
                out StarsLODNodeInfo node))
            {
                result.errorMessage =
                    $"Invalid LOD node ID: {nodeId}";

                return result;
            }

            uint starCount = node.starCount;

            result.drawBounds = new Bounds(
                node.center,
                node.halfSize * 2.0f
            );

            // If stars are disabled, return no stars for this node.
            if (!starFilterSettings.showStars)
            {
                result.success = true;
                result.stars = Array.Empty<GaiaStarGPU>();
                return result;
            }

            // Internal nodes contain no particles, so they need no file.
            if (starCount == 0)
            {
                result.success = true;
                result.stars = Array.Empty<GaiaStarGPU>();
                return result;
            }

            if (starCount > int.MaxValue)
            {
                result.errorMessage =
                    $"Node {nodeId} contains too many local stars.";

                return result;
            }

            string particlePath = Path.Combine(
                selectedLayerRootPath,
                "particles",
                $"particles_{nodeId:00000000}.bin"
            );

            if (!File.Exists(particlePath))
            {
                result.errorMessage =
                    "StarsLODLoader: Particle file not found:\n" +
                    particlePath;

                return result;
            }

            long particleFileSize =
                new FileInfo(particlePath).Length;

            long expectedParticleSize =
                (long)starCount * STAR_DISK_SIZE;

            if (particleFileSize != expectedParticleSize)
            {
                result.errorMessage =
                    "StarsLODLoader: Particle file size mismatch.\n" +
                    $"Node: {nodeId}\n" +
                    $"Expected: {expectedParticleSize:N0} bytes\n" +
                    $"Actual: {particleFileSize:N0} bytes";

                return result;
            }

            bool useClusterFilter =
                filterSettings.enabled &&
                clusterFilter != null &&
                clusterFilter.Database != null;

            bool useStarFilter =
                starFilterSettings.IsActive;

            bool useAnyFilter =
                useClusterFilter || useStarFilter;

            byte[] particleData =
                File.ReadAllBytes(particlePath);

            byte[] sourceIdData = null;

            // ------------------------------------------------
            // Load source IDs only when cluster filtering is active
            // ------------------------------------------------

            if (useClusterFilter)
            {
                string sourceIdPath = Path.Combine(
                    selectedLayerRootPath,
                    "source_ids",
                    $"source_ids_{nodeId:00000000}.bin"
                );

                if (!File.Exists(sourceIdPath))
                {
                    result.errorMessage =
                        "StarsLODLoader: Source ID file not found:\n" +
                        sourceIdPath;

                    return result;
                }

                long sourceIdFileSize =
                    new FileInfo(sourceIdPath).Length;

                long expectedSourceIdSize =
                    (long)starCount * SOURCE_ID_SIZE;

                if (sourceIdFileSize != expectedSourceIdSize)
                {
                    result.errorMessage =
                        "StarsLODLoader: Source ID file size mismatch.\n" +
                        $"Node: {nodeId}\n" +
                        $"Expected: {expectedSourceIdSize:N0} bytes\n" +
                        $"Actual: {sourceIdFileSize:N0} bytes";

                    return result;
                }

                sourceIdData = File.ReadAllBytes(sourceIdPath);
            }

            // ------------------------------------------------
            // Fast path: no filters
            // ------------------------------------------------

            if (!useAnyFilter)
            {
                GaiaStarGPU[] stars =
                    new GaiaStarGPU[(int)starCount];

                using (MemoryStream stream =
                    new MemoryStream(particleData))
                using (BinaryReader reader =
                    new BinaryReader(stream))
                {
                    for (int i = 0; i < stars.Length; i++)
                    {
                        stars[i] = ReadStarForGpu(reader);
                    }
                }

                result.success = true;
                result.stars = stars;
                return result;
            }

            // ------------------------------------------------
            // Filtered path
            // ------------------------------------------------

            List<GaiaStarGPU> filteredStars =
                new List<GaiaStarGPU>((int)starCount);

            using (MemoryStream particleStream =
                new MemoryStream(particleData))
            using (BinaryReader particleReader =
                new BinaryReader(particleStream))
            using (BinaryReader sourceIdReader =
                useClusterFilter
                    ? new BinaryReader(
                        new MemoryStream(sourceIdData))
                    : null)
            {
                for (int i = 0; i < (int)starCount; i++)
                {
                    GaiaStarGPU star = ReadStar(
                        particleReader,
                        out GaiaStarProperties properties
                    );

                    bool keep = true;

                    if (useStarFilter)
                    {
                        keep = starFilterSettings.Matches(
                            star.magnitude,
                            properties.massSolar,
                            properties.ageGyr,
                            properties.evolutionStage,
                            properties.temperatureK
                        );
                    }

                    if (useClusterFilter)
                    {
                        // Read one ID per particle, even if the star
                        // has already failed the stellar-property filter.
                        ulong sourceId =
                            sourceIdReader.ReadUInt64();

                        if (keep &&
                            !ShouldKeepStar(
                                sourceId,
                                filterSettings))
                        {
                            keep = false;
                        }
                    }

                    if (keep)
                    {
                        filteredStars.Add(star);
                    }
                }
            }

            result.success = true;
            result.stars = filteredStars.ToArray();

            return result;
        }
        catch (Exception e)
        {
            result.success = false;
            result.errorMessage =
                $"StarsLODLoader: Failed loading node {nodeId}:\n{e}";

            return result;
        }
    }

    // ========================================================
    // DISK STAR READING
    // ========================================================

    private static GaiaStarGPU ReadStar(
        BinaryReader reader,
        out GaiaStarProperties properties)
    {
        Vector3 position = new Vector3(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle()
        );

        float magnitude = reader.ReadSingle();
        float bpRp = reader.ReadSingle();

        float temperatureK = reader.ReadSingle();
        float massSolar = reader.ReadSingle();
        float ageGyr = reader.ReadSingle();
        int evolutionStage = reader.ReadInt32();

        properties = new GaiaStarProperties
        {
            temperatureK = temperatureK,
            massSolar = massSolar,
            ageGyr = ageGyr,
            evolutionStage = evolutionStage
        };

        return new GaiaStarGPU
        {
            position = position,
            magnitude = magnitude,
            bpRp = bpRp
        };
    }

    private static GaiaStarGPU ReadStarForGpu(BinaryReader reader)
    {
        Vector3 position = new Vector3(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle()
        );

        float magnitude = reader.ReadSingle();
        float bpRp = reader.ReadSingle();

        // Skip Teff, mass, age and evolution stage.
        reader.BaseStream.Seek(16, SeekOrigin.Current);

        return new GaiaStarGPU
        {
            position = position,
            magnitude = magnitude,
            bpRp = bpRp
        };
    }

    // ========================================================
    // COMPLETED LOADS
    // ========================================================

    private void ProcessCompletedLoads()
    {
        for (int i = loadingTasks.Count - 1; i >= 0; i--)
        {
            PendingLoad pending = loadingTasks[i];

            if (!pending.task.IsCompleted)
            {
                continue;
            }

            loadingTasks.RemoveAt(i);
            loadingNodes.Remove(pending.nodeId);

            NodeLoadResult result;

            try
            {
                result = pending.task.Result;
            }
            catch (Exception e)
            {
                Debug.LogError(
                    "StarsLODLoader: Background load failed:\n" + e
                );

                continue;
            }

            if (!result.success)
            {
                Debug.LogError(result.errorMessage);
                continue;
            }

            // Ignore results computed using outdated filter settings.
            if (result.filterRevision != activeFilterRevision)
            {
                continue;
            }

            // The camera may have moved while this node was loading.
            if (!desiredNodeIds.Contains(result.nodeId))
            {
                continue;
            }

            if (loadedNodes.ContainsKey(result.nodeId))
            {
                continue;
            }

            GraphicsBuffer buffer = null;

            if (result.stars.Length > 0)
            {
                buffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    result.stars.Length,
                    STAR_GPU_SIZE
                );

                buffer.SetData(result.stars);
            }

            LoadedNode loaded = new LoadedNode
            {
                nodeId = result.nodeId,
                starCount = result.stars.Length,
                buffer = buffer,
                drawBounds = result.drawBounds
            };

            loadedNodes.Add(result.nodeId, loaded);

            if (verboseLogging)
            {
                Debug.Log(
                    $"StarsLODLoader: Loaded node {result.nodeId}: " +
                    $"{result.stars.Length:N0} stars."
                );
            }
        }
    }

    // ========================================================
    // UNLOAD
    // ========================================================

    private void UnloadNode(uint nodeId)
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

        loadedNodes.Remove(nodeId);

        if (verboseLogging)
        {
            Debug.Log(
                $"StarsLODLoader: Unloaded node {nodeId}."
            );
        }
    }

    private void InvalidateLoadedNodes()
    {
        foreach (LoadedNode node in loadedNodes.Values)
        {
            if (node != null && node.buffer != null)
            {
                node.buffer.Release();
                node.buffer = null;
            }
        }

        loadedNodes.Clear();
    }

    // ========================================================
    // LOGGING
    // ========================================================

    private void LogSelectionState()
    {
        int desiredCount = desiredNodeIds.Count;

        int[] depthCounts = new int[32];

        foreach (uint nodeId in desiredNodeIds)
        {
            if (nodeId >= (uint)nodes.Count)
            {
                continue;
            }

            int depth = (int)nodes[(int)nodeId].depth;

            if (depth >= 0 && depth < depthCounts.Length)
            {
                depthCounts[depth]++;
            }
        }

        string depthSummary = "";

        for (int depth = 0; depth < depthCounts.Length; depth++)
        {
            if (depthCounts[depth] <= 0)
            {
                continue;
            }

            if (depthSummary.Length > 0)
            {
                depthSummary += ", ";
            }

            depthSummary += $"D{depth}={depthCounts[depth]}";
        }

        if (desiredCount != lastLoggedDesiredCount ||
            depthSummary != lastLoggedDepths)
        {
            lastLoggedDesiredCount = desiredCount;
            lastLoggedDepths = depthSummary;

            Debug.Log(
                $"StarsLODLoader: Desired={desiredCount}, " +
                $"Depths=[{depthSummary}]"
            );
        }
    }

    private void LogStreamingState()
    {
        int desiredCount = desiredNodeIds.Count;
        int loadedCount = loadedNodes.Count;

        int totalLoadedStars = 0;

        foreach (LoadedNode node in loadedNodes.Values)
        {
            if (node != null)
            {
                totalLoadedStars += node.starCount;
            }
        }

        if (desiredCount != lastLoggedDesiredCount ||
            loadedCount != lastLoggedLoadedCount)
        {
            lastLoggedDesiredCount = desiredCount;
            lastLoggedLoadedCount = loadedCount;

            Debug.Log(
                $"StarsLODLoader: Layer={layerIndex}, " +
                $"Desired={desiredCount}, " +
                $"Loaded={loadedCount}, " +
                $"Loading={loadingNodes.Count}, " +
                $"LoadedStars={totalLoadedStars:N0}"
            );
        }
    }

    // ========================================================
    // BUFFER PROVIDER
    // ========================================================

    public override void GetLoadedStarBuffers(
        List<GraphicsBuffer> buffers,
        List<int> counts,
        List<float> fades)
    {
        buffers.Clear();
        counts.Clear();
        fades.Clear();

        foreach (LoadedNode node in loadedNodes.Values)
        {
            if (node == null ||
                node.buffer == null ||
                node.starCount <= 0 ||
                node.nodeId >= (uint)nodes.Count)
            {
                continue;
            }

            buffers.Add(node.buffer);
            counts.Add(node.starCount);

            // The selected layer is the resolution level.
            // No parent/child particle cross-fading is required.
            fades.Add(1.0f);
        }
    }

    // ========================================================
    // CLEANUP
    // ========================================================

    private void OnDestroy()
    {
        isReady = false;

        foreach (LoadedNode node in loadedNodes.Values)
        {
            if (node != null && node.buffer != null)
            {
                node.buffer.Release();
                node.buffer = null;
            }
        }

        loadedNodes.Clear();
        loadingNodes.Clear();
        loadingTasks.Clear();
    }

    // ========================================================
    // INTERNAL NODE DATA
    // ========================================================

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

    // ========================================================
    // LOADED NODE
    // ========================================================

    private class LoadedNode
    {
        public uint nodeId;
        public int starCount;

        public GraphicsBuffer buffer;
        public Bounds drawBounds;
    }

    // ========================================================
    // PENDING LOAD
    // ========================================================

    private class PendingLoad
    {
        public uint nodeId;
        public Task<NodeLoadResult> task;
    }

    // ========================================================
    // BACKGROUND LOAD RESULT
    // ========================================================

    private class NodeLoadResult
    {
        public bool success;

        public uint nodeId;

        public GaiaStarGPU[] stars;

        public Bounds drawBounds;

        public string errorMessage;

        public int filterRevision;
    }

    // ========================================================
    // PUBLIC NODE METADATA
    // ========================================================

    public struct StarsLODNodeInfo
    {
        public uint nodeId;

        public Vector3 center;
        public Vector3 halfSize;

        public uint depth;

        // Stars stored directly in this node.
        // Internal nodes have a count of zero.
        public uint starCount;

        // Stars stored in this node and all its descendants.
        public ulong cumulativeStarCount;

        public uint childCount;
    }

    // ========================================================
    // PUBLIC GPU STAR FORMAT
    // ========================================================

    public struct GaiaStarGPU
    {
        public Vector3 position;
        public float magnitude;
        public float bpRp;
    }

    private struct GaiaStarProperties
    {
        public float temperatureK;
        public float massSolar;
        public float ageGyr;
        public int evolutionStage;
    }

    // ========================================================
    // MAXIMUM CONCURRENT LOADS
    // ========================================================

    public int MaxConcurrentLoads => maxConcurrentLoads;

    public void SetMaxConcurrentLoads(int value)
    {
        maxConcurrentLoads = Mathf.Clamp(
            value,
            1,
            MaxAllowedConcurrentLoads
        );
    }

    public void CommitMaxConcurrentLoads()
    {
        PlayerPrefs.SetInt(
            MaxConcurrentLoadsPreferenceKey,
            maxConcurrentLoads
        );

        PlayerPrefs.Save();

        if (isReady)
        {
            StartPendingLoads();
        }
    }

    public bool SetLayerIndex(int value)
    {
        if (!isReady ||
            value < 0 ||
            value >= availableLayerCount)
        {
            return false;
        }

        if (layerSwitchPending
                ? value == pendingLayerIndex
                : value == layerIndex)
        {
            return true;
        }

        pendingLayerIndex = value;
        layerSwitchPending = true;

        desiredNodeIds.Clear();

        // Switch immediately if no background loads are active.
        ProcessPendingLayerSwitch();

        return true;
    }

    public void CommitLayerIndex()
    {
        int value = layerSwitchPending
            ? pendingLayerIndex
            : layerIndex;

        if (value < 0 || value >= availableLayerCount)
            return;

        PlayerPrefs.SetInt(
            LayerIndexPreferenceKey,
            value
        );

        PlayerPrefs.Save();
    }

    private void ProcessPendingLayerSwitch()
    {
        if (!layerSwitchPending)
            return;

        // Let existing background reads finish before changing
        // metadata or the selected layer directory.
        foreach (PendingLoad pending in loadingTasks)
        {
            if (!pending.task.IsCompleted)
                return;
        }

        // Observe background task exceptions and discard old results.
        foreach (PendingLoad pending in loadingTasks)
        {
            if (pending.task.IsFaulted)
            {
                _ = pending.task.Exception;
            }
        }

        loadingTasks.Clear();
        loadingNodes.Clear();

        InvalidateLoadedNodes();
        desiredNodeIds.Clear();

        int previousLayer = layerIndex;
        int requestedLayer = pendingLayerIndex;

        layerIndex = requestedLayer;

        if (!LoadMetadata())
        {
            // Restore the previous layer if the requested layer
            // cannot be loaded.
            layerIndex = previousLayer;

            bool restored = LoadMetadata();

            layerSwitchPending = false;
            pendingLayerIndex = -1;
            isReady = restored;

            PlayerPrefs.SetInt(
                LayerIndexPreferenceKey,
                previousLayer
            );

            PlayerPrefs.Save();

            if (!restored)
            {
                Debug.LogError(
                    "StarsLODLoader: Failed to switch layers and " +
                    "failed to restore the previous layer."
                );

                return;
            }

            Debug.LogError(
                $"StarsLODLoader: Could not load layer {requestedLayer}. " +
                $"Restored layer {previousLayer}."
            );
        }
        else
        {
            layerSwitchPending = false;
            pendingLayerIndex = -1;
            isReady = true;
        }

        unchecked
        {
            activeFilterRevision++;
        }

        if (targetCamera != null)
        {
            lastStreamPosition = targetCamera.transform.position;
            lastStreamRotation = targetCamera.transform.rotation;
        }

        UpdateStreaming();
    }
}