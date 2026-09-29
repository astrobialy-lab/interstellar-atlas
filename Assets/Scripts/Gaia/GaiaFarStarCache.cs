using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

public class GaiaFarStarCache : MonoBehaviour
{
    // ============================================================
    // References
    // ============================================================

    [Header("References")]

    [SerializeField]
    private GaiaMSLODLoader lodLoader;

    [SerializeField]
    private GaiaStarRasterizer rasterizer;

    [SerializeField]
    private Camera targetCamera;

    [SerializeField]
    private ComputeShader cacheCompute;

    [SerializeField]
    private Shader cacheCompositeShader;


    // ============================================================
    // Cache texture
    // ============================================================

    [Header("Cache Texture")]

    [SerializeField]
    [Min(256)]
    private int cacheWidth = 2048;

    [SerializeField]
    private FilterMode cacheFilterMode =
        FilterMode.Bilinear;


    // ============================================================
    // Far-field definition
    // ============================================================

    [Header("Far Field")]

    [Tooltip(
        "Stars farther than this distance from the cache position " +
        "are placed into the far cache."
    )]
    [SerializeField]
    [Min(0.0f)]
    private float farCacheDistance = 5000.0f;


    // ============================================================
    // Cache refresh
    // ============================================================

    [Header("Cache Refresh")]

    [Tooltip(
        "Maximum allowed angular displacement of the closest " +
        "cached stars before a new cache is generated."
    )]
    [SerializeField]
    [Min(0.0001f)]
    private float cacheAngularErrorDegrees = 0.02f;


    // ============================================================
    // Far-cache node LOD
    // ============================================================

    [Header("Far Cache LOD")]

    [Tooltip(
        "When an octree node appears smaller than this angle " +
        "from the cache position, traversal stops refining it."
    )]
    [SerializeField]
    [Min(0.001f)]
    private float cacheNodeAngularDiameterDegrees =
        0.35f;


    // ============================================================
    // Runtime cache state
    // ============================================================

    // Currently published cache.
    private RenderTexture cacheTexture;

    // Texture being used as the destination when generating the
    // next cache.
    private RenderTexture pendingCacheTexture;

    // Position around which the currently published cache was
    // generated.
    private Vector3 cachePosition;

    // Position around which the cache currently being generated
    // is being built.
    private Vector3 generationCachePosition;

    private bool cacheValid;

    private bool refreshRequired;

    private bool generationInProgress;

    private bool generationStarted;

    private int generationNodeIndex;

    private int generationNodeCount;

    private Task<GaiaMSLODLoader.GaiaStarGPU[]>
        currentNodeLoadTask;

    private uint currentNodeId;


    // ============================================================
    // Far-cache node list
    // ============================================================

    private readonly List<uint> farCacheNodeIds =
        new List<uint>();


    // ============================================================
    // Temporary node GPU buffer
    // ============================================================

    private GraphicsBuffer nodeStarBuffer;

    private int nodeStarBufferCapacity;


    // ============================================================
    // Cache accumulation buffers
    // ============================================================

    private GraphicsBuffer cacheLuminance;

    private GraphicsBuffer cacheColorLuminance;


    // ============================================================
    // Reconstruction material
    // ============================================================

    private Material cacheCompositeMaterial;


    // ============================================================
    // Compute kernels
    // ============================================================

    private int clearKernel;

    private int rasterizeKernel;


    // ============================================================
    // Shader property IDs
    // ============================================================

    private static readonly int CacheLuminanceID =
        Shader.PropertyToID(
            "_CacheLuminance"
        );

    private static readonly int CacheColorLuminanceID =
        Shader.PropertyToID(
            "_CacheColorLuminance"
        );

    private static readonly int CacheWidthID =
        Shader.PropertyToID(
            "_CacheWidth"
        );

    private static readonly int CacheHeightID =
        Shader.PropertyToID(
            "_CacheHeight"
        );

    private static readonly int StarCountID =
        Shader.PropertyToID(
            "_StarCount"
        );

    private static readonly int CachePositionID =
        Shader.PropertyToID(
            "_CachePosition"
        );

    private static readonly int FarCacheDistanceID =
        Shader.PropertyToID(
            "_FarCacheDistance"
        );

    private static readonly int BrightnessMultiplierID =
        Shader.PropertyToID(
            "_BrightnessMultiplier"
        );

    private static readonly int StorageScaleID =
        Shader.PropertyToID(
            "_StorageScale"
        );

    private static readonly int ColorMinID =
        Shader.PropertyToID(
            "_ColorMin"
        );

    private static readonly int ColorMaxID =
        Shader.PropertyToID(
            "_ColorMax"
        );

    private static readonly int FixedScaleID =
        Shader.PropertyToID(
            "_FixedScale"
        );

    private static readonly int StarsID =
        Shader.PropertyToID(
            "_Stars"
        );

    private static readonly int CompositeCacheWidthID =
        Shader.PropertyToID(
            "_CacheWidth"
        );

    private static readonly int CompositeCacheHeightID =
        Shader.PropertyToID(
            "_CacheHeight"
        );

    private static readonly int CompositeStorageScaleInvID =
        Shader.PropertyToID(
            "_StorageScaleInv"
        );


    // ============================================================
    // Public properties
    // ============================================================

    public RenderTexture CacheTexture
    {
        get
        {
            return cacheTexture;
        }
    }

    public RenderTexture PendingCacheTexture
    {
        get
        {
            return pendingCacheTexture;
        }
    }

    public GraphicsBuffer CacheLuminanceBuffer
    {
        get
        {
            return cacheLuminance;
        }
    }

    public GraphicsBuffer CacheColorLuminanceBuffer
    {
        get
        {
            return cacheColorLuminance;
        }
    }

    public Vector3 CachePosition
    {
        get
        {
            return cachePosition;
        }
    }

    public bool CacheValid
    {
        get
        {
            return cacheValid;
        }
    }

    public bool RefreshRequired
    {
        get
        {
            return refreshRequired;
        }
    }

    public bool GenerationInProgress
    {
        get
        {
            return generationInProgress;
        }
    }

    public int CacheWidth
    {
        get
        {
            return cacheWidth;
        }
    }

    public int CacheHeight
    {
        get
        {
            return cacheWidth / 2;
        }
    }

    public float FarCacheDistance
    {
        get
        {
            return farCacheDistance;
        }
    }

    public float CacheAngularErrorDegrees
    {
        get
        {
            return cacheAngularErrorDegrees;
        }
    }

    public int GenerationNodeIndex
    {
        get
        {
            return generationNodeIndex;
        }
    }

    public int GenerationNodeCount
    {
        get
        {
            return generationNodeCount;
        }
    }


    // ============================================================
    // Unity
    // ============================================================

    private void Awake()
    {
        if (lodLoader == null)
        {
            lodLoader =
                GetComponent<GaiaMSLODLoader>();
        }

        if (rasterizer == null)
        {
            rasterizer =
                GetComponent<GaiaStarRasterizer>();
        }

        if (targetCamera == null)
        {
            targetCamera =
                Camera.main;
        }

        if (lodLoader == null)
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "No GaiaMSLODLoader assigned."
            );

            enabled = false;
            return;
        }

        if (rasterizer == null)
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "No GaiaStarRasterizer assigned."
            );

            enabled = false;
            return;
        }

        if (targetCamera == null)
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "No target camera assigned."
            );

            enabled = false;
            return;
        }

        if (cacheCompute == null)
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "No cache ComputeShader assigned."
            );

            enabled = false;
            return;
        }

        if (!SystemInfo.supportsComputeShaders)
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "Compute shaders are not supported on this platform."
            );

            enabled = false;
            return;
        }

        if (!cacheCompute.HasKernel(
                "ClearCacheKernel"))
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "ClearCacheKernel not found."
            );

            enabled = false;
            return;
        }

        if (!cacheCompute.HasKernel(
                "RasterizeCacheKernel"))
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "RasterizeCacheKernel not found."
            );

            enabled = false;
            return;
        }

        clearKernel =
            cacheCompute.FindKernel(
                "ClearCacheKernel"
            );

        rasterizeKernel =
            cacheCompute.FindKernel(
                "RasterizeCacheKernel"
            );

        if (!cacheCompute.IsSupported(
                clearKernel) ||
            !cacheCompute.IsSupported(
                rasterizeKernel))
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "The cache compute shader is not supported " +
                "on this device."
            );

            enabled = false;
            return;
        }

        // --------------------------------------------------------
        // Composite shader
        // --------------------------------------------------------

        if (cacheCompositeShader == null)
        {
            cacheCompositeShader =
                Shader.Find(
                    "Hidden/GaiaFarStarCacheComposite"
                );
        }

        if (cacheCompositeShader == null)
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "Could not find " +
                "'Hidden/GaiaFarStarCacheComposite'."
            );

            enabled = false;
            return;
        }

        cacheCompositeMaterial =
            CoreUtils.CreateEngineMaterial(
                cacheCompositeShader
            );

        if (cacheCompositeMaterial == null)
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "Failed to create cache composite material."
            );

            enabled = false;
            return;
        }

        CreateCacheTextures();

        CreateAccumulationBuffers();

        cachePosition =
            targetCamera.transform.position;

        generationCachePosition =
            cachePosition;

        cacheValid = false;

        refreshRequired = true;
    }


    private void Update()
    {
        if (lodLoader == null ||
            targetCamera == null)
        {
            return;
        }

        // --------------------------------------------------------
        // The loader's metadata is loaded in Start(), so wait
        // until the octree exists.
        // --------------------------------------------------------

        if (!generationStarted &&
            lodLoader.NodeCount > 0)
        {
            BeginRefresh();
        }


        // --------------------------------------------------------
        // Detect movement relative to the currently published
        // cache.
        // --------------------------------------------------------

        if (cacheValid &&
            !generationInProgress)
        {
            Vector3 currentPosition =
                targetCamera.transform.position;

            Vector3 displacement =
                currentPosition -
                cachePosition;

            float movement =
                displacement.magnitude;

            float refreshDistance =
                ComputeRefreshDistance();

            if (movement >= refreshDistance)
            {
                refreshRequired = true;
            }
        }


        // --------------------------------------------------------
        // Start a refresh.
        // --------------------------------------------------------

        if (refreshRequired &&
            !generationInProgress &&
            lodLoader.NodeCount > 0)
        {
            BeginRefresh();
        }


        // --------------------------------------------------------
        // Continue generation.
        // --------------------------------------------------------

        if (generationInProgress)
        {
            ProcessGeneration();
        }
    }


    private void OnDestroy()
    {
        ReleaseNodeBuffer();

        ReleaseAccumulationBuffers();

        ReleaseCacheTextures();

        if (cacheCompositeMaterial != null)
        {
            CoreUtils.Destroy(
                cacheCompositeMaterial
            );

            cacheCompositeMaterial = null;
        }
    }


    // ============================================================
    // Cache textures
    // ============================================================

    private void CreateCacheTextures()
    {
        ReleaseCacheTextures();

        int width =
            Mathf.Max(
                256,
                cacheWidth
            );

        width =
            Mathf.Max(
                2,
                width & ~1
            );

        int height =
            width / 2;

        cacheWidth =
            width;

        cacheTexture =
            CreateCacheRenderTexture(
                width,
                height,
                "Gaia Far Star Cache"
            );

        pendingCacheTexture =
            CreateCacheRenderTexture(
                width,
                height,
                "Gaia Far Star Cache Pending"
            );
    }


    private RenderTexture CreateCacheRenderTexture(
        int width,
        int height,
        string textureName)
    {
        RenderTextureDescriptor descriptor =
            new RenderTextureDescriptor(
                width,
                height
            );

        descriptor.depthBufferBits = 0;
        descriptor.msaaSamples = 1;

        descriptor.graphicsFormat =
            GraphicsFormat.R16G16B16A16_SFloat;

        // The compute shader writes to buffers, not directly
        // to this texture. Therefore random write is not needed.
        descriptor.enableRandomWrite = false;

        descriptor.sRGB = false;

        RenderTexture texture =
            new RenderTexture(
                descriptor
            );

        texture.name =
            textureName;

        texture.filterMode =
            cacheFilterMode;

        texture.wrapMode =
            TextureWrapMode.Repeat;

        texture.useMipMap = false;
        texture.autoGenerateMips = false;

        texture.Create();

        return texture;
    }


    private void ReleaseCacheTextures()
    {
        ReleaseRenderTexture(
            ref cacheTexture
        );

        ReleaseRenderTexture(
            ref pendingCacheTexture
        );
    }


    private static void ReleaseRenderTexture(
        ref RenderTexture texture)
    {
        if (texture == null)
        {
            return;
        }

        if (texture.IsCreated())
        {
            texture.Release();
        }

        Destroy(texture);

        texture = null;
    }


    // ============================================================
    // Accumulation buffers
    // ============================================================

    private void CreateAccumulationBuffers()
    {
        ReleaseAccumulationBuffers();

        long pixelCountLong =
            (long)cacheWidth *
            CacheHeight;

        if (pixelCountLong <= 0 ||
            pixelCountLong > int.MaxValue)
        {
            throw new InvalidOperationException(
                "GaiaFarStarCache: " +
                "Invalid cache pixel count."
            );
        }

        int pixelCount =
            (int)pixelCountLong;

        cacheLuminance =
            new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                pixelCount,
                sizeof(uint)
            );

        cacheColorLuminance =
            new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                pixelCount,
                sizeof(uint)
            );

        cacheLuminance.name =
            "Gaia Far Cache Luminance";

        cacheColorLuminance.name =
            "Gaia Far Cache Color Luminance";
    }


    private void ReleaseAccumulationBuffers()
    {
        if (cacheLuminance != null)
        {
            cacheLuminance.Release();
            cacheLuminance = null;
        }

        if (cacheColorLuminance != null)
        {
            cacheColorLuminance.Release();
            cacheColorLuminance = null;
        }
    }


    // ============================================================
    // Refresh distance
    // ============================================================

    private float ComputeRefreshDistance()
    {
        if (farCacheDistance <= 0.0f)
        {
            return 0.0f;
        }

        float angleRadians =
            cacheAngularErrorDegrees *
            Mathf.Deg2Rad;

        float distance =
            farCacheDistance *
            Mathf.Tan(
                angleRadians
            );

        return Mathf.Max(
            distance,
            0.0001f
        );
    }


    // ============================================================
    // Begin cache generation
    // ============================================================

    private void BeginRefresh()
    {
        if (generationInProgress)
        {
            return;
        }

        if (lodLoader == null ||
            lodLoader.NodeCount <= 0)
        {
            return;
        }

        // --------------------------------------------------------
        // Capture the exact position for THIS generation.
        //
        // This must remain fixed even if the player moves while
        // the cache is being generated.
        // --------------------------------------------------------

        generationCachePosition =
            targetCamera.transform.position;


        // --------------------------------------------------------
        // Find nodes contributing to the entire far sky.
        // --------------------------------------------------------

        farCacheNodeIds.Clear();

        GetFarCacheNodeIds(
            farCacheNodeIds
        );

        generationNodeCount =
            farCacheNodeIds.Count;

        generationNodeIndex = 0;


        // --------------------------------------------------------
        // Clear staging accumulation.
        //
        // The currently published cache texture remains untouched.
        // --------------------------------------------------------

        ClearAccumulationBuffers();


        refreshRequired = false;

        generationInProgress = true;

        generationStarted = true;

        currentNodeLoadTask = null;


        Debug.Log(
            "GaiaFarStarCache: " +
            $"Starting cache generation. " +
            $"Position={generationCachePosition}, " +
            $"Nodes={generationNodeCount}, " +
            $"FarDistance={farCacheDistance}"
        );
    }


    // ============================================================
    // Clear accumulation
    // ============================================================

    private void ClearAccumulationBuffers()
    {
        cacheCompute.SetBuffer(
            clearKernel,
            CacheLuminanceID,
            cacheLuminance
        );

        cacheCompute.SetBuffer(
            clearKernel,
            CacheColorLuminanceID,
            cacheColorLuminance
        );

        cacheCompute.SetInt(
            CacheWidthID,
            cacheWidth
        );

        cacheCompute.SetInt(
            CacheHeightID,
            CacheHeight
        );

        int groupsX =
            Mathf.CeilToInt(
                cacheWidth / 8.0f
            );

        int groupsY =
            Mathf.CeilToInt(
                CacheHeight / 8.0f
            );

        cacheCompute.Dispatch(
            clearKernel,
            groupsX,
            groupsY,
            1
        );
    }


    // ============================================================
    // Generation
    // ============================================================

    private void ProcessGeneration()
    {
        // --------------------------------------------------------
        // All nodes processed.
        // --------------------------------------------------------

        if (generationNodeIndex >=
            generationNodeCount)
        {
            FinishGeneration();
            return;
        }


        // --------------------------------------------------------
        // Start loading next node.
        // --------------------------------------------------------

        if (currentNodeLoadTask == null)
        {
            currentNodeId =
                farCacheNodeIds[
                    generationNodeIndex
                ];

            currentNodeLoadTask =
                lodLoader.LoadNodeStarsAsync(
                    currentNodeId
                );

            return;
        }


        // --------------------------------------------------------
        // Wait for background read.
        // --------------------------------------------------------

        if (!currentNodeLoadTask.IsCompleted)
        {
            return;
        }


        // --------------------------------------------------------
        // Get completed node.
        // --------------------------------------------------------

        GaiaMSLODLoader.GaiaStarGPU[] stars;

        try
        {
            stars =
                currentNodeLoadTask.Result;
        }
        catch (Exception e)
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                $"Failed to load node {currentNodeId}:\n" +
                e
            );

            currentNodeLoadTask = null;

            generationNodeIndex++;

            return;
        }

        currentNodeLoadTask = null;


        // --------------------------------------------------------
        // Empty node.
        // --------------------------------------------------------

        if (stars == null ||
            stars.Length == 0)
        {
            generationNodeIndex++;
            return;
        }


        // --------------------------------------------------------
        // Upload this node into the reusable GPU buffer.
        // --------------------------------------------------------

        EnsureNodeStarBuffer(
            stars.Length
        );

        nodeStarBuffer.SetData(
            stars,
            0,
            0,
            stars.Length
        );


        // --------------------------------------------------------
        // Configure cache rasterization.
        // --------------------------------------------------------

        cacheCompute.SetBuffer(
            rasterizeKernel,
            StarsID,
            nodeStarBuffer
        );

        cacheCompute.SetBuffer(
            rasterizeKernel,
            CacheLuminanceID,
            cacheLuminance
        );

        cacheCompute.SetBuffer(
            rasterizeKernel,
            CacheColorLuminanceID,
            cacheColorLuminance
        );

        cacheCompute.SetInt(
            CacheWidthID,
            cacheWidth
        );

        cacheCompute.SetInt(
            CacheHeightID,
            CacheHeight
        );

        cacheCompute.SetInt(
            StarCountID,
            stars.Length
        );

        cacheCompute.SetVector(
            CachePositionID,
            generationCachePosition
        );

        cacheCompute.SetFloat(
            FarCacheDistanceID,
            farCacheDistance
        );

        cacheCompute.SetFloat(
            BrightnessMultiplierID,
            rasterizer.BrightnessMultiplier
        );

        cacheCompute.SetFloat(
            StorageScaleID,
            rasterizer.StorageScale
        );

        cacheCompute.SetFloat(
            ColorMinID,
            rasterizer.ColorMin
        );

        cacheCompute.SetFloat(
            ColorMaxID,
            rasterizer.ColorMax
        );

        cacheCompute.SetInt(
            FixedScaleID,
            rasterizer.FixedScale
        );


        // --------------------------------------------------------
        // Dispatch one node.
        // --------------------------------------------------------

        int groupsX =
            Mathf.CeilToInt(
                stars.Length / 64.0f
            );

        cacheCompute.Dispatch(
            rasterizeKernel,
            groupsX,
            1,
            1
        );


        generationNodeIndex++;
    }


    // ============================================================
    // Finish generation
    // ============================================================

    private void FinishGeneration()
    {
        generationInProgress = false;

        currentNodeLoadTask = null;


        Debug.Log(
            "GaiaFarStarCache: " +
            $"Finished cache accumulation. " +
            $"Nodes={generationNodeCount}"
        );


        // --------------------------------------------------------
        // Convert the accumulated integer buffers into the HDR
        // equirectangular panorama.
        // --------------------------------------------------------

        ReconstructPendingCache();


        // --------------------------------------------------------
        // Publish the newly generated cache using the exact
        // position at which it was generated.
        // --------------------------------------------------------

        PublishPendingCache(
            generationCachePosition
        );


        // --------------------------------------------------------
        // The player may have moved during the generation process.
        // If the new cache is already outside its allowed movement
        // range, schedule another refresh.
        // --------------------------------------------------------

        if (targetCamera != null)
        {
            float movement =
                Vector3.Distance(
                    targetCamera.transform.position,
                    cachePosition
                );

            if (movement >=
                ComputeRefreshDistance())
            {
                refreshRequired = true;
            }
        }
    }


    // ============================================================
    // HDR reconstruction
    // ============================================================

    private void ReconstructPendingCache()
    {
        if (pendingCacheTexture == null)
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "Pending cache texture is NULL."
            );

            return;
        }

        if (cacheCompositeMaterial == null)
        {
            Debug.LogError(
                "GaiaFarStarCache: " +
                "Cache composite material is NULL."
            );

            return;
        }


        // --------------------------------------------------------
        // Bind accumulated buffers.
        // --------------------------------------------------------

        cacheCompositeMaterial.SetBuffer(
            CacheLuminanceID,
            cacheLuminance
        );

        cacheCompositeMaterial.SetBuffer(
            CacheColorLuminanceID,
            cacheColorLuminance
        );


        cacheCompositeMaterial.SetInt(
            CompositeCacheWidthID,
            cacheWidth
        );

        cacheCompositeMaterial.SetInt(
            CompositeCacheHeightID,
            CacheHeight
        );


        cacheCompositeMaterial.SetFloat(
            CompositeStorageScaleInvID,
            rasterizer.StorageScaleInverse
        );

        cacheCompositeMaterial.SetFloat(
            FixedScaleID,
            rasterizer.FixedScale
        );

        cacheCompositeMaterial.SetFloat(
            ColorMinID,
            rasterizer.ColorMin
        );

        cacheCompositeMaterial.SetFloat(
            ColorMaxID,
            rasterizer.ColorMax
        );


        // --------------------------------------------------------
        // Fullscreen reconstruction.
        //
        // The source texture is irrelevant because the composite
        // shader only reads the structured accumulation buffers.
        // --------------------------------------------------------

        Graphics.Blit(
            Texture2D.whiteTexture,
            pendingCacheTexture,
            cacheCompositeMaterial,
            0
        );


        Debug.Log(
            "GaiaFarStarCache: " +
            "Reconstructed HDR far-star panorama."
        );
    }


    // ============================================================
    // Temporary node GPU buffer
    // ============================================================

    private void EnsureNodeStarBuffer(
        int requiredCapacity)
    {
        if (requiredCapacity <=
            nodeStarBufferCapacity &&
            nodeStarBuffer != null)
        {
            return;
        }

        ReleaseNodeBuffer();

        nodeStarBuffer =
            new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                requiredCapacity,
                20
            );

        nodeStarBuffer.name =
            "Gaia Far Cache Node Stars";

        nodeStarBufferCapacity =
            requiredCapacity;
    }


    private void ReleaseNodeBuffer()
    {
        if (nodeStarBuffer != null)
        {
            nodeStarBuffer.Release();
            nodeStarBuffer = null;
        }

        nodeStarBufferCapacity = 0;
    }


    // ============================================================
    // Far-node selection
    //
    // No camera frustum is used.
    //
    // The cache covers the entire 360° sky.
    // ============================================================

    public void GetFarCacheNodeIds(
        List<uint> output)
    {
        output.Clear();

        if (lodLoader == null ||
            lodLoader.NodeCount <= 0)
        {
            return;
        }

        if (!lodLoader.TryGetNodeInfo(
                (uint)lodLoader.RootNodeId,
                out GaiaMSLODLoader.GaiaLODNodeInfo rootInfo))
        {
            return;
        }

        Stack<uint> stack =
            new Stack<uint>();

        stack.Push(
            rootInfo.nodeId
        );

        while (stack.Count > 0)
        {
            uint nodeId =
                stack.Pop();

            if (!lodLoader.TryGetNodeInfo(
                    nodeId,
                    out GaiaMSLODLoader.GaiaLODNodeInfo node))
            {
                continue;
            }

            Bounds bounds =
                new Bounds(
                    node.center,
                    node.halfSize * 2.0f
                );


            // ----------------------------------------------------
            // Use the position of THIS generation, not the current
            // player's potentially changed position.
            // ----------------------------------------------------

            float farthestDistance =
                ComputeFarthestDistance(
                    bounds,
                    generationCachePosition
                );

            if (farthestDistance <
                farCacheDistance)
            {
                continue;
            }


            // ----------------------------------------------------
            // This node can contribute to the far cache.
            // ----------------------------------------------------

            output.Add(
                node.nodeId
            );


            // ----------------------------------------------------
            // Leaf.
            // ----------------------------------------------------

            if (node.childCount == 0)
            {
                continue;
            }


            // ----------------------------------------------------
            // Stop refining once the node is sufficiently small
            // in angular extent.
            // ----------------------------------------------------

            float angularDiameter =
                ComputeAngularDiameter(
                    bounds,
                    generationCachePosition
                );

            if (angularDiameter <=
                cacheNodeAngularDiameterDegrees)
            {
                continue;
            }


            // ----------------------------------------------------
            // Refine.
            // ----------------------------------------------------

            for (int childIndex = 0;
                 childIndex < 8;
                 childIndex++)
            {
                int childId =
                    lodLoader.GetChildNodeId(
                        node.nodeId,
                        childIndex
                    );

                if (childId < 0)
                {
                    continue;
                }

                stack.Push(
                    (uint)childId
                );
            }
        }
    }


    // ============================================================
    // Angular size
    // ============================================================

    private static float ComputeAngularDiameter(
        Bounds bounds,
        Vector3 observer)
    {
        Vector3 center =
            bounds.center;

        float diameter =
            bounds.extents.magnitude *
            2.0f;

        float distance =
            Vector3.Distance(
                center,
                observer
            );

        if (distance <= 0.000001f)
        {
            return 180.0f;
        }

        float halfAngle =
            Mathf.Atan(
                (diameter * 0.5f) /
                distance
            );

        return
            2.0f *
            halfAngle *
            Mathf.Rad2Deg;
    }


    // ============================================================
    // Farthest distance
    // ============================================================

    private static float ComputeFarthestDistance(
        Bounds bounds,
        Vector3 observer)
    {
        Vector3 min =
            bounds.min;

        Vector3 max =
            bounds.max;

        float maximumSquaredDistance =
            0.0f;

        EvaluateCorner(
            new Vector3(
                min.x,
                min.y,
                min.z
            ),
            observer,
            ref maximumSquaredDistance
        );

        EvaluateCorner(
            new Vector3(
                max.x,
                min.y,
                min.z
            ),
            observer,
            ref maximumSquaredDistance
        );

        EvaluateCorner(
            new Vector3(
                min.x,
                max.y,
                min.z
            ),
            observer,
            ref maximumSquaredDistance
        );

        EvaluateCorner(
            new Vector3(
                max.x,
                max.y,
                min.z
            ),
            observer,
            ref maximumSquaredDistance
        );

        EvaluateCorner(
            new Vector3(
                min.x,
                min.y,
                max.z
            ),
            observer,
            ref maximumSquaredDistance
        );

        EvaluateCorner(
            new Vector3(
                max.x,
                min.y,
                max.z
            ),
            observer,
            ref maximumSquaredDistance
        );

        EvaluateCorner(
            new Vector3(
                min.x,
                max.y,
                max.z
            ),
            observer,
            ref maximumSquaredDistance
        );

        EvaluateCorner(
            new Vector3(
                max.x,
                max.y,
                max.z
            ),
            observer,
            ref maximumSquaredDistance
        );

        return
            Mathf.Sqrt(
                maximumSquaredDistance
            );
    }


    private static void EvaluateCorner(
        Vector3 corner,
        Vector3 observer,
        ref float maximumSquaredDistance)
    {
        Vector3 delta =
            corner -
            observer;

        float squaredDistance =
            delta.sqrMagnitude;

        if (squaredDistance >
            maximumSquaredDistance)
        {
            maximumSquaredDistance =
                squaredDistance;
        }
    }


    // ============================================================
    // Cache state control
    // ============================================================

    public void ForceRefresh()
    {
        refreshRequired = true;

        if (!generationInProgress &&
            lodLoader != null &&
            lodLoader.NodeCount > 0)
        {
            BeginRefresh();
        }
    }


    public void InvalidateCache()
    {
        cacheValid = false;
        refreshRequired = true;
    }


    // ============================================================
    // Publish completed cache
    // ============================================================

    private void PublishPendingCache(
        Vector3 publishedPosition)
    {
        if (pendingCacheTexture == null)
        {
            return;
        }

        RenderTexture oldCache =
            cacheTexture;

        cacheTexture =
            pendingCacheTexture;

        pendingCacheTexture =
            oldCache;

        cachePosition =
            publishedPosition;

        cacheValid = true;

        refreshRequired = false;

        Debug.Log(
            "GaiaFarStarCache: " +
            $"Published new far-star cache at " +
            $"{cachePosition}."
        );
    }


    // ============================================================
    // Debug helpers
    // ============================================================

    public float GetCurrentCacheMovement()
    {
        if (targetCamera == null)
        {
            return 0.0f;
        }

        return Vector3.Distance(
            targetCamera.transform.position,
            cachePosition
        );
    }


    public float GetRefreshDistance()
    {
        return ComputeRefreshDistance();
    }


    public int GetFarCacheNodeCount()
    {
        return farCacheNodeIds.Count;
    }
}