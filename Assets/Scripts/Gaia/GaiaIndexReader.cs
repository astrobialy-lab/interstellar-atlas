using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;

public class GaiaIndexReader : MonoBehaviour
{
    [Header("Index")]
    [SerializeField]
    private string indexRelativePath = "Data/Gaia/index.bin";

    private List<GaiaChunk>[] chunksByShell;

    public uint Version { get; private set; }
    public uint HealpixLevel { get; private set; }
    public uint ShellCount { get; private set; }
    public uint ChunkCount { get; private set; }

    public GaiaChunk[] Chunks { get; private set; }

    private Dictionary<ulong, GaiaChunk> chunkLookup;
    private byte[] stringTable;

    private void Awake()
    {
        LoadIndex();
    }

    private void LoadIndex()
    {
        string projectRoot =
            Directory.GetParent(Application.dataPath).FullName;

        string path =
            Path.Combine(
                projectRoot,
                indexRelativePath
            );

        if (!File.Exists(path))
        {
            Debug.LogError(
                $"Gaia index not found:\n{path}"
            );

            return;
        }

        using FileStream stream =
            File.OpenRead(path);

        using BinaryReader reader =
            new BinaryReader(stream);

        // ------------------------------------------------------------
        // Header
        // ------------------------------------------------------------

        uint magic =
            reader.ReadUInt32();

        if (magic != 0x47414941)
        {
            Debug.LogError(
                $"Invalid Gaia index magic: 0x{magic:X8}"
            );

            return;
        }

        Version =
            reader.ReadUInt32();

        HealpixLevel =
            reader.ReadUInt32();

        ShellCount =
            reader.ReadUInt32();

        ChunkCount =
            reader.ReadUInt32();

        Debug.Log(
            $"Gaia index:\n" +
            $"  Version: {Version}\n" +
            $"  HEALPix level: {HealpixLevel}\n" +
            $"  Shell count: {ShellCount}\n" +
            $"  Chunk count: {ChunkCount:N0}"
        );

        if (Version != 2)
        {
            Debug.LogError(
                $"Expected Gaia index version 2, but found version {Version}."
            );

            return;
        }

        // ------------------------------------------------------------
        // Chunks
        // ------------------------------------------------------------

        Chunks =
            new GaiaChunk[ChunkCount];

        chunkLookup =
            new Dictionary<ulong, GaiaChunk>(
                (int)ChunkCount
            );

        for (int i = 0;
             i < ChunkCount;
             i++)
        {
            GaiaChunk chunk =
                new GaiaChunk();

            chunk.chunkId =
                reader.ReadUInt32();

            chunk.healpixId =
                reader.ReadUInt32();

            chunk.shellId =
                reader.ReadUInt16();

            // Padding
            reader.ReadUInt16();

            chunk.starCount =
                reader.ReadUInt32();

            chunk.distanceMin =
                reader.ReadDouble();

            chunk.distanceMax =
                reader.ReadDouble();

            // AABB
            chunk.minX =
                reader.ReadSingle();

            chunk.minY =
                reader.ReadSingle();

            chunk.minZ =
                reader.ReadSingle();

            chunk.maxX =
                reader.ReadSingle();

            chunk.maxY =
                reader.ReadSingle();

            chunk.maxZ =
                reader.ReadSingle();

            // String table information
            chunk.pathOffset =
                reader.ReadUInt64();

            chunk.pathLength =
                reader.ReadUInt32();

            // Reserved
            reader.ReadUInt32();

            Chunks[i] =
                chunk;

            ulong key =
                MakeKey(
                    chunk.healpixId,
                    chunk.shellId
                );

            chunkLookup[key] =
                chunk;
        }

        // ------------------------------------------------------------
        // String table
        // ------------------------------------------------------------

        long remaining =
            stream.Length -
            stream.Position;

        stringTable =
            reader.ReadBytes(
                (int)remaining
            );

        // ------------------------------------------------------------
        // Organize chunks by shell
        // ------------------------------------------------------------

        chunksByShell =
            new List<GaiaChunk>[
                (int)ShellCount
            ];

        for (int i = 0;
             i < (int)ShellCount;
             i++)
        {
            chunksByShell[i] =
                new List<GaiaChunk>();
        }

        foreach (GaiaChunk chunk in Chunks)
        {
            if (chunk.shellId < ShellCount)
            {
                chunksByShell[
                    chunk.shellId
                ].Add(chunk);
            }
        }

        // ------------------------------------------------------------
        // Debug: first few chunks
        // ------------------------------------------------------------

        for (int i = 0;
             i < Mathf.Min(5, Chunks.Length);
             i++)
        {
            GaiaChunk chunk = Chunks[i];
        }
    }

    // ------------------------------------------------------------
    // HEALPix lookup
    // ------------------------------------------------------------

    public bool TryGetChunk(
        uint healpixId,
        ushort shellId,
        out GaiaChunk chunk)
    {
        chunk = null;

        if (chunkLookup == null)
            return false;

        ulong key =
            MakeKey(
                healpixId,
                shellId
            );

        return chunkLookup.TryGetValue(
            key,
            out chunk
        );
    }

    // ------------------------------------------------------------
    // Spatial lookup
    //
    // Finds all chunks whose AABB intersects a sphere around
    // the player's position.
    //
    // We first use the distance shells to avoid testing chunks
    // that cannot possibly intersect the sphere.
    // ------------------------------------------------------------

    public GaiaChunk[] GetChunksInSphere(
        Vector3 center,
        float radius)
    {
        if (Chunks == null ||
            chunksByShell == null)
        {
            return Array.Empty<GaiaChunk>();
        }

        float centerDistance =
            center.magnitude;

        float minDistance =
            Mathf.Max(
                0.0f,
                centerDistance - radius
            );

        float maxDistance =
            centerDistance + radius;

        float radiusSquared =
            radius * radius;

        List<GaiaChunk> result =
            new List<GaiaChunk>();

        // ------------------------------------------------------------
        // Only inspect shells whose radial distance can overlap
        // the player's search sphere.
        // ------------------------------------------------------------

        for (int shell = 0;
             shell < chunksByShell.Length;
             shell++)
        {
            List<GaiaChunk> shellChunks =
                chunksByShell[shell];

            if (shellChunks == null ||
                shellChunks.Count == 0)
            {
                continue;
            }

            GaiaChunk firstChunk =
                shellChunks[0];

            // Shell is completely farther away.
            if (firstChunk.distanceMin > maxDistance)
                continue;

            // Shell is completely closer.
            if (firstChunk.distanceMax < minDistance)
                continue;


            // --------------------------------------------------------
            // Test every chunk in the relevant shell.
            // --------------------------------------------------------

            foreach (GaiaChunk chunk in shellChunks)
            {
                if (chunk.IntersectsSphere(
                        center,
                        radiusSquared))
                {
                    result.Add(chunk);
                }
            }
        }

        return result.ToArray();
    }

    // ------------------------------------------------------------
    // Get chunk path from string table
    // ------------------------------------------------------------

    public string GetChunkPath(
        GaiaChunk chunk)
    {
        if (chunk == null ||
            stringTable == null)
        {
            return null;
        }

        int start =
            checked(
                (int)chunk.pathOffset
            );

        int length =
            checked(
                (int)chunk.pathLength
            );

        if (
            start < 0 ||
            length < 0 ||
            start + length > stringTable.Length
        )
        {
            return null;
        }

        return Encoding.UTF8.GetString(
            stringTable,
            start,
            length
        );
    }

    // ------------------------------------------------------------
    // Key for HEALPix + shell lookup
    // ------------------------------------------------------------

    private static ulong MakeKey(
        uint healpixId,
        ushort shellId)
    {
        return
            ((ulong)healpixId << 16) |
            shellId;
    }
}


// ====================================================================
// Gaia chunk metadata
// ====================================================================

[Serializable]
public class GaiaChunk
{
    public uint chunkId;

    public uint healpixId;

    public ushort shellId;

    public uint starCount;

    public double distanceMin;

    public double distanceMax;

    // AABB
    public float minX;
    public float minY;
    public float minZ;

    public float maxX;
    public float maxY;
    public float maxZ;

    // Path in the index string table
    public ulong pathOffset;

    public uint pathLength;


    // ------------------------------------------------------------
    // AABB vs sphere intersection
    // ------------------------------------------------------------

    public bool IntersectsSphere(
        Vector3 center,
        float radiusSquared)
    {
        float closestX =
            Mathf.Clamp(
                center.x,
                minX,
                maxX
            );

        float closestY =
            Mathf.Clamp(
                center.y,
                minY,
                maxY
            );

        float closestZ =
            Mathf.Clamp(
                center.z,
                minZ,
                maxZ
            );

        float dx =
            center.x -
            closestX;

        float dy =
            center.y -
            closestY;

        float dz =
            center.z -
            closestZ;

        float distanceSquared =
            dx * dx +
            dy * dy +
            dz * dz;

        return
            distanceSquared <= radiusSquared;
    }
}