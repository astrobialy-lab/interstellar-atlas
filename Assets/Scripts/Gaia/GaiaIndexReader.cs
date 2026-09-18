using System;
using System.IO;
using UnityEngine;

public class GaiaIndexReader : MonoBehaviour
{
    [Header("Debug Visualization")]
    [SerializeField] private int debugDepth = 0;
    [SerializeField] private bool drawOctree = true;

    private string IndexPath
    {
        get
        {
            string projectRoot =
                Directory.GetParent(Application.dataPath).FullName;

            return Path.Combine(
                projectRoot,
                "Data",
                "Gaia",
                "processed",
                "index.bin"
            );
        }
    }

    // Must match Python:
    // <QIIIIdddd
    //
    // uint64 node_id
    // uint32 parent_id
    // uint32 first_child
    // uint32 child_count
    // uint32 star_count
    // double center_x
    // double center_y
    // double center_z
    // double half_size
    private const int NODE_SIZE = 52;

    [Serializable]
    public struct GaiaNode
    {
        public ulong nodeId;
        public uint parentId;
        public uint firstChild;
        public uint childCount;
        public uint starCount;

        public double centerX;
        public double centerY;
        public double centerZ;
        public double halfSize;
    }

    private GaiaNode[] nodes;

    void Start()
    {
        LoadIndex();
    }

    void LoadIndex()
    {
        Debug.Log("Loading Gaia index:");
        Debug.Log(IndexPath);

        if (!File.Exists(IndexPath))
        {
            Debug.LogError("Gaia index.bin does not exist!");
            return;
        }

        using (BinaryReader reader =
               new BinaryReader(File.OpenRead(IndexPath)))
        {
            // Header
            uint magic = reader.ReadUInt32();
            uint version = reader.ReadUInt32();
            uint nodeCount = reader.ReadUInt32();

            Debug.Log($"Magic: 0x{magic:X8}");
            Debug.Log($"Version: {version}");
            Debug.Log($"Node count: {nodeCount}");

            if (magic != 0x47414941)
            {
                Debug.LogError("Invalid Gaia index magic!");
                return;
            }

            if (version != 1)
            {
                Debug.LogError($"Unsupported Gaia index version: {version}");
                return;
            }

            nodes = new GaiaNode[nodeCount];

            for (int i = 0; i < nodeCount; i++)
            {
                GaiaNode node = new GaiaNode();

                node.nodeId = reader.ReadUInt64();
                node.parentId = reader.ReadUInt32();
                node.firstChild = reader.ReadUInt32();
                node.childCount = reader.ReadUInt32();
                node.starCount = reader.ReadUInt32();

                node.centerX = reader.ReadDouble();
                node.centerY = reader.ReadDouble();
                node.centerZ = reader.ReadDouble();
                node.halfSize = reader.ReadDouble();

                nodes[i] = node;
            }
        }

        Debug.Log($"Loaded {nodes.Length} Gaia nodes.");

        // Print the first few nodes so we can verify the data.
        int samples = Mathf.Min(10, nodes.Length);

        for (int i = 0; i < samples; i++)
        {
            GaiaNode n = nodes[i];

            Debug.Log(
                $"Node {i}: " +
                $"id={n.nodeId}, " +
                $"firstChild={n.firstChild}, " +
                $"children={n.childCount}, " +
                $"stars={n.starCount}, " +
                $"center=({n.centerX:F2}, {n.centerY:F2}, {n.centerZ:F2}), " +
                $"halfSize={n.halfSize:F2}"
            );
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawOctree || nodes == null)
            return;

        Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 10f);
    }

    private int GetDepth(int nodeIndex)
    {
        int depth = 0;

        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodeIndex == 0)
                return depth;

            for (int j = 0; j < nodes.Length; j++)
            {
                GaiaNode parent = nodes[j];

                if (parent.childCount == 0)
                    continue;

                if (nodeIndex >= parent.firstChild &&
                    nodeIndex < parent.firstChild + parent.childCount)
                {
                    nodeIndex = j;
                    depth++;
                    break;
                }
            }
        }

        return depth;
    }
}