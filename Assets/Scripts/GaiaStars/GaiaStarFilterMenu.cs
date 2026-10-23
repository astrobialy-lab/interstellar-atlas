
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class GaiaStarFilterMenu : MonoBehaviour
{
    [Header("Filter")]
    public GaiaStarFilter starFilter;

    [Header("Brightness (apparent G magnitude)")]
    public Toggle brightnessToggle;
    public TMP_InputField maxMagnitudeInput;

    [Header("Mass (solar masses)")]
    public Toggle massToggle;
    public TMP_InputField minMassInput;
    public TMP_InputField maxMassInput;

    [Header("Age (Gyr)")]
    public Toggle ageToggle;
    public TMP_InputField minAgeInput;
    public TMP_InputField maxAgeInput;

    [Header("Evolution Stage")]
    public Toggle evolutionStageToggle;
    public TMP_InputField minEvolutionStageInput;
    public TMP_InputField maxEvolutionStageInput;

    [Header("Temperature (K)")]
    public Toggle temperatureToggle;
    public TMP_InputField minTemperatureInput;
    public TMP_InputField maxTemperatureInput;

    private void Start()
    {
        LoadCurrentSettings();
        gameObject.SetActive(false);
    }

    private void OnEnable()
    {
        if (starFilter != null)
        {
            starFilter.BrightnessFilterChanged +=
                HandleBrightnessFilterChanged;
        }
    }

    private void OnDisable()
    {
        if (starFilter != null)
        {
            starFilter.BrightnessFilterChanged -=
                HandleBrightnessFilterChanged;
        }
    }

    private void HandleBrightnessFilterChanged(
        bool enabled,
        float maxMagnitude)
    {
        if (brightnessToggle != null)
        {
            brightnessToggle.SetIsOnWithoutNotify(enabled);
        }

        if (maxMagnitudeInput != null)
        {
            maxMagnitudeInput.SetTextWithoutNotify(
                maxMagnitude.ToString(
                    CultureInfo.InvariantCulture));
        }
    }

    public bool IsOpen()
    {
        return gameObject.activeSelf;
    }

    public void OpenMenu()
    {
        LoadCurrentSettings();

        gameObject.SetActive(true);

        Canvas.ForceUpdateCanvases();
    }

    public void CloseMenu()
    {
        gameObject.SetActive(false);
    }

    public void ToggleMenu()
    {
        if (IsOpen())
            CloseMenu();
        else
            OpenMenu();
    }

    private void LoadCurrentSettings()
    {
        if (starFilter == null)
        {
            Debug.LogError(
                "GaiaStarFilterMenu: Star Filter is not assigned.");
            return;
        }

        GaiaStarFilterSettings settings = starFilter.CreateSnapshot();

        HandleBrightnessFilterChanged(settings.useMagnitudeLimit, settings.maxMagnitude);

        if (massToggle != null)
            massToggle.isOn = settings.useMassRange;

        if (minMassInput != null)
            minMassInput.text = settings.minMassSolar.ToString(
                CultureInfo.InvariantCulture);

        if (maxMassInput != null)
            maxMassInput.text = settings.maxMassSolar.ToString(
                CultureInfo.InvariantCulture);

        if (ageToggle != null)
            ageToggle.isOn = settings.useAgeRange;

        if (minAgeInput != null)
            minAgeInput.text = settings.minAgeGyr.ToString(
                CultureInfo.InvariantCulture);

        if (maxAgeInput != null)
            maxAgeInput.text = settings.maxAgeGyr.ToString(
                CultureInfo.InvariantCulture);

        if (evolutionStageToggle != null)
            evolutionStageToggle.isOn =
                settings.useEvolutionStageRange;

        if (minEvolutionStageInput != null)
            minEvolutionStageInput.text =
                settings.minEvolutionStage.ToString(
                    CultureInfo.InvariantCulture);

        if (maxEvolutionStageInput != null)
            maxEvolutionStageInput.text =
                settings.maxEvolutionStage.ToString(
                    CultureInfo.InvariantCulture);

        if (temperatureToggle != null)
            temperatureToggle.isOn =
                settings.useTemperatureRange;

        if (minTemperatureInput != null)
            minTemperatureInput.text =
                settings.minTemperatureK.ToString(
                    CultureInfo.InvariantCulture);

        if (maxTemperatureInput != null)
            maxTemperatureInput.text =
                settings.maxTemperatureK.ToString(
                    CultureInfo.InvariantCulture);
    }

    public void ApplyFilter()
    {
        if (starFilter == null)
        {
            Debug.LogError(
                "GaiaStarFilterMenu: Star Filter is not assigned.");
            return;
        }

        GaiaStarFilterSettings settings =
            starFilter.CreateSnapshot();

        if (massToggle != null)
            settings.useMassRange = massToggle.isOn;

        settings.minMassSolar =
            ParseFloat(minMassInput, settings.minMassSolar);

        settings.maxMassSolar =
            ParseFloat(maxMassInput, settings.maxMassSolar);

        if (ageToggle != null)
            settings.useAgeRange = ageToggle.isOn;

        settings.minAgeGyr =
            ParseFloat(minAgeInput, settings.minAgeGyr);

        settings.maxAgeGyr =
            ParseFloat(maxAgeInput, settings.maxAgeGyr);

        if (evolutionStageToggle != null)
            settings.useEvolutionStageRange =
                evolutionStageToggle.isOn;

        settings.minEvolutionStage =
            ParseInt(
                minEvolutionStageInput,
                settings.minEvolutionStage);

        settings.maxEvolutionStage =
            ParseInt(
                maxEvolutionStageInput,
                settings.maxEvolutionStage);

        if (temperatureToggle != null)
            settings.useTemperatureRange =
                temperatureToggle.isOn;

        settings.minTemperatureK =
            ParseFloat(
                minTemperatureInput,
                settings.minTemperatureK);

        settings.maxTemperatureK =
            ParseFloat(
                maxTemperatureInput,
                settings.maxTemperatureK);

        if (brightnessToggle != null)
        {
            settings.useMagnitudeLimit =
                brightnessToggle.isOn;
        }

        settings.maxMagnitude = ParseFloat(
            maxMagnitudeInput,
            settings.maxMagnitude);

        starFilter.ApplySettings(settings);
    }

    public void CancelChanges()
    {
        CloseMenu();
    }

    private static float ParseFloat(
        TMP_InputField input,
        float fallback)
    {
        if (input == null)
            return fallback;

        if (float.TryParse(
            input.text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float value))
        {
            return value;
        }

        return fallback;
    }

    private static int ParseInt(
        TMP_InputField input,
        int fallback)
    {
        if (input == null)
            return fallback;

        if (int.TryParse(
            input.text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value))
        {
            return value;
        }

        return fallback;
    }
}
