using System;
using System.IO;
using UnityEngine;

public class GaiaClusterFilter : MonoBehaviour
{
    [Header("Data")]
    [SerializeField]
    private string clusterDirectoryDataPath = "Clusters/HuntReffert2024";

    [Header("Filter")]
    [SerializeField]
    private bool filterEnabled = false;

    [SerializeField]
    private bool useMembershipProbability = false;

    [SerializeField]
    [Range(0.0f, 1.0f)]
    private float minMembershipProbability = 0.0f;

    [SerializeField]
    private bool useAgeRange = false;

    [SerializeField]
    private float minLogAge = 0.0f;

    [SerializeField]
    private float maxLogAge = 20.0f;

    [SerializeField]
    private bool useTotalMassRange = false;

    [SerializeField]
    private float minTotalMass = 0.0f;

    [SerializeField]
    private float maxTotalMass = 100000.0f;

    [SerializeField]
    private bool useTypeCode = false;

    [SerializeField]
    [Range(0, 255)]
    private int typeCode = 0;

    [SerializeField]
    private bool useDistanceRange = false;

    // WARNING: if you want to use filter by distance and degrees,
    // first make sure you appropriatly trastlate the raw clusters data to galactic coordinates with sun at the venter when running python process scripts.

    [SerializeField]
    private float minDistancePc = 0.0f;

    [SerializeField]
    private float maxDistancePc = 100000.0f;

    [SerializeField]
    private bool requireInRj = false;

    [SerializeField]
    private bool requireInRt = false;


    private GaiaClusterDatabase database;
    private int lastSettingsHash;

    public bool FilterEnabled
    {
        get { return filterEnabled; }
    }

    public GaiaClusterDatabase Database
    {
        get { return database; }
    }

    public int Revision
    {
        get { return ComputeSettingsHash(); }
    }

    public GaiaClusterFilterSettings CreateSnapshot()
    {
        return new GaiaClusterFilterSettings
        {
            enabled =
                filterEnabled,

            useMembershipProbability =
                useMembershipProbability,

            minMembershipProbability =
                minMembershipProbability,

            useAgeRange =
                useAgeRange,

            minLogAge =
                minLogAge,

            maxLogAge =
                maxLogAge,

            useDistanceRange =
                useDistanceRange,

            minDistancePc =
                minDistancePc,

            maxDistancePc =
                maxDistancePc,

            useTotalMassRange =
                useTotalMassRange,

            minTotalMass =
                minTotalMass,

            maxTotalMass =
                maxTotalMass,

            requireInRj =
                requireInRj,

            requireInRt =
                requireInRt,

            useTypeCode =
                useTypeCode,

            typeCode =
                (byte)Mathf.Clamp(
                    typeCode,
                    0,
                    255
                )
        };
    }

    private void Awake()
    {
        try
        {
            string dataRoot = ResolveDataRoot();

            database = GaiaClusterDatabase.Load(DataPath.Root, clusterDirectoryDataPath);

            Debug.Log(
                $"GaiaClusterFilter: Loaded cluster database. " +
                $"Clusters={database.ClusterCount}, " +
                $"Memberships={database.MembershipCount}, " +
                $"Path={Path.Combine(DataPath.Root, clusterDirectoryDataPath)}"
            );

            lastSettingsHash = ComputeSettingsHash();
        }
        catch (Exception e)
        {
            Debug.LogError(
                "GaiaClusterFilter: Failed to load cluster database:\n" +
                e
            );
        }
    }

    private static string ResolveDataRoot()
    {
#if UNITY_EDITOR
        return Path.GetFullPath(
            Path.Combine(Application.dataPath, "..")
        );

#elif UNITY_ANDROID
    using (var unityPlayer = new AndroidJavaClass(
        "com.unity3d.player.UnityPlayer"))
    using (var activity = unityPlayer.GetStatic<AndroidJavaObject>(
        "currentActivity"))
    using (var obbDirectory = activity.Call<AndroidJavaObject>(
        "getObbDir"))
    {
        string obbRoot = obbDirectory.Call<string>(
            "getAbsolutePath"
        );

        if (string.IsNullOrEmpty(obbRoot))
        {
            throw new IOException(
                "Android getObbDir() returned an empty path."
            );
        }

        return obbRoot;
    }

#else
    return Application.persistentDataPath;
#endif
    }

    public bool ShouldKeep(
        ulong sourceId,
        GaiaClusterFilterSettings settings)
    {
        if (!settings.enabled)
        {
            return true;
        }

        if (database == null)
        {
            return false;
        }

        return database.StarHasMatchingCluster(
            sourceId,
            settings
        );
    }

    private int ComputeSettingsHash()
    {
        unchecked
        {
            int hash = 17;

            hash = hash * 31 +
                   (filterEnabled ? 1 : 0);

            hash = hash * 31 +
                   (useMembershipProbability ? 1 : 0);

            hash = hash * 31 +
                   minMembershipProbability.GetHashCode();

            hash = hash * 31 +
                   (useAgeRange ? 1 : 0);

            hash = hash * 31 +
                   minLogAge.GetHashCode();

            hash = hash * 31 +
                   maxLogAge.GetHashCode();

            hash = hash * 31 +
                   (useDistanceRange ? 1 : 0);

            hash = hash * 31 +
                   minDistancePc.GetHashCode();

            hash = hash * 31 +
                   maxDistancePc.GetHashCode();

            hash = hash * 31 +
                   (useTotalMassRange ? 1 : 0);

            hash = hash * 31 +
                   minTotalMass.GetHashCode();

            hash = hash * 31 +
                   maxTotalMass.GetHashCode();

            hash = hash * 31 +
                   (requireInRj ? 1 : 0);

            hash = hash * 31 +
                   (requireInRt ? 1 : 0);

            hash = hash * 31 +
                   (useTypeCode ? 1 : 0);

            hash = hash * 31 +
                   typeCode;

            return hash;
        }
    }

    public void ApplySettings(
        GaiaClusterFilterSettings settings
    )
    {
        filterEnabled =
            settings.enabled;

        useMembershipProbability =
            settings.useMembershipProbability;

        minMembershipProbability =
            settings.minMembershipProbability;

        useAgeRange =
            settings.useAgeRange;

        minLogAge =
            settings.minLogAge;

        maxLogAge =
            settings.maxLogAge;

        useDistanceRange =
            settings.useDistanceRange;

        minDistancePc =
            settings.minDistancePc;

        maxDistancePc =
            settings.maxDistancePc;

        useTotalMassRange =
            settings.useTotalMassRange;

        minTotalMass =
            settings.minTotalMass;

        maxTotalMass =
            settings.maxTotalMass;

        requireInRj =
            settings.requireInRj;

        requireInRt =
            settings.requireInRt;

        useTypeCode =
            settings.useTypeCode;

        typeCode =
            settings.typeCode;
    }
}

[Serializable]
public struct GaiaClusterFilterSettings
{
    public bool enabled;

    public bool useMembershipProbability;
    public float minMembershipProbability;

    public bool useAgeRange;
    public float minLogAge;
    public float maxLogAge;

    public bool useDistanceRange;
    public float minDistancePc;
    public float maxDistancePc;

    public bool useTotalMassRange;
    public float minTotalMass;
    public float maxTotalMass;

    public bool requireInRj;
    public bool requireInRt;

    public bool useTypeCode;
    public byte typeCode;
}