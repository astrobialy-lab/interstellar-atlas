using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

public class DustLODLoader : MonoBehaviour
{
    [Header("Data")]
    [SerializeField] private string processedDirectory = "Data/Dust";
    [SerializeField] private int fallbackLOD = 4;
    [SerializeField] private int maxLOD = 8;

    [Header("GPU Brick Pool")]
    [SerializeField] private int maxGpuBricks = 64;
    [SerializeField] private int maxUploadsPerFrame = 2;
    [SerializeField] private int maxConcurrentLoads = 4;

    [Header("Streaming")]
    [SerializeField] private float streamingInterval = 0.20f;
    [SerializeField] private float fineRadiusPc = 96.0f;
    [SerializeField] private float preloadRadiusMultiplier = 1.25f;

    [Header("Extinction Sampling")]
    [SerializeField] private float stepMultiplier = 2.0f;
    [SerializeField] private int maxRaySteps = 128;

    [Tooltip(
        "V-band optical depth for n_H = 1 cm^-3 across 1 pc. " +
        "Approximate Galactic calibration.")]
    [SerializeField] private float tauVPerNHPerPc = 0.0012857f;

    // Retained temporarily for compatibility with existing UI code.
    // This no longer enables volumetric scattering.
    [SerializeField] private bool scatteringEnabled = false;

    private const int ChunkSize = 32;
    private const int BytesPerVoxel = 2;
    private const int BytesPerBrick =
        ChunkSize * ChunkSize * ChunkSize * BytesPerVoxel;

    private const int MaxShaderLevels = 16;
    private const uint IndexVersion = 1;

    private static readonly byte[] IndexMagic =
    {
        (byte)'D', (byte)'U', (byte)'S', (byte)'T',
        (byte)'L', (byte)'O', (byte)'D', (byte)'1'
    };

    private static readonly int PageTableId =
        Shader.PropertyToID("_PageTable");
    private static readonly int BrickPoolId =
        Shader.PropertyToID("_BrickPool");

    private static readonly int LevelParams0Id =
        Shader.PropertyToID("_LevelParams0");
    private static readonly int LevelParams1Id =
        Shader.PropertyToID("_LevelParams1");
    private static readonly int LevelCountId =
        Shader.PropertyToID("_LevelCount");
    private static readonly int FallbackLodId =
        Shader.PropertyToID("_FallbackLod");
    private static readonly int BrickSizeId =
        Shader.PropertyToID("_BrickSize");
    private static readonly int MaxRayStepsId =
        Shader.PropertyToID("_MaxRaySteps");
    private static readonly int StepMultiplierId =
        Shader.PropertyToID("_StepMultiplier");
    private static readonly int ExtinctionScaleId =
        Shader.PropertyToID("_ExtinctionScale");
    private static readonly int VolumeOriginId =
        Shader.PropertyToID("_VolumeOrigin");
    private static readonly int BaseStepId =
        Shader.PropertyToID("_BaseStep");
    private static readonly int VolumeBoundsMinId =
        Shader.PropertyToID("_VolumeBoundsMin");
    private static readonly int VolumeBoundsMaxId =
        Shader.PropertyToID("_VolumeBoundsMax");

    private readonly struct BrickCoord : IEquatable<BrickCoord>
    {
        public readonly int x;
        public readonly int y;
        public readonly int z;

        public BrickCoord(int x, int y, int z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public bool Equals(BrickCoord other) =>
            x == other.x && y == other.y && z == other.z;

        public override bool Equals(object obj) =>
            obj is BrickCoord other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = x;
                hash = hash * 397 ^ y;
                hash = hash * 397 ^ z;
                return hash;
            }
        }
    }

    private readonly struct BrickKey : IEquatable<BrickKey>
    {
        public readonly int level;
        public readonly int x;
        public readonly int y;
        public readonly int z;

        public BrickKey(int level, int x, int y, int z)
        {
            this.level = level;
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public bool Equals(BrickKey other) =>
            level == other.level &&
            x == other.x &&
            y == other.y &&
            z == other.z;

        public override bool Equals(object obj) =>
            obj is BrickKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = level;
                hash = hash * 397 ^ x;
                hash = hash * 397 ^ y;
                hash = hash * 397 ^ z;
                return hash;
            }
        }
    }

    private sealed class LevelInfo
    {
        public float voxelSizePc;
        public Vector3Int dimensions;
        public Vector3Int brickDimensions;
        public int pageOffset;

        public readonly List<BrickCoord> availableBricks =
            new List<BrickCoord>();
    }

    private sealed class LoadedBrick
    {
        public int slot;
        public float lastUsedTime;
    }

    private sealed class LoadResult
    {
        public BrickKey key;
        public byte[] bytes;
        public string error;
    }

    private struct Candidate
    {
        public BrickKey key;
        public float distance;

        public Candidate(BrickKey key, float distance)
        {
            this.key = key;
            this.distance = distance;
        }
    }

    private int[] pageTableCpu;

    private readonly List<LevelInfo> levels =
        new List<LevelInfo>();

    private readonly Dictionary<BrickKey, LoadedBrick> loaded =
        new Dictionary<BrickKey, LoadedBrick>();

    private readonly HashSet<BrickKey> loading =
        new HashSet<BrickKey>();

    private readonly Stack<int> freeSlots =
        new Stack<int>();

    private readonly ConcurrentQueue<LoadResult> completedLoads =
        new ConcurrentQueue<LoadResult>();

    private HashSet<BrickKey> currentDesired =
        new HashSet<BrickKey>();

    private CancellationTokenSource cancellation;
    private string dataRoot;

    private GraphicsBuffer pageTableGpu;
    private Texture2DArray brickPool;
    private Texture2D stagingTexture;
    private byte[] stagingSliceBytes;

    private Vector3 volumeOrigin;
    private Vector3 baseStep;
    private Vector3 volumeBoundsMin;
    private Vector3 volumeBoundsMax;

    private Vector3Int baseDimensions;

    private bool pageTableDirty;
    private bool initialized;
    private bool disposed;

    private float nextStreamingTime;
    private Camera lastCamera;

    // Packed arrays used by DustExtintionPrecompute.compute.
    // Keep these as Vector4 arrays to match the compute shader layout.
    private readonly Vector4[] shaderLevelParams0 =
        new Vector4[MaxShaderLevels];

    private readonly Vector4[] shaderLevelParams1 =
        new Vector4[MaxShaderLevels];

    public GraphicsBuffer PageTableBuffer => pageTableGpu;
    public Texture2DArray BrickPoolTexture => brickPool;

    public bool IsInitialized => initialized && !disposed;
    public int LevelCount => levels.Count;
    public int LoadedBrickCount => loaded.Count;
    public int DesiredBrickCount => currentDesired.Count;

    // Retained temporarily for compatibility with existing UI code.
    // It only changes the saved flag; scattering is no longer rendered.
    public void ToggleScattering()
    {
        scatteringEnabled = !scatteringEnabled;

        Debug.Log(
            $"Dust scattering: {(scatteringEnabled ? "ON" : "OFF")}. " +
            "Scattering rendering is currently disabled.");
    }

    /// <summary>
    /// Updates streaming and prepares the LOD parameters used by the
    /// cumulative-extinction compute shader.
    /// </summary>
    public void PrepareForCamera(Camera camera)
    {
        if (disposed)
            return;

        EnsureInitialized();
        PumpUploads();

        float now = Time.unscaledTime;

        bool streamNow =
            now >= nextStreamingTime || camera != lastCamera;

        if (streamNow && camera != null)
        {
            RebuildStreamingSet(camera);
            nextStreamingTime = now + streamingInterval;
            lastCamera = camera;
        }

        if (pageTableDirty && pageTableGpu != null)
        {
            pageTableGpu.SetData(pageTableCpu);
            pageTableDirty = false;
        }

        UpdateComputeLevelParameters();
    }

    /// <summary>
    /// Binds density bricks and LOD parameters to the extinction compute
    /// kernel through the current RenderGraph command stream.
    /// </summary>
    public bool BindExtinctionComputeParameters(
        ComputeGraphContext context,
        ComputeShader shader,
        int kernel,
        BufferHandle pageTableHandle,
        TextureHandle brickPoolHandle)
    {
        if (context == null ||
            shader == null ||
            disposed ||
            !initialized ||
            pageTableGpu == null ||
            brickPool == null ||
            levels.Count == 0 ||
            !pageTableHandle.IsValid() ||
            !brickPoolHandle.IsValid())
        {
            return false;
        }

        var cmd = context.cmd;

        cmd.SetComputeBufferParam(
            shader, kernel, PageTableId, pageTableHandle);

        cmd.SetComputeTextureParam(
            shader, kernel, BrickPoolId, brickPoolHandle);

        cmd.SetComputeIntParam(
            shader, LevelCountId, levels.Count);

        cmd.SetComputeIntParam(
            shader, FallbackLodId, fallbackLOD);

        cmd.SetComputeIntParam(
            shader, BrickSizeId, ChunkSize);

        cmd.SetComputeIntParam(
            shader, MaxRayStepsId, Mathf.Max(1, maxRaySteps));

        cmd.SetComputeFloatParam(
            shader,
            StepMultiplierId,
            Mathf.Max(0.1f, stepMultiplier));

        cmd.SetComputeFloatParam(
            shader,
            ExtinctionScaleId,
            Mathf.Max(0.0f, tauVPerNHPerPc));

        cmd.SetComputeVectorParam(
            shader,
            VolumeOriginId,
            new Vector4(
                volumeOrigin.x, volumeOrigin.y, volumeOrigin.z, 0.0f));

        cmd.SetComputeVectorParam(
            shader,
            BaseStepId,
            new Vector4(
                baseStep.x, baseStep.y, baseStep.z, 0.0f));

        cmd.SetComputeVectorParam(
            shader,
            VolumeBoundsMinId,
            new Vector4(
                volumeBoundsMin.x,
                volumeBoundsMin.y,
                volumeBoundsMin.z,
                0.0f));

        cmd.SetComputeVectorParam(
            shader,
            VolumeBoundsMaxId,
            new Vector4(
                volumeBoundsMax.x,
                volumeBoundsMax.y,
                volumeBoundsMax.z,
                0.0f));

        cmd.SetComputeVectorArrayParam(
            shader, LevelParams0Id, shaderLevelParams0);

        cmd.SetComputeVectorArrayParam(
            shader, LevelParams1Id, shaderLevelParams1);

        return true;
    }

    public int GetLoadedBrickCount() => loaded.Count;

    public void GetDebugNodeInfo(List<DustLODNodeInfo> output)
    {
        if (output == null)
            return;

        output.Clear();

        if (!initialized || disposed || levels.Count == 0)
            return;

        HashSet<BrickKey> keys =
            new HashSet<BrickKey>(currentDesired);

        foreach (BrickKey key in loaded.Keys)
            keys.Add(key);

        foreach (BrickKey key in keys)
        {
            if (key.level < 0 || key.level >= levels.Count)
                continue;

            LevelInfo levelInfo = levels[key.level];

            int x0 = key.x * ChunkSize;
            int y0 = key.y * ChunkSize;
            int z0 = key.z * ChunkSize;

            if (x0 < 0 || y0 < 0 || z0 < 0 ||
                x0 >= levelInfo.dimensions.x ||
                y0 >= levelInfo.dimensions.y ||
                z0 >= levelInfo.dimensions.z)
            {
                continue;
            }

            int x1 = Mathf.Min(
                x0 + ChunkSize, levelInfo.dimensions.x);
            int y1 = Mathf.Min(
                y0 + ChunkSize, levelInfo.dimensions.y);
            int z1 = Mathf.Min(
                z0 + ChunkSize, levelInfo.dimensions.z);

            if (x1 <= x0 || y1 <= y0 || z1 <= z0)
                continue;

            int scale = 1 << key.level;

            Vector3 p0 = CellBoundaryToWorld(
                x0, y0, z0, scale);

            Vector3 p1 = CellBoundaryToWorld(
                x1, y1, z1, scale);

            Vector3 min = Vector3.Min(p0, p1);
            Vector3 max = Vector3.Max(p0, p1);

            Bounds bounds = new Bounds(
                (min + max) * 0.5f,
                max - min);

            output.Add(new DustLODNodeInfo
            {
                level = key.level,
                x = key.x,
                y = key.y,
                z = key.z,
                bounds = bounds,
                isLoaded = loaded.ContainsKey(key),
                isDesired = currentDesired.Contains(key)
            });
        }
    }

    private void Awake()
    {
        Debug.Log($"DustLODLoader Awake on '{gameObject.name}'");
        EnsureInitialized();
    }

    private void OnDestroy()
    {
        Dispose();
    }

    private void EnsureInitialized()
    {
        if (!initialized)
            Initialize();
    }

    private void Initialize()
    {
        disposed = false;
        cancellation = new CancellationTokenSource();

#if UNITY_EDITOR
        dataRoot = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", processedDirectory));
#elif UNITY_ANDROID
        using (var unityPlayer =
            new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity =
            unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var obbDirectory =
            activity.Call<AndroidJavaObject>("getObbDir"))
        {
            string obbRoot =
                obbDirectory.Call<string>("getAbsolutePath");

            if (string.IsNullOrEmpty(obbRoot))
            {
                throw new IOException(
                    "Android getObbDir() returned an empty path.");
            }

            dataRoot = Path.Combine(obbRoot, processedDirectory);
        }
#else
        dataRoot = Path.Combine(
            Application.persistentDataPath, processedDirectory);
#endif

        string lodDir = Path.Combine(dataRoot, "lod");
        string indexPath = Path.Combine(lodDir, "index.bin");

        if (!Directory.Exists(lodDir))
        {
            throw new DirectoryNotFoundException(
                $"Dust LOD directory not found:\n{lodDir}");
        }

        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException(
                $"Dust LOD index not found:\n{indexPath}");
        }

        Debug.Log($"DustLODLoader reading index:\n{indexPath}");

        ParseIndex(indexPath);
        CreateGpuResources();

        initialized = true;

        Debug.Log(
            $"DustLODLoader initialized. " +
            $"Levels: {levels.Count}, GPU bricks: {maxGpuBricks}");
    }

    private void ParseIndex(string indexPath)
    {
        levels.Clear();

        using (FileStream stream = File.OpenRead(indexPath))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            byte[] magic = reader.ReadBytes(8);

            if (magic.Length != 8)
                throw new InvalidDataException("Invalid dust LOD index.");

            for (int i = 0; i < 8; i++)
            {
                if (magic[i] != IndexMagic[i])
                    throw new InvalidDataException(
                        "Invalid dust LOD index magic.");
            }

            uint version = reader.ReadUInt32();

            if (version != IndexVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported dust LOD index version: {version}");
            }

            ushort chunkSize = reader.ReadUInt16();

            if (chunkSize != ChunkSize)
            {
                throw new InvalidDataException(
                    $"Expected chunk size {ChunkSize}, got {chunkSize}.");
            }

            ushort indexedLevels = reader.ReadUInt16();

            if (indexedLevels == 0)
            {
                throw new InvalidDataException(
                    "Dust LOD index contains no levels.");
            }

            if (indexedLevels > maxLOD + 1)
            {
                throw new InvalidDataException(
                    $"Dust LOD index contains {indexedLevels} levels, " +
                    $"but loader supports only LOD0..LOD{maxLOD}.");
            }

            // Retain compatibility with the index format.
            reader.ReadSingle(); // Base voxel size, unused at runtime.

            int baseX = checked((int)reader.ReadUInt32());
            int baseY = checked((int)reader.ReadUInt32());
            int baseZ = checked((int)reader.ReadUInt32());

            reader.ReadUInt32(); // Padded X
            reader.ReadUInt32(); // Padded Y
            reader.ReadUInt32(); // Padded Z

            double ox = reader.ReadDouble();
            double oy = reader.ReadDouble();
            double oz = reader.ReadDouble();

            double sx = reader.ReadDouble();
            double sy = reader.ReadDouble();
            double sz = reader.ReadDouble();

            baseDimensions = new Vector3Int(baseX, baseY, baseZ);
            volumeOrigin = new Vector3(
                (float)ox, (float)oy, (float)oz);

            baseStep = new Vector3(
                (float)sx, (float)sy, (float)sz);

            for (int i = 0; i < indexedLevels; i++)
            {
                int levelNumber = reader.ReadUInt16();
                reader.ReadUInt16(); // Reserved

                float voxelSize = reader.ReadSingle();

                int dimX = checked((int)reader.ReadUInt32());
                int dimY = checked((int)reader.ReadUInt32());
                int dimZ = checked((int)reader.ReadUInt32());

                int brickX = checked((int)reader.ReadUInt32());
                int brickY = checked((int)reader.ReadUInt32());
                int brickZ = checked((int)reader.ReadUInt32());

                int nonempty = checked((int)reader.ReadUInt32());

                if (levelNumber != i)
                {
                    throw new InvalidDataException(
                        $"Dust LOD index level mismatch. " +
                        $"Expected LOD{i}, got LOD{levelNumber}.");
                }

                if (levelNumber > maxLOD)
                {
                    throw new InvalidDataException(
                        $"Unexpected LOD level {levelNumber}.");
                }

                LevelInfo level = new LevelInfo
                {
                    voxelSizePc = voxelSize,
                    dimensions = new Vector3Int(dimX, dimY, dimZ),
                    brickDimensions = new Vector3Int(
                        brickX, brickY, brickZ)
                };

                for (int n = 0; n < nonempty; n++)
                {
                    int bx = checked((int)reader.ReadUInt32());
                    int by = checked((int)reader.ReadUInt32());
                    int bz = checked((int)reader.ReadUInt32());

                    reader.ReadSingle(); // Max density, not needed at runtime.

                    level.availableBricks.Add(
                        new BrickCoord(bx, by, bz));
                }

                levels.Add(level);
            }
        }

        if (levels.Count == 0)
            throw new InvalidDataException(
                "Dust LOD index produced zero levels.");

        if (fallbackLOD > levels.Count - 1)
            fallbackLOD = levels.Count - 1;

        fallbackLOD = Mathf.Clamp(
            fallbackLOD, 0, levels.Count - 1);

        BuildPageOffsets();
        ComputeVolumeBounds();

        Debug.Log(
            $"Dust LOD index loaded: {levels.Count} levels " +
            $"(LOD0..LOD{levels.Count - 1}).");
    }

    private void CreateGpuResources()
    {
        if (!SystemInfo.supports2DArrayTextures)
        {
            throw new NotSupportedException(
                "This GPU does not support 2D array textures.");
        }

        int maxArraySlices = SystemInfo.maxTextureArraySlices;
        int maxSupportedBricks = maxArraySlices / ChunkSize;

        if (maxSupportedBricks < 1)
        {
            throw new NotSupportedException(
                "GPU texture-array slice limit is too small.");
        }

        if (maxGpuBricks > maxSupportedBricks)
        {
            Debug.LogWarning(
                $"Dust GPU brick pool reduced from {maxGpuBricks} " +
                $"to {maxSupportedBricks} because the GPU supports " +
                $"only {maxArraySlices} texture-array slices.");

            maxGpuBricks = maxSupportedBricks;
        }

        if (maxGpuBricks < 4)
        {
            throw new NotSupportedException(
                "Dust GPU brick pool must support at least 4 bricks.");
        }

        int totalEntries = 0;

        foreach (LevelInfo level in levels)
        {
            Vector3Int dims = level.brickDimensions;
            totalEntries += dims.x * dims.y * dims.z;
        }

        pageTableCpu = new int[totalEntries];

        pageTableGpu = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            totalEntries,
            sizeof(int));

        pageTableGpu.SetData(pageTableCpu);

        int totalSlices = maxGpuBricks * ChunkSize;

        brickPool = new Texture2DArray(
            ChunkSize,
            ChunkSize,
            totalSlices,
            TextureFormat.RHalf,
            false,
            true)
        {
            name = "DustBrickPool",
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        stagingTexture = new Texture2D(
            ChunkSize,
            ChunkSize,
            TextureFormat.RHalf,
            false,
            true)
        {
            name = "DustBrickStaging",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        stagingSliceBytes =
            new byte[ChunkSize * ChunkSize * BytesPerVoxel];

        freeSlots.Clear();

        for (int i = maxGpuBricks - 1; i >= 0; i--)
            freeSlots.Push(i);
    }

    private void BuildPageOffsets()
    {
        int offset = 0;

        for (int i = 0; i < levels.Count; i++)
        {
            LevelInfo info = levels[i];
            info.pageOffset = offset;

            offset +=
                info.brickDimensions.x *
                info.brickDimensions.y *
                info.brickDimensions.z;
        }
    }

    private int GetPageIndex(BrickKey key)
    {
        LevelInfo info = levels[key.level];

        return info.pageOffset + key.x +
            info.brickDimensions.x *
            (key.y + info.brickDimensions.y * key.z);
    }

    private void RebuildStreamingSet(Camera camera)
    {
        if (levels.Count == 0)
        {
            Debug.LogError("DustLODLoader has no LOD levels loaded.");
            return;
        }

        fallbackLOD = Mathf.Clamp(
            fallbackLOD, 0, levels.Count - 1);

        float now = Time.unscaledTime;
        HashSet<BrickKey> desired = new HashSet<BrickKey>();

        // Always request every available brick at the fallback LOD.
        LevelInfo fallbackLevel = levels[fallbackLOD];

        foreach (BrickCoord coord in fallbackLevel.availableBricks)
        {
            desired.Add(new BrickKey(
                fallbackLOD, coord.x, coord.y, coord.z));
        }

        List<Candidate> candidates = new List<Candidate>();

        for (int level = 0; level < fallbackLOD; level++)
        {
            LevelInfo info = levels[level];

            float radius =
                fineRadiusPc *
                Mathf.Pow(2.0f, level) *
                preloadRadiusMultiplier;

            float radiusSq = radius * radius;

            foreach (BrickCoord coord in info.availableBricks)
            {
                float distanceSq = DistanceSqToBrick(
                    level, coord, camera.transform.position);

                if (distanceSq <= radiusSq)
                {
                    candidates.Add(new Candidate(
                        new BrickKey(
                            level, coord.x, coord.y, coord.z),
                        Mathf.Sqrt(distanceSq)));
                }
            }
        }

        candidates.Sort(CompareCandidates);

        int remainingCapacity = Mathf.Max(
            0, maxGpuBricks - desired.Count);

        int candidateCount = Mathf.Min(
            remainingCapacity, candidates.Count);

        for (int i = 0; i < candidateCount; i++)
            desired.Add(candidates[i].key);

        currentDesired = desired;

        foreach (BrickKey key in desired)
        {
            if (loaded.TryGetValue(key, out LoadedBrick brick))
                brick.lastUsedTime = now;
        }

        int started = 0;

        foreach (BrickKey key in desired)
        {
            if (loaded.ContainsKey(key) || loading.Contains(key))
                continue;

            if (started >= maxConcurrentLoads)
                break;

            StartLoad(key);
            started++;
        }

        while (loaded.Count > maxGpuBricks)
        {
            if (!TryEvictOne())
                break;
        }
    }

    private static int CompareCandidates(Candidate a, Candidate b)
    {
        int levelCompare = a.key.level.CompareTo(b.key.level);

        if (levelCompare != 0)
            return levelCompare;

        return a.distance.CompareTo(b.distance);
    }

    private void StartLoad(BrickKey key)
    {
        if (disposed || loading.Contains(key))
            return;

        string path = GetBrickPath(key);

        if (!File.Exists(path))
        {
            Debug.LogWarning($"Dust brick missing:\n{path}");
            return;
        }

        loading.Add(key);
        _ = LoadBrickAsync(key, path);
    }

    private async Task LoadBrickAsync(BrickKey key, string path)
    {
        try
        {
            byte[] bytes = await Task.Run(
                () => File.ReadAllBytes(path),
                cancellation.Token);

            completedLoads.Enqueue(new LoadResult
            {
                key = key,
                bytes = bytes
            });
        }
        catch (Exception exception)
        {
            if (!disposed)
            {
                completedLoads.Enqueue(new LoadResult
                {
                    key = key,
                    error = exception.Message
                });
            }
        }
    }

    private void PumpUploads()
    {
        int uploads = 0;

        while (uploads < maxUploadsPerFrame &&
               completedLoads.TryDequeue(out LoadResult result))
        {
            loading.Remove(result.key);

            if (result.error != null)
            {
                Debug.LogWarning(
                    $"Dust brick load failed: {result.error}");
                continue;
            }

            if (result.bytes == null ||
                result.bytes.Length != BytesPerBrick)
            {
                Debug.LogWarning("Invalid dust brick size.");
                continue;
            }

            if (!currentDesired.Contains(result.key) ||
                loaded.ContainsKey(result.key))
            {
                continue;
            }

            int slot = AcquireSlot();

            if (slot < 0)
                continue;

            int sliceBytes =
                ChunkSize * ChunkSize * BytesPerVoxel;

            int baseSlice = slot * ChunkSize;

            for (int z = 0; z < ChunkSize; z++)
            {
                Buffer.BlockCopy(
                    result.bytes,
                    z * sliceBytes,
                    stagingSliceBytes,
                    0,
                    sliceBytes);

                stagingTexture.LoadRawTextureData(stagingSliceBytes);
                stagingTexture.Apply(false, false);

                Graphics.CopyTexture(
                    stagingTexture, 0, 0,
                    brickPool, baseSlice + z, 0);
            }

            loaded[result.key] = new LoadedBrick
            {
                slot = slot,
                lastUsedTime = Time.unscaledTime
            };

            pageTableCpu[GetPageIndex(result.key)] = slot + 1;
            pageTableDirty = true;
            uploads++;
        }
    }

    private int AcquireSlot()
    {
        if (freeSlots.Count > 0)
            return freeSlots.Pop();

        if (!TryEvictOne() || freeSlots.Count == 0)
            return -1;

        return freeSlots.Pop();
    }

    private bool TryEvictOne()
    {
        BrickKey candidateKey = default;
        LoadedBrick candidate = null;

        foreach (KeyValuePair<BrickKey, LoadedBrick> pair in loaded)
        {
            if (currentDesired.Contains(pair.Key))
                continue;

            if (candidate == null ||
                pair.Value.lastUsedTime < candidate.lastUsedTime)
            {
                candidateKey = pair.Key;
                candidate = pair.Value;
            }
        }

        if (candidate == null)
            return false;

        int slot = candidate.slot;

        pageTableCpu[GetPageIndex(candidateKey)] = 0;
        loaded.Remove(candidateKey);
        freeSlots.Push(slot);

        pageTableDirty = true;
        return true;
    }

    private string GetBrickPath(BrickKey key)
    {
        string directory = Path.Combine(
            dataRoot, "lod", $"LOD{key.level:00}");

        return Path.Combine(
            directory,
            $"chunk_{key.x:000000}_{key.y:000000}_{key.z:000000}.bin");
    }

    private float DistanceSqToBrick(
        int level,
        BrickCoord brick,
        Vector3 point)
    {
        LevelInfo info = levels[level];
        int scale = 1 << level;

        int cellX0 = brick.x * ChunkSize;
        int cellY0 = brick.y * ChunkSize;
        int cellZ0 = brick.z * ChunkSize;

        int cellX1 = Mathf.Min(
            cellX0 + ChunkSize, info.dimensions.x);
        int cellY1 = Mathf.Min(
            cellY0 + ChunkSize, info.dimensions.y);
        int cellZ1 = Mathf.Min(
            cellZ0 + ChunkSize, info.dimensions.z);

        Vector3 p0 = CellBoundaryToWorld(
            cellX0, cellY0, cellZ0, scale);

        Vector3 p1 = CellBoundaryToWorld(
            cellX1, cellY1, cellZ1, scale);

        Vector3 min = Vector3.Min(p0, p1);
        Vector3 max = Vector3.Max(p0, p1);

        float dx = Mathf.Max(
            min.x - point.x, 0.0f, point.x - max.x);

        float dy = Mathf.Max(
            min.y - point.y, 0.0f, point.y - max.y);

        float dz = Mathf.Max(
            min.z - point.z, 0.0f, point.z - max.z);

        return dx * dx + dy * dy + dz * dz;
    }

    private Vector3 CellBoundaryToWorld(
        int x,
        int y,
        int z,
        int scale)
    {
        return new Vector3(
            volumeOrigin.x + (x * scale - 0.5f) * baseStep.x,
            volumeOrigin.y + (y * scale - 0.5f) * baseStep.y,
            volumeOrigin.z + (z * scale - 0.5f) * baseStep.z);
    }

    private void ComputeVolumeBounds()
    {
        Vector3 lastCenter = volumeOrigin + Vector3.Scale(
            new Vector3(
                baseDimensions.x - 1,
                baseDimensions.y - 1,
                baseDimensions.z - 1),
            baseStep);

        Vector3 half = new Vector3(
            Mathf.Abs(baseStep.x) * 0.5f,
            Mathf.Abs(baseStep.y) * 0.5f,
            Mathf.Abs(baseStep.z) * 0.5f);

        Vector3 a = volumeOrigin - half;
        Vector3 b = volumeOrigin + half;
        Vector3 c = lastCenter - half;
        Vector3 d = lastCenter + half;

        volumeBoundsMin = Vector3.Min(
            Vector3.Min(a, b),
            Vector3.Min(c, d));

        volumeBoundsMax = Vector3.Max(
            Vector3.Max(a, b),
            Vector3.Max(c, d));
    }

    private void UpdateComputeLevelParameters()
    {
        for (int i = 0; i < MaxShaderLevels; i++)
        {
            shaderLevelParams0[i] = Vector4.zero;
            shaderLevelParams1[i] =
                new Vector4(0.0f, 1e30f, 0.0f, 0.0f);
        }

        int count = Mathf.Min(levels.Count, MaxShaderLevels);

        for (int i = 0; i < count; i++)
        {
            LevelInfo info = levels[i];

            float switchDistance = i < fallbackLOD
                ? fineRadiusPc * Mathf.Pow(2.0f, i)
                : 1e30f;

            shaderLevelParams0[i] = new Vector4(
                info.pageOffset,
                info.brickDimensions.x,
                info.brickDimensions.y,
                info.brickDimensions.z);

            shaderLevelParams1[i] = new Vector4(
                info.voxelSizePc,
                switchDistance,
                0.0f,
                0.0f);
        }
    }

    private void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;

        loaded.Clear();
        loading.Clear();
        currentDesired.Clear();

        while (completedLoads.TryDequeue(out _))
        {
            // Discard completed reads during teardown.
        }

        if (pageTableGpu != null)
        {
            pageTableGpu.Release();
            pageTableGpu = null;
        }

        if (brickPool != null)
        {
            Destroy(brickPool);
            brickPool = null;
        }

        if (stagingTexture != null)
        {
            Destroy(stagingTexture);
            stagingTexture = null;
        }

        initialized = false;
    }

    public struct DustLODNodeInfo
    {
        public int level;
        public int x;
        public int y;
        public int z;
        public Bounds bounds;
        public bool isLoaded;
        public bool isDesired;
    }
}