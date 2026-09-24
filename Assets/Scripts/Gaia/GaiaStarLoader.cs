using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

public class GaiaStarLoader : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private GaiaIndexReader indexReader;

    [SerializeField]
    private Transform playerTransform;

    [Header("Spatial Streaming")]
    [SerializeField]
    private float loadRadius = 10.0f;

    [SerializeField]
    private float unloadRadius = 12.0f;

    [SerializeField]
    private float streamUpdateDistance = 1.0f;

    [Header("Debug")]
    [SerializeField]
    private bool verboseLogging = false;

    private const int STAR_SIZE = 65;
    private const int STAR_GPU_SIZE = 20;

    private readonly Dictionary<uint, LoadedChunk> loadedChunks =
        new Dictionary<uint, LoadedChunk>();

    private readonly HashSet<uint> loadingChunks =
        new HashSet<uint>();

    private readonly HashSet<uint> desiredChunkIds =
        new HashSet<uint>();

    private readonly List<Task<ChunkLoadResult>> loadingTasks =
        new List<Task<ChunkLoadResult>>();

    private Vector3 lastStreamPosition;

    private void Start()
    {
        if (indexReader == null)
        {
            Debug.LogError(
                "GaiaStarLoader: No GaiaIndexReader assigned."
            );
            return;
        }

        if (playerTransform == null)
        {
            playerTransform = transform;
        }

        lastStreamPosition = playerTransform.position;

        UpdateStreaming();
    }

    private void Update()
    {
        ProcessCompletedLoads();

        if (playerTransform == null)
            return;

        Vector3 currentPosition = playerTransform.position;

        Vector3 movement =
            currentPosition - lastStreamPosition;

        float movementSinceLastUpdateSquared =
            movement.sqrMagnitude;

        float streamUpdateDistanceSquared =
            streamUpdateDistance * streamUpdateDistance;

        if (movementSinceLastUpdateSquared >=
            streamUpdateDistanceSquared)
        {
            lastStreamPosition = currentPosition;
            UpdateStreaming();
        }
    }

    private void UpdateStreaming()
    {
        Vector3 playerPosition =
            playerTransform.position;

        GaiaChunk[] desiredChunks =
            indexReader.GetChunksInSphere(
                playerPosition,
                loadRadius
            );

        desiredChunkIds.Clear();

        foreach (GaiaChunk chunk in desiredChunks)
        {
            desiredChunkIds.Add(
                chunk.chunkId
            );
        }

        foreach (GaiaChunk chunk in desiredChunks)
        {
            if (loadedChunks.ContainsKey(
                    chunk.chunkId))
            {
                continue;
            }

            if (loadingChunks.Contains(
                    chunk.chunkId))
            {
                continue;
            }

            StartBackgroundLoad(chunk);
        }

        GaiaChunk[] chunksToKeep =
            indexReader.GetChunksInSphere(
                playerPosition,
                unloadRadius
            );

        HashSet<uint> keepChunkIds =
            new HashSet<uint>();

        foreach (GaiaChunk chunk in chunksToKeep)
        {
            keepChunkIds.Add(
                chunk.chunkId
            );
        }

        List<uint> chunksToUnload =
            new List<uint>();

        foreach (uint chunkId in loadedChunks.Keys)
        {
            if (!keepChunkIds.Contains(chunkId))
            {
                chunksToUnload.Add(
                    chunkId
                );
            }
        }

        foreach (uint chunkId in chunksToUnload)
        {
            UnloadChunk(chunkId);
        }
    }

    private void StartBackgroundLoad(
        GaiaChunk chunk)
    {
        loadingChunks.Add(
            chunk.chunkId
        );

        Task<ChunkLoadResult> task =
            Task.Run(
                () => ReadChunkFromDisk(chunk)
            );

        loadingTasks.Add(task);
    }

    private void ProcessCompletedLoads()
    {
        for (int i = loadingTasks.Count - 1;
             i >= 0;
             i--)
        {
            Task<ChunkLoadResult> task =
                loadingTasks[i];

            if (!task.IsCompleted)
                continue;

            loadingTasks.RemoveAt(i);

            if (task.IsCanceled ||
                task.IsFaulted)
            {
                continue;
            }

            ChunkLoadResult result =
                task.Result;

            loadingChunks.Remove(
                result.chunkId
            );

            if (!result.success)
            {
                Debug.LogError(
                    result.errorMessage
                );

                continue;
            }

            if (!desiredChunkIds.Contains(
                    result.chunkId))
            {
                continue;
            }

            if (loadedChunks.ContainsKey(
                    result.chunkId))
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

            LoadedChunk loadedChunk =
                new LoadedChunk();

            loadedChunk.chunkId =
                result.chunkId;

            loadedChunk.starCount =
                result.stars.Length;

            loadedChunk.buffer =
                buffer;

            loadedChunk.drawBounds =
                result.drawBounds;

            loadedChunks.Add(
                result.chunkId,
                loadedChunk
            );

            if (verboseLogging)
            {
                Debug.Log(
                    $"Loaded Gaia chunk " +
                    $"{result.chunkId}: " +
                    $"{result.stars.Length} stars."
                );
            }
        }
    }

    private ChunkLoadResult ReadChunkFromDisk(
        GaiaChunk chunk)
    {
        ChunkLoadResult result =
            new ChunkLoadResult();

        result.chunkId =
            chunk.chunkId;

        string projectRoot =
            Directory.GetParent(
                Application.dataPath
            ).FullName;

        string relativePath =
            indexReader.GetChunkPath(chunk);

        string fullPath =
            Path.Combine(
                projectRoot,
                "Data",
                "Gaia",
                relativePath
            );

        if (!File.Exists(fullPath))
        {
            result.success = false;

            result.errorMessage =
                $"Gaia chunk file not found:\n" +
                $"{fullPath}";

            return result;
        }

        long fileSize =
            new FileInfo(fullPath).Length;

        long expectedSize =
            (long)chunk.starCount *
            STAR_SIZE;

        if (fileSize != expectedSize)
        {
            result.success = false;

            result.errorMessage =
                $"Gaia chunk size mismatch.\n" +
                $"Chunk: {chunk.chunkId}\n" +
                $"Expected: {expectedSize} bytes\n" +
                $"Actual: {fileSize} bytes";

            return result;
        }

        byte[] data =
            File.ReadAllBytes(
                fullPath
            );

        StarGPU[] stars =
            new StarGPU[chunk.starCount];

        using MemoryStream stream =
            new MemoryStream(data);

        using BinaryReader reader =
            new BinaryReader(stream);

        for (int i = 0;
             i < chunk.starCount;
             i++)
        {
            reader.ReadUInt64();

            float x =
                reader.ReadSingle();

            float y =
                reader.ReadSingle();

            float z =
                reader.ReadSingle();

            float magnitude =
                reader.ReadSingle();

            float bpRp =
                reader.ReadSingle();

            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();

            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();

            reader.ReadByte();

            stars[i] =
                new StarGPU
                {
                    position =
                        new Vector3(
                            x,
                            y,
                            z
                        ),

                    magnitude =
                        magnitude,

                    bpRp =
                        bpRp
                };
        }

        result.success =
            true;

        result.stars =
            stars;

        result.drawBounds =
            new Bounds(
                new Vector3(
                    (chunk.minX + chunk.maxX) * 0.5f,
                    (chunk.minY + chunk.maxY) * 0.5f,
                    (chunk.minZ + chunk.maxZ) * 0.5f
                ),

                new Vector3(
                    Mathf.Max(
                        chunk.maxX - chunk.minX,
                        1.0f
                    ),

                    Mathf.Max(
                        chunk.maxY - chunk.minY,
                        1.0f
                    ),

                    Mathf.Max(
                        chunk.maxZ - chunk.minZ,
                        1.0f
                    )
                )
            );

        return result;
    }

    private void UnloadChunk(
        uint chunkId)
    {
        if (!loadedChunks.TryGetValue(
                chunkId,
                out LoadedChunk chunk))
        {
            return;
        }

        if (chunk.buffer != null)
        {
            chunk.buffer.Release();
            chunk.buffer = null;
        }

        loadedChunks.Remove(
            chunkId
        );

        if (verboseLogging)
        {
            Debug.Log(
                $"Unloaded Gaia chunk {chunkId}."
            );
        }
    }

    private void OnDestroy()
    {
        foreach (LoadedChunk chunk in loadedChunks.Values)
        {
            if (chunk.buffer != null)
            {
                chunk.buffer.Release();
                chunk.buffer = null;
            }
        }

        loadedChunks.Clear();

        loadingChunks.Clear();
        desiredChunkIds.Clear();
        loadingTasks.Clear();
    }

    private class LoadedChunk
    {
        public uint chunkId;
        public int starCount;
        public GraphicsBuffer buffer;
        public Bounds drawBounds;
    }

    private class ChunkLoadResult
    {
        public bool success;
        public uint chunkId;
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

    public void GetLoadedStarBuffers(
        List<GraphicsBuffer> buffers,
        List<int> counts)
    {
        buffers.Clear();
        counts.Clear();

        foreach (LoadedChunk chunk in loadedChunks.Values)
        {
            if (chunk == null)
                continue;

            if (chunk.buffer == null)
                continue;

            if (chunk.starCount <= 0)
                continue;

            buffers.Add(chunk.buffer);
            counts.Add(chunk.starCount);
        }
    }
}