using System;
using System.IO;
using UnityEngine;

public class GaiaStarLoader : MonoBehaviour
{
    [SerializeField]
    private GaiaIndexReader indexReader;

    private const int STAR_SIZE = 65;

    private const int HEALPIX_LEVEL = 6;
    private const int HEALPIX_NSIDE = 1 << HEALPIX_LEVEL;

    private void Start()
    {
        LoadPlayerChunk();
    }

    private void LoadPlayerChunk()
    {
        if (indexReader == null)
        {
            Debug.LogError(
                "GaiaStarLoader: No GaiaIndexReader assigned."
            );
            return;
        }

        Vector3 position = transform.position;

        double x = position.x;
        double y = position.y;
        double z = position.z;

        double distance = position.magnitude;

        int shell = GetShell(distance);

        if (shell < 0)
        {
            Debug.LogWarning(
                "GaiaStarLoader: Player is at the origin."
            );
            return;
        }

        int healpix =
            GetHealpixNested(x, y, z);

        Debug.Log(
            $"Player Gaia chunk:\n" +
            $"  Position: {position}\n" +
            $"  Distance: {distance:F2} pc\n" +
            $"  Shell: {shell}\n" +
            $"  HEALPix: {healpix}"
        );

        if (!indexReader.TryGetChunk(
            (uint)healpix,
            (ushort)shell,
            out GaiaChunk chunk))
        {
            Debug.Log(
                $"No Gaia chunk exists for " +
                $"HEALPix={healpix}, shell={shell}."
            );
            return;
        }

        Debug.Log(
            $"Loading player chunk:\n" +
            $"  Chunk ID: {chunk.chunkId}\n" +
            $"  HEALPix: {chunk.healpixId}\n" +
            $"  Shell: {chunk.shellId}\n" +
            $"  Stars: {chunk.starCount}\n" +
            $"  Path: {indexReader.GetChunkPath(chunk)}"
        );

        LoadChunk(chunk);
    }

    private void LoadChunk(GaiaChunk chunk)
    {
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
                "processed",
                relativePath
            );

        if (!File.Exists(fullPath))
        {
            Debug.LogError(
                $"Gaia chunk file not found:\n{fullPath}"
            );
            return;
        }

        long fileSize =
            new FileInfo(fullPath).Length;

        long expectedSize =
            (long)chunk.starCount * STAR_SIZE;

        if (fileSize != expectedSize)
        {
            Debug.LogError(
                $"Gaia chunk size mismatch.\n" +
                $"Expected {expectedSize} bytes, " +
                $"got {fileSize} bytes."
            );
            return;
        }

        byte[] data =
            File.ReadAllBytes(fullPath);

        using MemoryStream stream =
            new MemoryStream(data);

        using BinaryReader reader =
            new BinaryReader(stream);

        for (int i = 0;
             i < chunk.starCount;
             i++)
        {
            GaiaStar star =
                ReadStar(reader);

            CreateTestStar(
                star,
                chunk.chunkId,
                i
            );
        }

        Debug.Log(
            $"Rendered chunk {chunk.chunkId}: " +
            $"{chunk.starCount} stars."
        );
    }

    private GaiaStar ReadStar(
        BinaryReader reader)
    {
        GaiaStar star = new GaiaStar();

        star.sourceId =
            reader.ReadUInt64();

        star.x =
            reader.ReadSingle();

        star.y =
            reader.ReadSingle();

        star.z =
            reader.ReadSingle();

        star.gMagnitude =
            reader.ReadSingle();

        star.bpRp =
            reader.ReadSingle();

        star.teff =
            reader.ReadSingle();

        star.logg =
            reader.ReadSingle();

        star.metallicity =
            reader.ReadSingle();

        star.radius =
            reader.ReadSingle();

        star.a0 =
            reader.ReadSingle();

        star.ag =
            reader.ReadSingle();

        star.abp =
            reader.ReadSingle();

        star.arp =
            reader.ReadSingle();

        star.ebpRp =
            reader.ReadSingle();

        star.flags =
            reader.ReadByte();

        return star;
    }

    private GameObject CreateTestStar(
        GaiaStar star,
        uint chunkId,
        int index)
    {
        GameObject starObject =
            GameObject.CreatePrimitive(
                PrimitiveType.Sphere
            );

        starObject.name =
            $"GaiaStar_Chunk{chunkId}_Star{index}";

        starObject.transform.position =
            new Vector3(
                star.x,
                star.y,
                star.z
            );

        // Temporary size so stars are visible.
        starObject.transform.localScale =
            Vector3.one * 10f;

        return starObject;
    }

    private int GetShell(
        double distance)
    {
        if (distance <= 0)
            return -1;

        if (distance < 100)
            return 0;

        if (distance < 250)
            return 1;

        if (distance < 500)
            return 2;

        if (distance < 1000)
            return 3;

        if (distance < 2000)
            return 4;

        if (distance < 5000)
            return 5;

        if (distance < 10000)
            return 6;

        if (distance < 20000)
            return 7;

        if (distance < 40000)
            return 8;

        return 9;
    }

    private int GetHealpixNested(
        double x,
        double y,
        double z)
    {
        double r =
            System.Math.Sqrt(
                x * x +
                y * y +
                z * z
            );

        if (r == 0)
            return 0;

        double zNorm = z / r;

        double phi =
            System.Math.Atan2(y, x);

        if (phi < 0)
            phi += 2.0 * System.Math.PI;

        double za =
            System.Math.Abs(zNorm);

        double tt =
            phi / (0.5 * System.Math.PI);

        int face;
        double ix;
        double iy;

        if (za <= 2.0 / 3.0)
        {
            double temp1 =
                HEALPIX_NSIDE *
                (0.5 + tt - 0.75 * zNorm);

            double temp2 =
                HEALPIX_NSIDE *
                (0.5 + tt + 0.75 * zNorm);

            int jp =
                FloorToInt(temp1);

            int jm =
                FloorToInt(temp2);

            int ifp =
                jp / HEALPIX_NSIDE;

            int ifm =
                jm / HEALPIX_NSIDE;

            if (ifp == ifm)
                face = ifp;
            else if (ifp < ifm)
                face = ifp + 4;
            else
                face = ifm + 8;

            ix =
                Mod(
                    jm,
                    HEALPIX_NSIDE
                );

            iy =
                Mod(
                    jp,
                    HEALPIX_NSIDE
                );
        }
        else
        {
            double tp =
                tt -
                System.Math.Floor(tt);

            double tmp =
                HEALPIX_NSIDE *
                System.Math.Sqrt(
                    3.0 * (1.0 - za)
                );

            int jp =
                FloorToInt(
                    tp * tmp
                );

            int jm =
                FloorToInt(
                    (1.0 - tp) * tmp
                );

            if (zNorm >= 0)
            {
                face =
                    (int)System.Math.Floor(tt);

                face %= 4;

                ix = jm;
                iy = jp;
            }
            else
            {
                face =
                    8 +
                    (
                        (int)System.Math.Floor(tt)
                        % 4
                    );

                ix = jp;
                iy = jm;
            }
        }

        int xPixel =
            Mathf.Clamp(
                FloorToInt(ix),
                0,
                HEALPIX_NSIDE - 1
            );

        int yPixel =
            Mathf.Clamp(
                FloorToInt(iy),
                0,
                HEALPIX_NSIDE - 1
            );

        return XYToNested(
            face,
            xPixel,
            yPixel
        );
    }

    private int XYToNested(
        int face,
        int x,
        int y)
    {
        int pixel = 0;

        for (int bit = 0;
             bit < HEALPIX_LEVEL;
             bit++)
        {
            pixel |=
                ((x >> bit) & 1)
                << (2 * bit);

            pixel |=
                ((y >> bit) & 1)
                << (2 * bit + 1);
        }

        return
            face *
            HEALPIX_NSIDE *
            HEALPIX_NSIDE
            + pixel;
    }

    private int FloorToInt(
        double value)
    {
        return (int)System.Math.Floor(value);
    }

    private int Mod(
        int value,
        int modulus)
    {
        int result =
            value % modulus;

        if (result < 0)
            result += modulus;

        return result;
    }
}

[Serializable]
public class GaiaStar
{
    public ulong sourceId;

    public float x;
    public float y;
    public float z;

    public float gMagnitude;
    public float bpRp;

    public float teff;
    public float logg;
    public float metallicity;
    public float radius;

    public float a0;
    public float ag;
    public float abp;
    public float arp;
    public float ebpRp;

    public byte flags;
}