using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public class GaiaIndexReader : MonoBehaviour
{
    [Header("Index")]
    [SerializeField]
    private string indexRelativePath =
        "Data/Gaia/processed/index.bin";

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

        string path = Path.Combine(
            projectRoot,
            indexRelativePath
        );

        Debug.Log($"Loading Gaia index:\n{path}");

        if (!File.Exists(path))
        {
            Debug.LogError(
                $"Gaia index not found:\n{path}"
            );
            return;
        }

        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader =
            new BinaryReader(stream);

        uint magic = reader.ReadUInt32();

        if (magic != 0x47414941)
        {
            Debug.LogError(
                $"Invalid Gaia index magic: 0x{magic:X8}"
            );
            return;
        }

        Version = reader.ReadUInt32();
        HealpixLevel = reader.ReadUInt32();
        ShellCount = reader.ReadUInt32();
        ChunkCount = reader.ReadUInt32();

        Debug.Log(
            $"Gaia index:\n" +
            $"  Version: {Version}\n" +
            $"  HEALPix level: {HealpixLevel}\n" +
            $"  Shell count: {ShellCount}\n" +
            $"  Chunk count: {ChunkCount}"
        );

        Chunks =
            new GaiaChunk[ChunkCount];

        chunkLookup =
            new Dictionary<ulong, GaiaChunk>(
                (int)ChunkCount
            );

        for (int i = 0; i < ChunkCount; i++)
        {
            GaiaChunk chunk =
                new GaiaChunk();

            chunk.chunkId =
                reader.ReadUInt32();

            chunk.healpixId =
                reader.ReadUInt32();

            chunk.shellId =
                reader.ReadUInt16();

            reader.ReadUInt16();

            chunk.starCount =
                reader.ReadUInt32();

            chunk.distanceMin =
                reader.ReadDouble();

            chunk.distanceMax =
                reader.ReadDouble();

            chunk.pathOffset =
                reader.ReadUInt64();

            chunk.pathLength =
                reader.ReadUInt32();

            reader.ReadUInt32();

            Chunks[i] = chunk;

            ulong key =
                MakeKey(
                    chunk.healpixId,
                    chunk.shellId
                );

            chunkLookup[key] = chunk;
        }

        // The rest of the file is the string table.
        long remaining =
            stream.Length - stream.Position;

        stringTable =
            reader.ReadBytes((int)remaining);

        Debug.Log(
            $"String table: " +
            $"{stringTable.Length:N0} bytes"
        );

        Debug.Log(
            $"Loaded {Chunks.Length:N0} Gaia chunks."
        );

        for (
            int i = 0;
            i < Mathf.Min(5, Chunks.Length);
            i++
        )
        {
            GaiaChunk chunk = Chunks[i];

            Debug.Log(
                $"Chunk {chunk.chunkId}: " +
                $"HEALPix={chunk.healpixId}, " +
                $"shell={chunk.shellId}, " +
                $"stars={chunk.starCount}, " +
                $"distance={chunk.distanceMin}-" +
                $"{chunk.distanceMax} pc, " +
                $"path={GetChunkPath(chunk)}"
            );
        }
    }

    public bool TryGetChunk(
        uint healpixId,
        ushort shellId,
        out GaiaChunk chunk
    )
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

    public string GetChunkPath(
        GaiaChunk chunk
    )
    {
        if (chunk == null ||
            stringTable == null)
        {
            return null;
        }

        int start =
            checked((int)chunk.pathOffset);

        int length =
            checked((int)chunk.pathLength);

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

    private static ulong MakeKey(
        uint healpixId,
        ushort shellId
    )
    {
        return ((ulong)healpixId << 16)
             | shellId;
    }
}

[Serializable]
public class GaiaChunk
{
    public uint chunkId;
    public uint healpixId;
    public ushort shellId;

    public uint starCount;

    public double distanceMin;
    public double distanceMax;

    public ulong pathOffset;
    public uint pathLength;
}