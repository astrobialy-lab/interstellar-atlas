using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class GaiaMSLODLoader : GaiaStarBufferProvider
{
    // ========================================================
    // LOD DATA
    // ========================================================

    [Header("LOD Data")]
    [SerializeField]
    private string metadataDirectoryDataPath = "Gaia/lod";

    [SerializeField]
    private Camera targetCamera;


    // ========================================================
    // LOD SELECTION
    // ========================================================

    [Header("LOD Selection")]
    [SerializeField]
    [Min(0.0f)]
    private float solidAngleThreshold = 2.4e-4f;

    private double solidAngleFadeRange => solidAngleThreshold * 0.5;

    private double solidAnglePreloadRange => solidAngleFadeRange * 0.5;

    [SerializeField]
    private bool frustumCulling = true;


    // ========================================================
    // CLUSTER FILTERING
    // ========================================================

    [Header("Cluster Filtering")]
    [SerializeField]
    private GaiaClusterFilter clusterFilter;

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


    // ========================================================
    // DEBUG
    // ========================================================

    [Header("Debug")]
    [SerializeField]
    private bool verboseLogging = false;


    // ========================================================
    // CONSTANTS
    // ========================================================

    private const string SolidAngleThresholdPreferenceKey = "GaiaMSLODLoader.SolidAngleThreshold";
    private const float MinimumSolidAngleThreshold = 1e-8f;

    private const int STAR_GPU_SIZE = 20;
    private const int STAR_DISK_SIZE = 36;
    private const int SOURCE_ID_SIZE = 8;


    // ========================================================
    // NODE METADATA
    // ========================================================

    private readonly List<LODNode> nodes =
        new List<LODNode>();

    private readonly HashSet<uint> desiredNodeIds =
        new HashSet<uint>();

    private int rootNodeId = 0;
    private bool isReady = false;


    // ========================================================
    // LOADED / LOADING NODES
    // ========================================================

    private readonly Dictionary<uint, LoadedNode> loadedNodes =
        new Dictionary<uint, LoadedNode>();

    private readonly HashSet<uint> loadingNodes =
        new HashSet<uint>();

    private readonly List<Task<NodeLoadResult>> loadingTasks =
        new List<Task<NodeLoadResult>>();


    // ========================================================
    // STREAMING STATE
    // ========================================================

    private Vector3 lastStreamPosition;
    private Quaternion lastStreamRotation;

    private int activeFilterRevision = 0;
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

    public int FilterRevision
    {
        get
        {
            return activeFilterRevision;
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

    public IReadOnlyCollection<uint> LoadedNodeIds
    {
        get
        {
            return loadedNodes.Keys;
        }
    }


    // ========================================================
    // UNITY
    // ========================================================

    private void Awake()
    {
        float savedThreshold = PlayerPrefs.GetFloat(SolidAngleThresholdPreferenceKey, solidAngleThreshold);

        if (!float.IsNaN(savedThreshold) && !float.IsInfinity(savedThreshold) && savedThreshold > 0f)
        {
            solidAngleThreshold = Mathf.Max(MinimumSolidAngleThreshold, savedThreshold);
        }

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
                "GaiaMSLODLoader: Loader is not ready."
            );

            return;
        }

        if (targetCamera == null)
        {
            Debug.LogError(
                "GaiaMSLODLoader: No target camera assigned."
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

        if (starFilter == null)
        {
            starFilter = GetComponent<GaiaStarFilter>();
        }

        if (starFilter == null)
        {
            starFilter = FindAnyObjectByType<GaiaStarFilter>();
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
            targetCamera
                .transform.position;

        lastStreamRotation =
            targetCamera
                .transform.rotation;

        UpdateStreaming();
    }

    private void Update()
    {
        if (!isReady)
        {
            return;
        }

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

        // Recalculate desired nodes only when the
        // camera has moved/rotated enough.
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


    // ========================================================
    // NODE SELECTION
    // ========================================================

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


    // ========================================================
    // NODE FADE
    // ========================================================

    private float ComputeNodeFade(
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


    // ========================================================
    // NODE API
    // ========================================================

    public bool TryGetNodeInfo(
        uint nodeId,
        out GaiaLODNodeInfo info)
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


    // ========================================================
    // METADATA
    // ========================================================

    private bool LoadMetadata()
    {
        string metadataPath =
            Path.Combine(
                DataPath.Root,
                metadataDirectoryDataPath,
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

            // ------------------------------------------------
            // Header
            // ------------------------------------------------

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

            ulong totalContentCount =
                reader.ReadUInt64();

            uint nSigma =
                reader.ReadUInt32();

            // Root minimum is currently not needed by the runtime.
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();

            float rootSize =
                reader.ReadSingle();

            nodes.Clear();

            // ------------------------------------------------
            // Nodes
            // ------------------------------------------------

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

                // MSLOD001:
                // No integrated-light field.
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

            // ------------------------------------------------
            // Validation
            // ------------------------------------------------

            if (nodes.Count != nodeCount)
            {
                Debug.LogError(
                    "GaiaMSLODLoader: Node count mismatch."
                );

                return false;
            }

            if (nodes.Count == 0)
            {
                Debug.LogError(
                    "GaiaMSLODLoader: Metadata contains no nodes."
                );

                return false;
            }

            Debug.Log(
                $"GaiaMSLODLoader: Loaded MSLOD001 metadata. " +
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
                "GaiaMSLODLoader: Failed to read metadata:\n" +
                e
            );

            return false;
        }
    }


    // ========================================================
    // SOLID ANGLE
    // ========================================================

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
                "GaiaMSLODLoader: Filter settings changed. " +
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

        // Do not filter anything when the database failed
        // to load. This keeps the normal Gaia renderer usable.
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


    // ========================================================
    // LOD / STREAMING
    // ========================================================

    private void UpdateStreaming()
    {
        if (!isReady)
        {
            return;
        }

        UpdateSelection();

        StartPendingLoads();

        IReadOnlyCollection<uint> desired =
            DesiredNodeIds;

        List<uint> unload =
            new List<uint>();

        foreach (uint nodeId in loadedNodes.Keys)
        {
            if (!desired.Contains(nodeId))
            {
                unload.Add(nodeId);
            }
        }

        foreach (uint nodeId in unload)
        {
            UnloadNode(nodeId);
        }

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

        IReadOnlyCollection<uint> desired =
            DesiredNodeIds;

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

        foreach (uint nodeId in desired)
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

        Task<NodeLoadResult> task =
            Task.Run(
                () => ReadNodeFromDisk(
                    nodeId,
                    clusterSettings,
                    starSettings,
                    filterRevision
                )
            );

        loadingTasks.Add(task);
    }

    private NodeLoadResult ReadNodeFromDisk(
        uint nodeId,
        GaiaClusterFilterSettings filterSettings,
        GaiaStarFilterSettings starFilterSettings,
        int filterRevision)
    {
        NodeLoadResult result =
            new NodeLoadResult();

        result.nodeId =
            nodeId;

        result.filterRevision =
            filterRevision;

        if (!TryGetNodeInfo(
                nodeId,
                out GaiaLODNodeInfo node))
        {
            result.success = false;

            result.errorMessage =
                $"Invalid LOD node ID: {nodeId}";

            return result;
        }

        uint starCount =
            node.starCount;

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
                DataPath.Root,
                metadataDirectoryDataPath,
                "particles",
                $"particles_{nodeId:00000000}.bin"
            );

        if (!File.Exists(particlePath))
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

        long expectedParticleSize = (long)starCount * STAR_DISK_SIZE;

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


        // ----------------------------------------------------
        // Determine which filters are active
        // ----------------------------------------------------

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

        // ----------------------------------------------------
        // Load source IDs only when cluster filtering is active
        // ----------------------------------------------------

        if (useClusterFilter)
        {
            string sourceIdPath =
                Path.Combine(
                    DataPath.Root,
                    metadataDirectoryDataPath,
                    "source_ids",
                    $"source_ids_{nodeId:00000000}.bin"
                );

            if (!File.Exists(sourceIdPath))
            {
                result.success = false;

                result.errorMessage =
                    "LOD source ID sidecar not found:\n" +
                    sourceIdPath;

                return result;
            }

            long sourceIdFileSize =
                new FileInfo(sourceIdPath).Length;

            long expectedSourceIdSize =
                (long)starCount * SOURCE_ID_SIZE;

            if (sourceIdFileSize != expectedSourceIdSize)
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
                File.ReadAllBytes(sourceIdPath);
        }

        // ----------------------------------------------------
        // Fast path: no filters are active
        // ----------------------------------------------------

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

            result.drawBounds =
                new Bounds(
                    node.center,
                    node.halfSize * 2.0f
                );

            return result;
        }

        // ----------------------------------------------------
        // Filtered path: cluster filter, star filter, or both
        // ----------------------------------------------------

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
            for (int i = 0; i < starCount; i++)
            {
                // Always read the complete 36-byte record,
                // including properties the GPU does not use.
                GaiaStarGPU star =
                    ReadStar(
                        particleReader,
                        out GaiaStarProperties properties
                    );

                bool keep = true;

                // Stellar-property filtering
                if (useStarFilter)
                {
                    keep = starFilterSettings.Matches(
                        properties.massSolar,
                        properties.ageGyr,
                        properties.evolutionStage,
                        properties.temperatureK
                    );
                }

                // Read one source ID for every star whenever
                // cluster filtering is active. This keeps
                // particle records and IDs correctly aligned.
                if (useClusterFilter)
                {
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

        result.stars =
            filteredStars.ToArray();

        result.drawBounds =
            new Bounds(
                node.center,
                node.halfSize * 2.0f
            );

        return result;
    }

    // --------------------------------------------------------
    // Read one 36-byte disk record
    // --------------------------------------------------------

    private static GaiaStarGPU ReadStar(
        BinaryReader reader,
        out GaiaStarProperties properties)
    {
        Vector3 position =
            new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );

        float magnitude =
            reader.ReadSingle();

        float bpRp =
            reader.ReadSingle();

        // The remaining 16 bytes are CPU-side filter data.
        float temperatureK =
            reader.ReadSingle();

        float massSolar =
            reader.ReadSingle();

        float ageGyr =
            reader.ReadSingle();

        int evolutionStage =
            reader.ReadInt32();

        properties = new GaiaStarProperties
        {
            temperatureK = temperatureK,
            massSolar = massSolar,
            ageGyr = ageGyr,
            evolutionStage = evolutionStage
        };

        // Keep the GPU structure at 20 bytes.
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

        // Skip temperature, mass, age and evolution stage.
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
                result =
                    task.Result;
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

            // Filter changed while this node was loading.
            if (result.filterRevision !=
                activeFilterRevision)
            {
                continue;
            }

            // Camera may have moved while loading.
            // If no longer desired, discard the result.
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


    // ========================================================
    // UNLOAD
    // ========================================================

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


    // ========================================================
    // LOGGING
    // ========================================================

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
            depthSummary != lastLoggedDepths)
        {
            lastLoggedDesiredCount =
                desiredCount;

            lastLoggedDepths =
                depthSummary;

            Debug.Log(
                $"GaiaMSLODLoader: " +
                $"Desired={desiredCount}, " +
                $"Depths=[{depthSummary}]"
            );
        }
    }

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


    // ========================================================
    // CLEANUP
    // ========================================================

    private void OnDestroy()
    {
        foreach (
            LoadedNode node
            in loadedNodes.Values)
        {
            if (node != null &&
                node.buffer != null)
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
    // PUBLIC GAIA NODE METADATA
    // ========================================================

    public struct GaiaLODNodeInfo
    {
        public uint nodeId;

        public Vector3 center;
        public Vector3 halfSize;

        public uint depth;
        public uint starCount;
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
    // Solid Angle Threshold
    // ========================================================

    public float SolidAngleThreshold => solidAngleThreshold;

    public void SetSolidAngleThreshold(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return;

        solidAngleThreshold = Mathf.Max(
            MinimumSolidAngleThreshold,
            value
        );
    }

    public void CommitSolidAngleThreshold()
    {
        PlayerPrefs.SetFloat(
            SolidAngleThresholdPreferenceKey,
            solidAngleThreshold
        );

        PlayerPrefs.Save();

        // Recalculate the desired LOD nodes immediately.
        if (isReady && targetCamera != null)
        {
            UpdateStreaming();
        }
    }

    private void SaveSolidAngleThreshold()
    {
        PlayerPrefs.SetFloat(
            SolidAngleThresholdPreferenceKey,
            solidAngleThreshold
        );

        PlayerPrefs.Save();
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused)
            SaveSolidAngleThreshold();
    }

    private void OnApplicationQuit()
    {
        SaveSolidAngleThreshold();
    }
}