
using UnityEngine;

[System.Serializable]
public struct GaiaStarFilterSettings
{
    public bool enabled;

    public int displayStage;

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

    private bool UsesMagnitudeLimit =>
        displayStage >= 1 && displayStage <= 3;

    public bool IsActive =>
        IsNoStarsStage ||
        UsesMagnitudeLimit ||
        (enabled && HasAnyCriterion);

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

        // THESE VALUES WERE ADJUSTED FOR STARS PROCESSING SCRIPT THAT FILTERS FOR ALL STARS OVER MASS OF MASS OF 2.0 [SOLAR MASS]
        // Brightness limits apply independently of the
        // other optional stellar-property filters.
        switch (displayStage)
        {
            case 1:
                // Approximately 2% of stars.
                if (!IsFinite(magnitude) || magnitude > -1.5f)
                    return false;
                break;

            case 2:
                // Approximately 8% of stars.
                if (!IsFinite(magnitude) || magnitude > -0.2f)
                    return false;
                break;

            case 3:
                // Approximately 40% of stars.
                if (!IsFinite(magnitude) || magnitude > 0.85f)
                    return false;
                break;

                // Stage 4: all stars.
                // Stage 5: no stars, handled above.
        }

        // Keep the existing mass, age, evolution-stage,
        // and temperature filters working as before.
        if (!enabled)
            return true;

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

        settings = newSettings;
        UpdateClusterSpriteVisibility();

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
        // Cycle from most stars to fewest stars.
        switch (settings.displayStage)
        {
            case 4:
                settings.displayStage = 3; // 40%
                break;

            case 3:
                settings.displayStage = 2; // 8%
                break;

            case 2:
                settings.displayStage = 1; // 2%
                break;

            case 1:
                settings.displayStage = 5; // No stars
                break;

            case 5:
            default:
                settings.displayStage = 4; // All stars
                break;
        }

        UpdateClusterSpriteVisibility();

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
        if (settings.displayStage != 4)
        {
            settings.displayStage = 4;

            unchecked
            {
                revision++;
            }
        }

        Debug.Log("GaiaStarFilter: Starting at Stage 4/5 - All stars");
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
