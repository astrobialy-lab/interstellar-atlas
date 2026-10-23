
using UnityEngine;

[System.Serializable]
public struct GaiaStarFilterSettings
{
    public int displayStage;

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

    public bool IsNoStarsStage => displayStage == 5;

    public bool IsActive =>
        IsNoStarsStage ||
        useMagnitudeLimit ||
        HasAnyCriterion;

    public bool Matches(
        float magnitude,
        float massSolar,
        float ageGyr,
        int evolutionStage,
        float temperatureK)
    {
        // Stage 5: display no stars.
        if (displayStage == 5)
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
                return false;
        }

        if (useAgeRange)
        {
            if (!IsFinite(ageGyr) ||
                ageGyr < minAgeGyr ||
                ageGyr > maxAgeGyr)
                return false;
        }

        if (useEvolutionStageRange)
        {
            if (evolutionStage < 0 ||
                evolutionStage < minEvolutionStage ||
                evolutionStage > maxEvolutionStage)
                return false;
        }

        if (useTemperatureRange)
        {
            if (!IsFinite(temperatureK) ||
                temperatureK < minTemperatureK ||
                temperatureK > maxTemperatureK)
                return false;
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
    [SerializeField] private ClusterMarkerGenerator clusterMarkerGenerator;

    [Header("Settings")]
    [SerializeField] private GaiaStarFilterSettings settings =
        new GaiaStarFilterSettings
        {
            displayStage = 4,

            useMagnitudeLimit = false,
            maxMagnitude = 0.85f,

            minMassSolar = 0f,
            maxMassSolar = 100f,

            minAgeGyr = 0f,
            maxAgeGyr = 13.5f,

            minEvolutionStage = 100,
            maxEvolutionStage = 1300,

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

    public void ApplySettings(
        GaiaStarFilterSettings newSettings)
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

        if (newSettings.displayStage < 1 ||
            newSettings.displayStage > 5)
        {
            newSettings.displayStage = 4;
        }

        if (float.IsNaN(newSettings.maxMagnitude) ||
            float.IsInfinity(newSettings.maxMagnitude))
        {
            newSettings.maxMagnitude = 0.85f;
        }

        // Allow a brightness filter to be enabled from the menu
        // even when the current state is "no stars".
        if (newSettings.displayStage == 5 &&
            newSettings.useMagnitudeLimit)
        {
            newSettings.displayStage = 4;
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

    public int DisplayStage => settings.displayStage;

    public int CycleDisplayStage()
    {
        // THESE VALUES WERE ADJUSTED FOR STARS PROCESSING SCRIPT THAT FILTERS FOR ALL STARS OVER MASS OF MASS OF 2.0 [SOLAR MASS]
        // Cycle from most stars to fewest stars.
        switch (settings.displayStage)
        {
            case 4:
                settings.displayStage = 3;
                settings.useMagnitudeLimit = true;
                settings.maxMagnitude = 0.85f;
                break;

            case 3:
                settings.displayStage = 2;
                settings.useMagnitudeLimit = true;
                settings.maxMagnitude = -0.2f;
                break;

            case 2:
                settings.displayStage = 1;
                settings.useMagnitudeLimit = true;
                settings.maxMagnitude = -1.5f;
                break;

            case 1:
                settings.displayStage = 5;
                settings.useMagnitudeLimit = false;
                break;

            case 5:
            default:
                settings.displayStage = 4;
                settings.useMagnitudeLimit = false;
                break;
        }

        UpdateClusterSpriteVisibility();

        BrightnessFilterChanged?.Invoke(settings.useMagnitudeLimit, settings.maxMagnitude);

        unchecked
        {
            revision++;
        }

        // THESE VALUES WERE ADJUSTED FOR STARS PROCESSING SCRIPT THAT FILTERS FOR ALL STARS OVER MASS OF MASS OF 2.0 [SOLAR MASS]
        string description = settings.displayStage switch
        {
            1 => "G <= -1.5 (approximately 2%)",
            2 => "G <= -0.2 (approximately 8%)",
            3 => "G <= 0.85 (approximately 40%)",
            4 => "All stars (100%)",
            5 => "No stars (0%)",
            _ => "Unknown"
        };

        Debug.Log(
            $"GaiaStarFilter: Stage {settings.displayStage}/5 - " +
            description);

        return settings.displayStage;
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

    private void Awake()
    {
        // Always start with all stars visible.
        if (settings.displayStage != 4 ||
            settings.useMagnitudeLimit)
        {
            settings.displayStage = 4;
            settings.useMagnitudeLimit = false;

            unchecked
            {
                revision++;
            }
        }
    }

    private void UpdateClusterSpriteVisibility()
    {
        if (clusterMarkerGenerator == null)
            return;

        clusterMarkerGenerator.SetMarkersVisible(
            settings.displayStage == 5
        );
    }
}
