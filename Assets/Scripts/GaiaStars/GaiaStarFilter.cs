
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

    private int revision;

    public int Revision => revision;

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

        // Synchronize the filter menu. This does not cycle brightness.
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

    public bool StarsVisible => settings.showStars;

    public void SetClustersOnly(bool enabled)
    {
        SetStarsVisible(!enabled);
    }

    public void SetStarsVisible(bool visible)
    {
        if (settings.showStars == visible)
        {
            UpdateClusterSpriteVisibility();
            return;
        }

        settings.showStars = visible;

        UpdateClusterSpriteVisibility();

        unchecked
        {
            revision++;
        }
    }

    private void Awake()
    {
        // Always start with stars visible.
        // Preserve all magnitude and stellar-property filter settings.
        settings.showStars = true;
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
            return;

        // Show clusters only when stars are disabled.
        clusterMarkerGenerator.SetMarkersVisible(!settings.showStars);
    }
}
