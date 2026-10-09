using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

[Serializable]
public sealed class LandmarkBinaryRecord
{
    public string Name { get; }
    public string Type { get; }
    public Vector3 UnityPosition { get; }
    public string ShortDescription { get; }
    public string ParagraphDescription { get; }

    public LandmarkBinaryRecord(
        string name,
        string type,
        Vector3 unityPosition,
        string shortDescription,
        string paragraphDescription)
    {
        Name = name;
        Type = type;
        UnityPosition = unityPosition;
        ShortDescription = shortDescription;
        ParagraphDescription = paragraphDescription;
    }
}

public static class LandmarkBinaryReader
{
    private const string ExpectedMagic = "LMARK001";
    private const uint ExpectedVersion = 1;
    private const uint MaximumRecords = 1_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    // Must match process_landmarks.py:
    // header = <8sII
    // record = <HHHIfff, followed by name/type/short/paragraph UTF-8 bytes.
    public static List<LandmarkBinaryRecord> Read(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            throw new InvalidDataException("Landmark binary file is empty.");

        using (var stream = new MemoryStream(bytes, false))
        using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
        {
            string magic = Encoding.ASCII.GetString(ReadExact(reader, 8));
            if (magic != ExpectedMagic)
                throw new InvalidDataException(
                    $"Invalid landmark binary magic: '{magic}'. Expected '{ExpectedMagic}'.");

            uint version = reader.ReadUInt32();
            if (version != ExpectedVersion)
                throw new InvalidDataException(
                    $"Unsupported landmark binary version {version}; expected {ExpectedVersion}.");

            uint count = reader.ReadUInt32();
            if (count > MaximumRecords)
                throw new InvalidDataException($"Landmark record count is unreasonable: {count}.");

            var records = new List<LandmarkBinaryRecord>((int)count);

            for (uint i = 0; i < count; i++)
            {
                ushort nameLength = reader.ReadUInt16();
                ushort typeLength = reader.ReadUInt16();
                ushort shortLength = reader.ReadUInt16();
                uint paragraphLength = reader.ReadUInt32();

                if (paragraphLength > int.MaxValue)
                    throw new InvalidDataException(
                        $"Landmark {i} has a paragraph description too large to load.");

                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                float z = reader.ReadSingle();

                string name = ReadUtf8(reader, nameLength);
                string type = ReadUtf8(reader, typeLength);
                string shortDescription = ReadUtf8(reader, shortLength);
                string paragraphDescription = ReadUtf8(reader, (int)paragraphLength);

                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidDataException($"Landmark record {i} has an empty name.");
                if (float.IsNaN(x) || float.IsInfinity(x) ||
                    float.IsNaN(y) || float.IsInfinity(y) ||
                    float.IsNaN(z) || float.IsInfinity(z))
                    throw new InvalidDataException($"Landmark '{name}' has invalid coordinates.");

                records.Add(new LandmarkBinaryRecord(
                    name,
                    type,
                    new Vector3(x, y, z),
                    shortDescription,
                    paragraphDescription));
            }

            if (stream.Position != stream.Length)
                throw new InvalidDataException(
                    $"Landmark binary has {stream.Length - stream.Position} unexpected trailing bytes.");

            return records;
        }
    }

    private static string ReadUtf8(BinaryReader reader, int length)
    {
        byte[] bytes = ReadExact(reader, length);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Landmark binary contains invalid UTF-8 text.", ex);
        }
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException("Landmark binary ended unexpectedly.");
        return bytes;
    }
}
