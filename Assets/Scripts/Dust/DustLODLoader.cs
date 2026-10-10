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

    [Header("Ray Marching")]
    [SerializeField] private float stepMultiplier = 0.75f;
    [SerializeField] private int maxRaySteps = 512;

    [Header("Extinction")]
    [Tooltip("V-band optical depth for n_H = 1 cm^-3 across 1 pc. Approximate Galactic calibration using N_H/A_V = 2.21e21 cm^-2 mag^-1 and tau_V = A_V/1.086.")]
    [SerializeField] private float tauVPerNHPerPc = 0.0012857f;

    [Tooltip("Approximate A_lambda/A_V multipliers for linear RGB: R~0.82, G~1.00, B~1.32. Broad-band approximation; blue is attenuated more strongly than red.")]
    [SerializeField] private Vector3 extinctionWavelengthMultipliers = new Vector3(0.82f, 1.00f, 1.32f);

    [Header("Basic Scattering")]
    [Tooltip("Artistic gain applied to the single-direction illumination approximation. 1 means no extra gain; this is not a substitute for per-star illumination.")]
    [SerializeField] private float scatteringGain = 0.05f;

    [SerializeField] private bool scatteringEnabled = false;

    [Tooltip("Optional tint applied to scattered light; white is the neutral starting point.")]
    [SerializeField] private Color scatteringTint = Color.white;

    [SerializeField] private Color lightColor = Color.white;

    [Tooltip("Direction FROM each dust sample TOWARD a distant directional light source. This is a single-direction approximation, not the actual Gaia star field.")]
    [SerializeField] private Vector3 lightDirection = new Vector3(0.5f, 0.2f, 1.0f);

    [Tooltip("Henyey-Greenstein anisotropy: positive values favor forward scattering.")]
    [Range(-0.95f, 0.95f)]
    [SerializeField] private float phaseAsymmetryG = 0.60f;

    [Tooltip("Single-scattering albedo alpha_sca / alpha_ext; approximate visible-light value.")]
    [Range(0.0f, 1.0f)]
    [SerializeField] private float scatteringAlbedo = 0.60f;

    private const int ChunkSize = 32;
    private const int VoxelsPerBrick = ChunkSize * ChunkSize * ChunkSize;
    private const int BytesPerVoxel = 2;
    private const int BytesPerBrick = VoxelsPerBrick * BytesPerVoxel;
    private const int MaxShaderLevels = 16;
    private const uint IndexVersion = 1;

    private static readonly byte[] IndexMagic =
    {
        (byte)'D', (byte)'U', (byte)'S', (byte)'T',
        (byte)'L', (byte)'O', (byte)'D', (byte)'1'
    };

    private static readonly int PageTableId = Shader.PropertyToID("_PageTable");
    private static readonly int BrickPoolId = Shader.PropertyToID("_BrickPool");

    private static readonly int LevelParams0Id = Shader.PropertyToID("_LevelParams0");
    private static readonly int LevelParams1Id = Shader.PropertyToID("_LevelParams1");
    private static readonly int LevelCountId = Shader.PropertyToID("_LevelCount");
    private static readonly int FallbackLodId = Shader.PropertyToID("_FallbackLod");
    private static readonly int BrickSizeId = Shader.PropertyToID("_BrickSize");
    private static readonly int MaxRayStepsId = Shader.PropertyToID("_MaxRaySteps");
    private static readonly int StepMultiplierId = Shader.PropertyToID("_StepMultiplier");
    private static readonly int ExtinctionScaleId = Shader.PropertyToID("_ExtinctionScale");
    private static readonly int VolumeOriginId = Shader.PropertyToID("_VolumeOrigin");
    private static readonly int BaseStepId = Shader.PropertyToID("_BaseStep");
    private static readonly int VolumeBoundsMinId = Shader.PropertyToID("_VolumeBoundsMin");
    private static readonly int VolumeBoundsMaxId = Shader.PropertyToID("_VolumeBoundsMax");

    private readonly struct BrickCoord : IEquatable<BrickCoord>
    {
        public readonly int x, y, z;
        public BrickCoord(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public bool Equals(BrickCoord other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is BrickCoord other && Equals(other);
        public override int GetHashCode() { unchecked { int h = x; h = h * 397 ^ y; h = h * 397 ^ z; return h; } }
    }

    private readonly struct BrickKey : IEquatable<BrickKey>
    {
        public readonly int level, x, y, z;
        public BrickKey(int level, int x, int y, int z) { this.level = level; this.x = x; this.y = y; this.z = z; }
        public bool Equals(BrickKey other) => level == other.level && x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is BrickKey other && Equals(other);
        public override int GetHashCode() { unchecked { int h = level; h = h * 397 ^ x; h = h * 397 ^ y; h = h * 397 ^ z; return h; } }
    }

    private sealed class LevelInfo
    {
        public int level;
        public float voxelSizePc;
        public Vector3Int dimensions;
        public Vector3Int brickDimensions;
        public int pageOffset;
        public readonly List<BrickCoord> availableBricks = new List<BrickCoord>();
    }

    private sealed class LoadedBrick
    {
        public BrickKey key;
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
        public Candidate(BrickKey key, float distance) { this.key = key; this.distance = distance; }
    }

    private int[] pageTableCpu;
    private readonly List<LevelInfo> levels = new List<LevelInfo>();
    private readonly Dictionary<BrickKey, LoadedBrick> loaded = new Dictionary<BrickKey, LoadedBrick>();
    private readonly HashSet<BrickKey> loading = new HashSet<BrickKey>();
    private readonly Dictionary<int, BrickKey> slotToKey = new Dictionary<int, BrickKey>();
    private readonly Stack<int> freeSlots = new Stack<int>();
    private readonly ConcurrentQueue<LoadResult> completedLoads = new ConcurrentQueue<LoadResult>();
    private HashSet<BrickKey> currentDesired = new HashSet<BrickKey>();
    private CancellationTokenSource cancellation;
    private GraphicsBuffer pageTableGpu;
    private Texture2DArray brickPool;
    private Texture2D stagingTexture;
    private ushort[] decodedBrick;
    private byte[] stagingSliceBytes;

    private Vector3 volumeOrigin, baseStep, volumeBoundsMin, volumeBoundsMax;
    private Vector3Int baseDimensions;
    private float baseVoxelSizePc;
    private bool pageTableDirty, initialized, disposed;
    private float nextStreamingTime;
    private Camera lastCamera;

    private readonly float[] shaderVoxelSizes = new float[MaxShaderLevels];
    private readonly float[] shaderSwitchDistances = new float[MaxShaderLevels];
    private readonly float[] shaderPageOffsets = new float[MaxShaderLevels];
    private readonly float[] shaderBrickDimsX = new float[MaxShaderLevels];
    private readonly float[] shaderBrickDimsY = new float[MaxShaderLevels];
    private readonly float[] shaderBrickDimsZ = new float[MaxShaderLevels];

    // Compute-shader-friendly packed level arrays. float4 arrays avoid constant-buffer
    // array-stride ambiguity when sent through RenderGraph's command buffer.
    private readonly Vector4[] shaderLevelParams0 = new Vector4[MaxShaderLevels];
    private readonly Vector4[] shaderLevelParams1 = new Vector4[MaxShaderLevels];

    public GraphicsBuffer PageTableBuffer => pageTableGpu;
    public Texture2DArray BrickPoolTexture => brickPool;
    public bool IsInitialized => initialized && !disposed;
    public int LevelCount => levels.Count;
    public int LoadedBrickCount => loaded.Count;
    public int DesiredBrickCount => currentDesired.Count;

    public void ToggleScattering()
    {
        scatteringEnabled = !scatteringEnabled;

        Debug.Log(
            $"Dust scattering: {(scatteringEnabled ? "ON" : "OFF")}"
        );
    }

    public void PrepareForCamera(Camera camera, Material material)
    {
        if (disposed) return;
        EnsureInitialized();
        PumpUploads();

        float now = Time.unscaledTime;
        bool streamNow = now >= nextStreamingTime || camera != lastCamera;
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

        ApplyShaderParameters(material);
    }

    /// <summary>
    /// Bind the same density bricks and LOD sampling settings used by DustVolume.shader
    /// to the cumulative-extinction compute kernel. The pass context is supplied so all
    /// values are recorded into the correct RenderGraph command stream.
    /// </summary>
    public bool BindExtinctionComputeParameters(
        ComputeGraphContext context,
        ComputeShader shader,
        int kernel,
        BufferHandle pageTableHandle,
        TextureHandle brickPoolHandle)
    {
        if (context == null || shader == null || disposed || !initialized ||
            pageTableGpu == null || brickPool == null || levels.Count == 0 ||
            !pageTableHandle.IsValid() || !brickPoolHandle.IsValid())
            return false;

        var cmd = context.cmd;
        cmd.SetComputeBufferParam(shader, kernel, PageTableId, pageTableHandle);
        cmd.SetComputeTextureParam(shader, kernel, BrickPoolId, brickPoolHandle);
        cmd.SetComputeIntParam(shader, LevelCountId, levels.Count);
        cmd.SetComputeIntParam(shader, FallbackLodId, fallbackLOD);
        cmd.SetComputeIntParam(shader, BrickSizeId, ChunkSize);
        cmd.SetComputeIntParam(shader, MaxRayStepsId, Mathf.Max(1, maxRaySteps));
        cmd.SetComputeFloatParam(shader, StepMultiplierId, Mathf.Max(0.1f, stepMultiplier));
        cmd.SetComputeFloatParam(shader, ExtinctionScaleId, Mathf.Max(0.0f, tauVPerNHPerPc));
        cmd.SetComputeVectorParam(shader, VolumeOriginId, new Vector4(volumeOrigin.x, volumeOrigin.y, volumeOrigin.z, 0.0f));
        cmd.SetComputeVectorParam(shader, BaseStepId, new Vector4(baseStep.x, baseStep.y, baseStep.z, 0.0f));
        cmd.SetComputeVectorParam(shader, VolumeBoundsMinId, new Vector4(volumeBoundsMin.x, volumeBoundsMin.y, volumeBoundsMin.z, 0.0f));
        cmd.SetComputeVectorParam(shader, VolumeBoundsMaxId, new Vector4(volumeBoundsMax.x, volumeBoundsMax.y, volumeBoundsMax.z, 0.0f));
        cmd.SetComputeVectorArrayParam(shader, LevelParams0Id, shaderLevelParams0);
        cmd.SetComputeVectorArrayParam(shader, LevelParams1Id, shaderLevelParams1);
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

        // Include every brick that is either requested or loaded.
        HashSet<BrickKey> keys =
            new HashSet<BrickKey>(currentDesired);

        foreach (BrickKey key in loaded.Keys)
            keys.Add(key);

        foreach (BrickKey key in keys)
        {
            if (key.level < 0 || key.level >= levels.Count)
                continue;

            LevelInfo levelInfo = levels[key.level];

            // Brick coordinates are measured in this LOD's voxel grid.
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

            // Clamp edge bricks to the actual LOD dimensions.
            int x1 = Mathf.Min(x0 + ChunkSize, levelInfo.dimensions.x);
            int y1 = Mathf.Min(y0 + ChunkSize, levelInfo.dimensions.y);
            int z1 = Mathf.Min(z0 + ChunkSize, levelInfo.dimensions.z);

            if (x1 <= x0 || y1 <= y0 || z1 <= z0)
                continue;

            // Convert this LOD's voxel coordinates to world coordinates.
            int scale = 1 << key.level;

            Vector3 p0 = CellBoundaryToWorld(
                x0, y0, z0, scale);

            Vector3 p1 = CellBoundaryToWorld(
                x1, y1, z1, scale);

            Vector3 min = Vector3.Min(p0, p1);
            Vector3 max = Vector3.Max(p0, p1);

            Bounds bounds = new Bounds(
                (min + max) * 0.5f,
                max - min
            );

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

    private void OnDestroy() => Dispose();

    private void EnsureInitialized()
    {
        if (!initialized) Initialize();
    }

    private void Initialize()
    {
        disposed = false;
        cancellation = new CancellationTokenSource();

        string root = Path.Combine(Application.dataPath, "..", processedDirectory);
        root = Path.GetFullPath(root);
        string lodDir = Path.Combine(root, "lod");
        string indexPath = Path.Combine(lodDir, "index.bin");

        if (!Directory.Exists(lodDir))
            throw new DirectoryNotFoundException($"Dust LOD directory not found:\n{lodDir}");
        if (!File.Exists(indexPath))
            throw new FileNotFoundException($"Dust LOD index not found:\n{indexPath}");

        Debug.Log($"DustLODLoader reading index:\n{indexPath}");
        ParseIndex(indexPath);
        Debug.Log($"DustLODLoader ParseIndex returned {levels.Count} levels.");
        CreateGpuResources();
        initialized = true;
        Debug.Log($"DustLODLoader initialized. Levels: {levels.Count}, GPU bricks: {maxGpuBricks}");
    }

    private void ParseIndex(string indexPath)
    {
        levels.Clear();
        using (FileStream stream = File.OpenRead(indexPath))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            byte[] magic = reader.ReadBytes(8);
            if (magic.Length != 8) throw new InvalidDataException("Invalid dust LOD index.");
            for (int i = 0; i < 8; i++)
                if (magic[i] != IndexMagic[i]) throw new InvalidDataException("Invalid dust LOD index magic.");

            uint version = reader.ReadUInt32();
            if (version != IndexVersion) throw new InvalidDataException($"Unsupported dust LOD index version: {version}");

            ushort chunkSize = reader.ReadUInt16();
            if (chunkSize != ChunkSize) throw new InvalidDataException($"Expected chunk size {ChunkSize}, got {chunkSize}.");

            ushort indexedLevels = reader.ReadUInt16();
            if (indexedLevels == 0) throw new InvalidDataException("Dust LOD index contains no levels.");
            if (indexedLevels > maxLOD + 1)
                throw new InvalidDataException($"Dust LOD index contains {indexedLevels} levels, but loader supports only LOD0..LOD{maxLOD}.");

            baseVoxelSizePc = reader.ReadSingle();
            int baseX = checked((int)reader.ReadUInt32());
            int baseY = checked((int)reader.ReadUInt32());
            int baseZ = checked((int)reader.ReadUInt32());
            reader.ReadUInt32(); // padded X
            reader.ReadUInt32(); // padded Y
            reader.ReadUInt32(); // padded Z

            double ox = reader.ReadDouble();
            double oy = reader.ReadDouble();
            double oz = reader.ReadDouble();
            double sx = reader.ReadDouble();
            double sy = reader.ReadDouble();
            double sz = reader.ReadDouble();

            baseDimensions = new Vector3Int(baseX, baseY, baseZ);
            volumeOrigin = new Vector3((float)ox, (float)oy, (float)oz);
            baseStep = new Vector3((float)sx, (float)sy, (float)sz);

            for (int i = 0; i < indexedLevels; i++)
            {
                int levelNumber = reader.ReadUInt16();
                reader.ReadUInt16(); // reserved
                float voxelSize = reader.ReadSingle();
                int dimX = checked((int)reader.ReadUInt32());
                int dimY = checked((int)reader.ReadUInt32());
                int dimZ = checked((int)reader.ReadUInt32());
                int brickX = checked((int)reader.ReadUInt32());
                int brickY = checked((int)reader.ReadUInt32());
                int brickZ = checked((int)reader.ReadUInt32());
                int nonempty = checked((int)reader.ReadUInt32());

                if (levelNumber != i)
                    throw new InvalidDataException($"Dust LOD index level mismatch. Expected LOD{i}, got LOD{levelNumber}.");
                if (levelNumber > maxLOD)
                    throw new InvalidDataException($"Unexpected LOD level {levelNumber}.");

                LevelInfo level = new LevelInfo
                {
                    level = levelNumber,
                    voxelSizePc = voxelSize,
                    dimensions = new Vector3Int(dimX, dimY, dimZ),
                    brickDimensions = new Vector3Int(brickX, brickY, brickZ)
                };

                for (int n = 0; n < nonempty; n++)
                {
                    int bx = checked((int)reader.ReadUInt32());
                    int by = checked((int)reader.ReadUInt32());
                    int bz = checked((int)reader.ReadUInt32());
                    reader.ReadSingle(); // max_density
                    level.availableBricks.Add(new BrickCoord(bx, by, bz));
                }
                levels.Add(level);
            }
        }

        if (levels.Count == 0) throw new InvalidDataException("Dust LOD index produced zero levels.");
        if (levels.Count != maxLOD + 1 && fallbackLOD > levels.Count - 1)
            fallbackLOD = levels.Count - 1;
        fallbackLOD = Mathf.Clamp(fallbackLOD, 0, levels.Count - 1);
        BuildPageOffsets();
        ComputeVolumeBounds();
        Debug.Log($"Dust LOD index loaded: {levels.Count} levels (LOD0..LOD{levels.Count - 1}).");
    }

    private void CreateGpuResources()
    {
        if (!SystemInfo.supports2DArrayTextures)
            throw new NotSupportedException("This GPU does not support 2D texture arrays.");

        int maxArraySlices = SystemInfo.maxTextureArraySlices;
        int maxSupportedBricks = maxArraySlices / ChunkSize;
        if (maxSupportedBricks < 1)
            throw new NotSupportedException("GPU texture-array slice limit is too small.");

        if (maxGpuBricks > maxSupportedBricks)
        {
            Debug.LogWarning($"Dust GPU brick pool reduced from {maxGpuBricks} to {maxSupportedBricks} because the GPU supports only {maxArraySlices} texture-array slices.");
            maxGpuBricks = maxSupportedBricks;
        }
        if (maxGpuBricks < 4)
            throw new NotSupportedException("Dust GPU brick pool must support at least 4 bricks.");

        int totalEntries = 0;
        for (int level = 0; level < levels.Count; level++)
        {
            Vector3Int dims = levels[level].brickDimensions;
            totalEntries += dims.x * dims.y * dims.z;
        }

        pageTableCpu = new int[totalEntries];
        pageTableGpu = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalEntries, sizeof(int));
        pageTableGpu.SetData(pageTableCpu);

        int totalSlices = maxGpuBricks * ChunkSize;
        brickPool = new Texture2DArray(ChunkSize, ChunkSize, totalSlices, TextureFormat.RHalf, false, true)
        {
            name = "DustBrickPool",
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        stagingTexture = new Texture2D(ChunkSize, ChunkSize, TextureFormat.RHalf, false, true)
        {
            name = "DustBrickStaging",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        decodedBrick = new ushort[VoxelsPerBrick];
        stagingSliceBytes = new byte[ChunkSize * ChunkSize * 2];

        freeSlots.Clear();
        for (int i = maxGpuBricks - 1; i >= 0; i--) freeSlots.Push(i);
    }

    private void BuildPageOffsets()
    {
        int offset = 0;
        for (int level = 0; level < levels.Count; level++)
        {
            LevelInfo info = levels[level];
            info.pageOffset = offset;
            offset += info.brickDimensions.x * info.brickDimensions.y * info.brickDimensions.z;
        }
    }

    private int GetPageIndex(BrickKey key)
    {
        LevelInfo info = levels[key.level];
        return info.pageOffset + key.x + info.brickDimensions.x * (key.y + info.brickDimensions.y * key.z);
    }

    private void RebuildStreamingSet(Camera camera)
    {
        if (levels.Count == 0)
        {
            Debug.LogError("DustLODLoader has no LOD levels loaded.");
            return;
        }

        fallbackLOD = Mathf.Clamp(fallbackLOD, 0, levels.Count - 1);
        float now = Time.unscaledTime;
        HashSet<BrickKey> desired = new HashSet<BrickKey>();

        LevelInfo fallbackLevel = levels[fallbackLOD];
        foreach (BrickCoord c in fallbackLevel.availableBricks)
            desired.Add(new BrickKey(fallbackLOD, c.x, c.y, c.z));

        List<Candidate> candidates = new List<Candidate>();
        for (int level = 0; level < fallbackLOD; level++)
        {
            LevelInfo info = levels[level];
            float radius = fineRadiusPc * Mathf.Pow(2.0f, level) * preloadRadiusMultiplier;
            float radiusSq = radius * radius;

            foreach (BrickCoord c in info.availableBricks)
            {
                float distanceSq = DistanceSqToBrick(level, c, camera.transform.position);
                if (distanceSq <= radiusSq)
                    candidates.Add(new Candidate(new BrickKey(level, c.x, c.y, c.z), Mathf.Sqrt(distanceSq)));
            }
        }

        candidates.Sort(CompareCandidates);
        int remainingCapacity = Mathf.Max(0, maxGpuBricks - desired.Count);
        int count = Mathf.Min(remainingCapacity, candidates.Count);
        for (int i = 0; i < count; i++) desired.Add(candidates[i].key);
        currentDesired = desired;

        foreach (BrickKey key in desired)
            if (loaded.TryGetValue(key, out LoadedBrick brick)) brick.lastUsedTime = now;

        int started = 0;
        foreach (BrickKey key in desired)
        {
            if (loaded.ContainsKey(key) || loading.Contains(key)) continue;
            if (started >= maxConcurrentLoads) break;
            StartLoad(key);
            started++;
        }

        while (loaded.Count > maxGpuBricks)
            if (!TryEvictOne()) break;
    }

    private static int CompareCandidates(Candidate a, Candidate b)
    {
        int levelCompare = a.key.level.CompareTo(b.key.level);
        if (levelCompare != 0) return levelCompare;
        return a.distance.CompareTo(b.distance);
    }

    private void StartLoad(BrickKey key)
    {
        if (disposed || loading.Contains(key)) return;
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
            byte[] bytes = await Task.Run(() => File.ReadAllBytes(path), cancellation.Token);
            completedLoads.Enqueue(new LoadResult { key = key, bytes = bytes });
        }
        catch (Exception e)
        {
            if (!disposed) completedLoads.Enqueue(new LoadResult { key = key, error = e.Message });
        }
    }

    private void PumpUploads()
    {
        int uploads = 0;
        while (uploads < maxUploadsPerFrame && completedLoads.TryDequeue(out LoadResult result))
        {
            loading.Remove(result.key);
            if (result.error != null)
            {
                Debug.LogWarning($"Dust brick load failed: {result.error}");
                continue;
            }
            if (result.bytes == null || result.bytes.Length != BytesPerBrick)
            {
                Debug.LogWarning("Invalid dust brick size.");
                continue;
            }
            if (!currentDesired.Contains(result.key) || loaded.ContainsKey(result.key)) continue;

            int slot = AcquireSlot();
            if (slot < 0) continue;

            Buffer.BlockCopy(result.bytes, 0, decodedBrick, 0, result.bytes.Length);
            int sliceBytes = ChunkSize * ChunkSize * 2;
            int baseSlice = slot * ChunkSize;

            for (int z = 0; z < ChunkSize; z++)
            {
                Buffer.BlockCopy(result.bytes, z * sliceBytes, stagingSliceBytes, 0, sliceBytes);
                stagingTexture.LoadRawTextureData(stagingSliceBytes);
                stagingTexture.Apply(false, false);
                Graphics.CopyTexture(stagingTexture, 0, 0, brickPool, baseSlice + z, 0);
            }

            LoadedBrick brick = new LoadedBrick { key = result.key, slot = slot, lastUsedTime = Time.unscaledTime };
            loaded[result.key] = brick;
            slotToKey[slot] = result.key;
            pageTableCpu[GetPageIndex(result.key)] = slot + 1;
            pageTableDirty = true;
            uploads++;
        }
    }

    private int AcquireSlot()
    {
        if (freeSlots.Count > 0) return freeSlots.Pop();
        if (!TryEvictOne()) return -1;
        if (freeSlots.Count == 0) return -1;
        return freeSlots.Pop();
    }

    private bool TryEvictOne()
    {
        BrickKey candidateKey = default;
        LoadedBrick candidate = null;

        foreach (KeyValuePair<BrickKey, LoadedBrick> pair in loaded)
        {
            if (currentDesired.Contains(pair.Key)) continue;
            if (candidate == null || pair.Value.lastUsedTime < candidate.lastUsedTime)
            {
                candidateKey = pair.Key;
                candidate = pair.Value;
            }
        }

        if (candidate == null) return false;
        int slot = candidate.slot;
        pageTableCpu[GetPageIndex(candidateKey)] = 0;
        loaded.Remove(candidateKey);
        slotToKey.Remove(slot);
        freeSlots.Push(slot);
        pageTableDirty = true;
        return true;
    }

    private string GetBrickPath(BrickKey key)
    {
        string root = Path.Combine(Application.dataPath, "..", processedDirectory);
        root = Path.GetFullPath(root);
        string directory = Path.Combine(root, "lod", $"LOD{key.level:00}");
        return Path.Combine(directory, $"chunk_{key.x:000000}_{key.y:000000}_{key.z:000000}.bin");
    }

    private float DistanceSqToBrick(int level, BrickCoord brick, Vector3 point)
    {
        LevelInfo info = levels[level];
        int scale = 1 << level;
        int cellX0 = brick.x * ChunkSize;
        int cellY0 = brick.y * ChunkSize;
        int cellZ0 = brick.z * ChunkSize;
        int cellX1 = Mathf.Min(cellX0 + ChunkSize, info.dimensions.x);
        int cellY1 = Mathf.Min(cellY0 + ChunkSize, info.dimensions.y);
        int cellZ1 = Mathf.Min(cellZ0 + ChunkSize, info.dimensions.z);

        Vector3 p0 = CellBoundaryToWorld(cellX0, cellY0, cellZ0, scale);
        Vector3 p1 = CellBoundaryToWorld(cellX1, cellY1, cellZ1, scale);
        Vector3 min = Vector3.Min(p0, p1);
        Vector3 max = Vector3.Max(p0, p1);

        float dx = Mathf.Max(min.x - point.x, 0.0f, point.x - max.x);
        float dy = Mathf.Max(min.y - point.y, 0.0f, point.y - max.y);
        float dz = Mathf.Max(min.z - point.z, 0.0f, point.z - max.z);
        return dx * dx + dy * dy + dz * dz;
    }

    private Vector3 CellBoundaryToWorld(int x, int y, int z, int scale)
    {
        return new Vector3(
            volumeOrigin.x + (x * scale - 0.5f) * baseStep.x,
            volumeOrigin.y + (y * scale - 0.5f) * baseStep.y,
            volumeOrigin.z + (z * scale - 0.5f) * baseStep.z
        );
    }

    private void ComputeVolumeBounds()
    {
        Vector3 lastCenter = volumeOrigin + Vector3.Scale(
            new Vector3(baseDimensions.x - 1, baseDimensions.y - 1, baseDimensions.z - 1), baseStep);
        Vector3 half = new Vector3(Mathf.Abs(baseStep.x) * 0.5f, Mathf.Abs(baseStep.y) * 0.5f, Mathf.Abs(baseStep.z) * 0.5f);
        Vector3 a = volumeOrigin - half;
        Vector3 b = volumeOrigin + half;
        Vector3 c = lastCenter - half;
        Vector3 d = lastCenter + half;
        volumeBoundsMin = Vector3.Min(Vector3.Min(a, b), Vector3.Min(c, d));
        volumeBoundsMax = Vector3.Max(Vector3.Max(a, b), Vector3.Max(c, d));
    }

    private void ApplyShaderParameters(Material material)
    {
        for (int i = 0; i < MaxShaderLevels; i++)
        {
            shaderVoxelSizes[i] = 0.0f;
            shaderSwitchDistances[i] = 1e30f;
            shaderPageOffsets[i] = 0.0f;
            shaderBrickDimsX[i] = 0.0f;
            shaderBrickDimsY[i] = 0.0f;
            shaderBrickDimsZ[i] = 0.0f;
        }

        int count = Mathf.Min(levels.Count, MaxShaderLevels);
        for (int i = 0; i < count; i++)
        {
            LevelInfo info = levels[i];
            shaderVoxelSizes[i] = info.voxelSizePc;
            shaderPageOffsets[i] = info.pageOffset;
            shaderBrickDimsX[i] = info.brickDimensions.x;
            shaderBrickDimsY[i] = info.brickDimensions.y;
            shaderBrickDimsZ[i] = info.brickDimensions.z;
            if (i < fallbackLOD) shaderSwitchDistances[i] = fineRadiusPc * Mathf.Pow(2.0f, i);
        }

        for (int i = 0; i < MaxShaderLevels; i++)
        {
            shaderLevelParams0[i] = new Vector4(
                shaderPageOffsets[i], shaderBrickDimsX[i], shaderBrickDimsY[i], shaderBrickDimsZ[i]);
            shaderLevelParams1[i] = new Vector4(
                shaderVoxelSizes[i], shaderSwitchDistances[i], 0.0f, 0.0f);
        }

        Vector3 ld = lightDirection;
        if (ld.sqrMagnitude < 1e-6f) ld = Vector3.forward;
        ld.Normalize();

        if (material == null) return;

        material.SetBuffer(PageTableId, pageTableGpu);
        material.SetTexture(BrickPoolId, brickPool);
        material.SetInt("_LevelCount", count);
        material.SetInt("_FallbackLod", fallbackLOD);
        material.SetInt("_BrickSize", ChunkSize);
        material.SetInt("_MaxRaySteps", maxRaySteps);
        material.SetFloat("_StepMultiplier", Mathf.Max(0.1f, stepMultiplier));

        material.SetFloat("_ExtinctionScale", Mathf.Max(0.0f, tauVPerNHPerPc));
        material.SetVector("_ExtinctionRGB", new Vector4(
            Mathf.Max(0.0f, extinctionWavelengthMultipliers.x),
            Mathf.Max(0.0f, extinctionWavelengthMultipliers.y),
            Mathf.Max(0.0f, extinctionWavelengthMultipliers.z), 0.0f));

        material.SetFloat("_ScatteringStrength", scatteringEnabled ? Mathf.Max(0.0f, scatteringGain) : 0.0f);
        material.SetFloat("_ScatteringAlbedo", Mathf.Clamp01(scatteringAlbedo));
        material.SetColor("_ScatteringColor", scatteringTint);
        material.SetColor("_LightColor", lightColor);
        material.SetVector("_LightDirection", new Vector4(ld.x, ld.y, ld.z, 0.0f));
        material.SetFloat("_PhaseG", Mathf.Clamp(phaseAsymmetryG, -0.95f, 0.95f));

        material.SetVector("_VolumeOrigin", new Vector4(volumeOrigin.x, volumeOrigin.y, volumeOrigin.z, 0.0f));
        material.SetVector("_BaseStep", new Vector4(baseStep.x, baseStep.y, baseStep.z, 0.0f));
        material.SetVector("_VolumeBoundsMin", new Vector4(volumeBoundsMin.x, volumeBoundsMin.y, volumeBoundsMin.z, 0.0f));
        material.SetVector("_VolumeBoundsMax", new Vector4(volumeBoundsMax.x, volumeBoundsMax.y, volumeBoundsMax.z, 0.0f));

        material.SetFloatArray("_PageOffsets", shaderPageOffsets);
        material.SetFloatArray("_BrickDimsX", shaderBrickDimsX);
        material.SetFloatArray("_BrickDimsY", shaderBrickDimsY);
        material.SetFloatArray("_BrickDimsZ", shaderBrickDimsZ);
        material.SetFloatArray("_VoxelSizes", shaderVoxelSizes);
        material.SetFloatArray("_LodSwitchDistances", shaderSwitchDistances);
    }

    private void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;

        loaded.Clear();
        loading.Clear();
        slotToKey.Clear();
        currentDesired.Clear();

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
