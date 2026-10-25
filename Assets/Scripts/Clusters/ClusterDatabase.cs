using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;

public sealed class ClusterDatabase
{
    private const uint CLUSTER_MAGIC = 0x434C5354;
    private const uint CLUSTER_VERSION = 1;
    private const int CLUSTER_HEADER_SIZE = 16;
    private const int CLUSTER_RECORD_SIZE = 136;

    private const uint MEMBER_MAGIC = 0x4D454D42;
    private const uint MEMBER_VERSION = 1;
    private const int MEMBER_HEADER_SIZE = 16;
    private const int MEMBER_RECORD_SIZE = 16;

    private readonly ClusterRecord[] clusters;
    private readonly MembershipRecord[] memberships;

    private readonly Dictionary<ushort, int> clusterIndexById;
    private readonly bool[,] hasMembershipByFlags;
    private readonly float[,] maxProbabilityByFlags;

    private ClusterDatabase(
        ClusterRecord[] clusters,
        MembershipRecord[] memberships)
    {
        this.clusters = clusters;
        this.memberships = memberships;

        clusterIndexById =
            new Dictionary<ushort, int>(
                clusters.Length
            );

        for (int i = 0; i < clusters.Length; i++)
        {
            clusterIndexById[
                clusters[i].id
            ] = i;
        }

        hasMembershipByFlags =
            new bool[clusters.Length, 4];

        maxProbabilityByFlags =
            new float[clusters.Length, 4];

        for (int i = 0; i < clusters.Length; i++)
        {
            for (int flags = 0; flags < 4; flags++)
            {
                maxProbabilityByFlags[i, flags] =
                    float.NegativeInfinity;
            }
        }

        for (int i = 0; i < memberships.Length; i++)
        {
            MembershipRecord membership = memberships[i];

            if (!clusterIndexById.TryGetValue(
                    membership.clusterId,
                    out int clusterIndex))
            {
                continue;
            }

            // Bit 0 = inRj; bit 1 = inRt.
            int flagCombination = membership.flags & 3;

            hasMembershipByFlags[
                clusterIndex,
                flagCombination] = true;

            float probability = membership.probability;

            if (!float.IsNaN(probability) &&
                !float.IsInfinity(probability) &&
                probability > maxProbabilityByFlags[
                    clusterIndex,
                    flagCombination])
            {
                maxProbabilityByFlags[
                    clusterIndex,
                    flagCombination] = probability;
            }
        }
    }

    public ClusterRecord GetClusterAt(int index)
    {
        if (index < 0 || index >= clusters.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index));
        }

        return clusters[index];
    }

    public int ClusterCount
    {
        get { return clusters.Length; }
    }

    public int MembershipCount
    {
        get { return memberships.Length; }
    }

    public static ClusterDatabase Load(
        string projectRoot,
        string relativeDirectory)
    {
        string directory =
            Path.Combine(
                projectRoot,
                relativeDirectory
            );

        string clustersPath =
            Path.Combine(
                directory,
                "clusters.bin"
            );

        string membersPath =
            Path.Combine(
                directory,
                "members.bin"
            );

        if (!File.Exists(clustersPath))
        {
            throw new FileNotFoundException(
                "Cluster catalogue not found.",
                clustersPath
            );
        }

        if (!File.Exists(membersPath))
        {
            throw new FileNotFoundException(
                "Cluster membership catalogue not found.",
                membersPath
            );
        }

        ClusterRecord[] clusters =
            ReadClusters(
                clustersPath
            );

        string namesPath = Path.Combine(directory, "cluster_names.tsv");
        Dictionary<ushort, string> names = ReadClusterNames(namesPath);

        for (int i = 0; i < clusters.Length; i++)
        {
            ClusterRecord cluster = clusters[i];

            cluster.name = names.TryGetValue(cluster.id, out string name)
                ? name
                : $"Cluster {cluster.id}";

            clusters[i] = cluster;
        }

        MembershipRecord[] memberships =
            ReadMemberships(
                membersPath
            );

        return new ClusterDatabase(
            clusters,
            memberships
        );
    }

    private static Dictionary<ushort, string> ReadClusterNames(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Cluster names file not found.", path);

        var names = new Dictionary<ushort, string>();

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;

            if (!ushort.TryParse(
                    line.Substring(0, tab).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out ushort id))
                continue;

            string name = line.Substring(tab + 1).Trim();

            if (name.Length > 0)
                names[id] = name;
        }

        return names;
    }

    private static ClusterRecord[] ReadClusters(
        string path)
    {
        long fileSize =
            new FileInfo(path).Length;

        if (fileSize < CLUSTER_HEADER_SIZE)
        {
            throw new InvalidDataException(
                "clusters.bin is too small to contain a header."
            );
        }

        using FileStream file =
            File.OpenRead(path);

        using BinaryReader reader =
            new BinaryReader(file);

        uint magic =
            reader.ReadUInt32();

        uint version =
            reader.ReadUInt32();

        uint recordSize =
            reader.ReadUInt32();

        uint count =
            reader.ReadUInt32();

        if (magic != CLUSTER_MAGIC)
        {
            throw new InvalidDataException(
                $"Invalid clusters.bin magic: 0x{magic:X8}"
            );
        }

        if (version != CLUSTER_VERSION)
        {
            throw new InvalidDataException(
                $"Unsupported clusters.bin version: {version}"
            );
        }

        if (recordSize != CLUSTER_RECORD_SIZE)
        {
            throw new InvalidDataException(
                $"Unexpected cluster record size: " +
                $"{recordSize}. Expected {CLUSTER_RECORD_SIZE}."
            );
        }

        long expectedSize =
            CLUSTER_HEADER_SIZE +
            (long)count * recordSize;

        if (fileSize != expectedSize)
        {
            throw new InvalidDataException(
                "Invalid clusters.bin size.\n" +
                $"Expected: {expectedSize} bytes\n" +
                $"Actual:   {fileSize} bytes"
            );
        }

        ClusterRecord[] result =
            new ClusterRecord[count];

        for (int i = 0; i < count; i++)
        {
            ClusterRecord cluster =
                new ClusterRecord();

            cluster.id =
                reader.ReadUInt16();

            cluster.type =
                reader.ReadByte();

            cluster.flags =
                reader.ReadByte();

            cluster.n =
                reader.ReadUInt32();

            cluster.nt =
                reader.ReadUInt32();

            cluster.glon =
                reader.ReadSingle();

            cluster.glat =
                reader.ReadSingle();

            cluster.r50pc =
                reader.ReadSingle();

            cluster.rcpc =
                reader.ReadSingle();

            cluster.rtpc =
                reader.ReadSingle();

            cluster.rtotpc =
                reader.ReadSingle();

            cluster.pmRA =
                reader.ReadSingle();

            cluster.pmDE =
                reader.ReadSingle();

            cluster.plx =
                reader.ReadSingle();

            cluster.dist16 =
                reader.ReadSingle();

            cluster.dist50 =
                reader.ReadSingle();

            cluster.dist84 =
                reader.ReadSingle();

            cluster.x =
                reader.ReadSingle();

            cluster.y =
                reader.ReadSingle();

            cluster.z =
                reader.ReadSingle();

            cluster.rv =
                reader.ReadSingle();

            cluster.logAge16 =
                reader.ReadSingle();

            cluster.logAge50 =
                reader.ReadSingle();

            cluster.logAge84 =
                reader.ReadSingle();

            cluster.av16 =
                reader.ReadSingle();

            cluster.av50 =
                reader.ReadSingle();

            cluster.av84 =
                reader.ReadSingle();

            cluster.r50Jpc =
                reader.ReadSingle();

            cluster.rJpc =
                reader.ReadSingle();

            cluster.probJ =
                reader.ReadSingle();

            cluster.nJ =
                reader.ReadInt32();

            cluster.massJ =
                reader.ReadSingle();

            cluster.eMassJ =
                reader.ReadSingle();

            cluster.massTot =
                reader.ReadSingle();

            cluster.eMassTot =
                reader.ReadSingle();

            cluster.minClSize =
                reader.ReadSingle();

            result[i] =
                cluster;
        }

        return result;
    }

    private static MembershipRecord[] ReadMemberships(
        string path)
    {
        long fileSize =
            new FileInfo(path).Length;

        if (fileSize < MEMBER_HEADER_SIZE)
        {
            throw new InvalidDataException(
                "members.bin is too small to contain a header."
            );
        }

        using FileStream file =
            File.OpenRead(path);

        using BinaryReader reader =
            new BinaryReader(file);

        uint magic =
            reader.ReadUInt32();

        uint version =
            reader.ReadUInt32();

        uint recordSize =
            reader.ReadUInt32();

        uint count =
            reader.ReadUInt32();

        if (magic != MEMBER_MAGIC)
        {
            throw new InvalidDataException(
                $"Invalid members.bin magic: 0x{magic:X8}"
            );
        }

        if (version != MEMBER_VERSION)
        {
            throw new InvalidDataException(
                $"Unsupported members.bin version: {version}"
            );
        }

        if (recordSize != MEMBER_RECORD_SIZE)
        {
            throw new InvalidDataException(
                $"Unexpected membership record size: " +
                $"{recordSize}. Expected {MEMBER_RECORD_SIZE}."
            );
        }

        long expectedSize =
            MEMBER_HEADER_SIZE +
            (long)count * recordSize;

        if (fileSize != expectedSize)
        {
            throw new InvalidDataException(
                "Invalid members.bin size.\n" +
                $"Expected: {expectedSize} bytes\n" +
                $"Actual:   {fileSize} bytes"
            );
        }

        MembershipRecord[] result =
            new MembershipRecord[count];

        for (int i = 0; i < count; i++)
        {
            MembershipRecord membership =
                new MembershipRecord();

            membership.sourceId =
                reader.ReadUInt64();

            membership.clusterId =
                reader.ReadUInt16();

            membership.flags =
                reader.ReadUInt16();

            membership.probability =
                reader.ReadSingle();

            result[i] =
                membership;
        }

        return result;
    }

    public bool TryGetCluster(
        ushort clusterId,
        out ClusterRecord cluster)
    {
        int index;

        if (clusterIndexById.TryGetValue(
                clusterId,
                out index))
        {
            cluster =
                clusters[index];

            return true;
        }

        cluster =
            default;

        return false;
    }

    public bool StarHasMatchingCluster(
        ulong sourceId,
        GaiaClusterFilterSettings settings)
    {
        int first =
            LowerBoundSourceId(
                sourceId
            );

        if (first >= memberships.Length ||
            memberships[first].sourceId != sourceId)
        {
            return false;
        }

        for (
            int i = first;
            i < memberships.Length;
            i++)
        {
            MembershipRecord membership =
                memberships[i];

            if (membership.sourceId != sourceId)
            {
                break;
            }

            ClusterRecord cluster;

            if (!TryGetCluster(
                    membership.clusterId,
                    out cluster))
            {
                continue;
            }

            if (Matches(
                    cluster,
                    membership,
                    settings))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(
        ClusterRecord cluster,
        MembershipRecord membership,
        GaiaClusterFilterSettings settings)
    {
        if (settings.useMembershipProbability)
        {
            if (membership.probability <
                settings.minMembershipProbability)
            {
                return false;
            }
        }

        if (settings.useAgeRange)
        {
            if (cluster.logAge50 < settings.minLogAge ||
                cluster.logAge50 > settings.maxLogAge)
            {
                return false;
            }
        }

        if (settings.useDistanceRange)
        {
            if (cluster.dist50 < settings.minDistancePc ||
                cluster.dist50 > settings.maxDistancePc)
            {
                return false;
            }
        }

        if (settings.useTotalMassRange)
        {
            if (cluster.massTot < settings.minTotalMass ||
                cluster.massTot > settings.maxTotalMass)
            {
                return false;
            }
        }

        if (settings.requireInRj)
        {
            const ushort IN_RJ = 1;

            if ((membership.flags & IN_RJ) == 0)
            {
                return false;
            }
        }

        if (settings.requireInRt)
        {
            const ushort IN_RT = 2;

            if ((membership.flags & IN_RT) == 0)
            {
                return false;
            }
        }

        if (settings.useTypeCode)
        {
            if (cluster.type != settings.typeCode)
            {
                return false;
            }
        }

        return true;
    }

    private int LowerBoundSourceId(
        ulong sourceId)
    {
        int low = 0;
        int high = memberships.Length;

        while (low < high)
        {
            int mid =
                low +
                ((high - low) >> 1);

            if (memberships[mid].sourceId <
                sourceId)
            {
                low =
                    mid + 1;
            }
            else
            {
                high =
                    mid;
            }
        }

        return low;
    }

    public struct ClusterRecord
    {
        public ushort id;
        public string name;
        public byte type;
        public byte flags;

        public uint n;
        public uint nt;

        public float glon;
        public float glat;

        public float r50pc;
        public float rcpc;
        public float rtpc;
        public float rtotpc;

        public float pmRA;
        public float pmDE;
        public float plx;

        public float dist16;
        public float dist50;
        public float dist84;

        public float x;
        public float y;
        public float z;

        public float rv;

        public float logAge16;
        public float logAge50;
        public float logAge84;

        public float av16;
        public float av50;
        public float av84;

        public float r50Jpc;
        public float rJpc;
        public float probJ;

        public int nJ;

        public float massJ;
        public float eMassJ;
        public float massTot;
        public float eMassTot;

        public float minClSize;
    }

    public struct MembershipRecord
    {
        public ulong sourceId;
        public ushort clusterId;
        public ushort flags;
        public float probability;
    }

    public bool ClusterHasMatchingMembership(
        ushort clusterId,
        GaiaClusterFilterSettings settings)
    {
        if (!clusterIndexById.TryGetValue(
                clusterId,
                out int clusterIndex))
        {
            return false;
        }

        for (int combination = 0; combination < 4; combination++)
        {
            if (settings.requireInRj &&
                (combination & 1) == 0)
            {
                continue;
            }

            if (settings.requireInRt &&
                (combination & 2) == 0)
            {
                continue;
            }

            if (!hasMembershipByFlags[
                    clusterIndex,
                    combination])
            {
                continue;
            }

            if (settings.useMembershipProbability)
            {
                float maxProbability =
                    maxProbabilityByFlags[
                        clusterIndex,
                        combination];

                if (float.IsNegativeInfinity(maxProbability) ||
                    maxProbability < settings.minMembershipProbability)
                {
                    continue;
                }
            }

            return true;
        }

        return false;
    }
}