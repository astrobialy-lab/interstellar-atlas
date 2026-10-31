
using UnityEngine;

[System.Serializable]
public struct GaiaStarFilterSettings
{
    public bool showStars;

    public bool useMagnitudeLimit;
    public float maxMagnitude;

    public bool useMassRange;
    public float minMassSolar;
    public float maxMassSolar;

    public bool useAgeRange;
    public float minAgeGyr;
    public float maxAgeGyr;

    public bool useEvolutionStageRange;
    public int minEvolutionStage;
    public int maxEvolutionStage;

    public bool useTemperatureRange;
    public float minTemperatureK;
    public float maxTemperatureK;

    public bool HasAnyCriterion =>
        useMassRange ||
        useAgeRange ||
        useEvolutionStageRange ||
        useTemperatureRange;

    public bool IsActive =>
        !showStars ||
        useMagnitudeLimit ||
        HasAnyCriterion;

    public bool Matches(
        float magnitude,
        float massSolar,
        float ageGyr,
        int evolutionStage,
        float temperatureK)
    {
        // If stars are disabled, reject every star.
        if (!showStars)
            return false;

        // Optional apparent G-magnitude limit.
        // Lower magnitude values represent brighter stars.
        if (useMagnitudeLimit)
        {
            if (!IsFinite(magnitude) ||
                magnitude > maxMagnitude)
            {
                return false;
            }
        }

        if (useMassRange)
        {
            if (!IsFinite(massSolar) ||
                massSolar < minMassSolar ||
                massSolar > maxMassSolar)
            {
                return false;
            }
        }

        if (useAgeRange)
        {
            if (!IsFinite(ageGyr) ||
                ageGyr < minAgeGyr ||
                ageGyr > maxAgeGyr)
            {
                return false;
            }
        }

        if (useEvolutionStageRange)
        {
            if (evolutionStage < 0 ||
                evolutionStage < minEvolutionStage ||
                evolutionStage > maxEvolutionStage)
            {
                return false;
            }
        }

        if (useTemperatureRange)
        {
            if (!IsFinite(temperatureK) ||
                temperatureK < minTemperatureK ||
                temperatureK > maxTemperatureK)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) &&
               !float.IsInfinity(value);
    }
}

public sealed class GaiaStarFilter : MonoBehaviour
{
    [Header("Cluster Sprites")]
    [SerializeField]
    private ClusterMarkerGenerator clusterMarkerGenerator;

    [Header("Settings")]
    [SerializeField]
    private GaiaStarFilterSettings settings =
        new GaiaStarFilterSettings
        {
            showStars = true,

            useMagnitudeLimit = false,
            maxMagnitude = 0.85f,

            useMassRange = false,
            minMassSolar = 0f,
            maxMassSolar = 100f,

            useAgeRange = false,
            minAgeGyr = 0f,
            maxAgeGyr = 13.5f,

            useEvolutionStageRange = false,
            minEvolutionStage = 100,
            maxEvolutionStage = 1300,

            useTemperatureRange = false,
            minTemperatureK = 0f,
            maxTemperatureK = 50000f
        };

    // Cluster visibility is independent of star visibility.
    private bool showClusters = false;

    private int revision;

    public int Revision => revision;

    public bool StarsVisible => settings.showStars;

    public bool ClustersVisible => showClusters;

    public event System.Action<bool, float> BrightnessFilterChanged;

    public GaiaStarFilterSettings CreateSnapshot()
    {
        return settings;
    }

    public void ApplySettings(GaiaStarFilterSettings newSettings)
    {
        NormalizeFloatRange(
            ref newSettings.minMassSolar,
            ref newSettings.maxMassSolar,
            0f,
            100f);

        NormalizeFloatRange(
            ref newSettings.minAgeGyr,
            ref newSettings.maxAgeGyr,
            0f,
            13.5f);

        NormalizeFloatRange(
            ref newSettings.minTemperatureK,
            ref newSettings.maxTemperatureK,
            0f,
            50000f);

        if (newSettings.minEvolutionStage >
            newSettings.maxEvolutionStage)
        {
            int temp = newSettings.minEvolutionStage;

            newSettings.minEvolutionStage =
                newSettings.maxEvolutionStage;

            newSettings.maxEvolutionStage = temp;
        }

        if (float.IsNaN(newSettings.maxMagnitude) ||
            float.IsInfinity(newSettings.maxMagnitude))
        {
            newSettings.maxMagnitude = 0.85f;
        }

        settings = newSettings;

        UpdateClusterSpriteVisibility();

        BrightnessFilterChanged?.Invoke(
            settings.useMagnitudeLimit,
            settings.maxMagnitude);

        unchecked
        {
            revision++;
        }
    }

    private static void NormalizeFloatRange(
        ref float minimum,
        ref float maximum,
        float defaultMinimum,
        float defaultMaximum)
    {
        if (float.IsNaN(minimum) ||
            float.IsInfinity(minimum))
        {
            minimum = defaultMinimum;
        }

        if (float.IsNaN(maximum) ||
            float.IsInfinity(maximum))
        {
            maximum = defaultMaximum;
        }

        if (minimum > maximum)
        {
            float temp = minimum;
            minimum = maximum;
            maximum = temp;
        }
    }

    // ========================================================
    // DISPLAY VISIBILITY
    // ========================================================

    public void SetDisplayVisibility(
        bool starsVisible,
        bool clustersVisible)
    {
        bool starsChanged =
            settings.showStars != starsVisible;

        bool clustersChanged =
            showClusters != clustersVisible;

        if (!starsChanged && !clustersChanged)
        {
            UpdateClusterSpriteVisibility();
            return;
        }

        settings.showStars = starsVisible;
        showClusters = clustersVisible;

        UpdateClusterSpriteVisibility();

        // Only star visibility affects the star LOD loader.
        // Changing cluster visibility alone does not reload stars.
        if (starsChanged)
        {
            unchecked
            {
                revision++;
            }
        }
    }

    public void SetStarsVisible(bool visible)
    {
        SetDisplayVisibility(visible, showClusters);
    }

    public void SetClustersVisible(bool visible)
    {
        SetDisplayVisibility(settings.showStars, visible);
    }

    // Convenience method for the traditional clusters-only mode.
    public void SetClustersOnly(bool enabled)
    {
        SetDisplayVisibility(
            !enabled,
            enabled
        );
    }

    // ========================================================
    // INITIALIZATION
    // ========================================================

    private void Awake()
    {
        settings.showStars = true;
        showClusters = false;
    }

    private void Start()
    {
        if (clusterMarkerGenerator == null)
        {
            clusterMarkerGenerator =
                FindAnyObjectByType<ClusterMarkerGenerator>();
        }

        UpdateClusterSpriteVisibility();
    }

    private void UpdateClusterSpriteVisibility()
    {
        if (clusterMarkerGenerator == null)
        {
            clusterMarkerGenerator =
                FindAnyObjectByType<ClusterMarkerGenerator>();
        }

        if (clusterMarkerGenerator == null)
            return;

        clusterMarkerGenerator.SetMarkersVisible(showClusters);
    }
}