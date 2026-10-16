
using UnityEngine;

[System.Serializable]
public struct GaiaStarFilterSettings
{
    public bool enabled;

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
        enabled && HasAnyCriterion;

    public bool Matches(
        float massSolar,
        float ageGyr,
        int evolutionStage,
        float temperatureK)
    {
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
    [SerializeField]
    private GaiaStarFilterSettings settings =
        new GaiaStarFilterSettings
        {
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

        settings = newSettings;

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
}
