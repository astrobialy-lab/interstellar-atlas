using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ClusterFilterMenu : MonoBehaviour
{
    [Header("Filter")]
    public ClusterFilter clusterFilter;

    [Header("General")]
    public Toggle filterEnabledToggle;

    [Header("Membership Probability")]
    public Toggle membershipToggle;
    public TMP_InputField membershipInput;

    [Header("Age")]
    public Toggle ageToggle;
    public TMP_InputField minAgeInput;
    public TMP_InputField maxAgeInput;

    [Header("Total Mass")]
    public Toggle totalMassToggle;
    public TMP_InputField minMassInput;
    public TMP_InputField maxMassInput;

    [Header("Cluster Type")]
    public Toggle typeCodeToggle;
    public TMP_InputField typeCodeInput;

    [Header("Distance")]
    public Toggle distanceToggle;
    public TMP_InputField minDistanceInput;
    public TMP_InputField maxDistanceInput;

    [Header("Cluster Location")]
    public Toggle requireRjToggle;
    public Toggle requireRtToggle;

    private void Start()
    {
        gameObject.SetActive(false);
        LoadCurrentSettings();
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
        {
            CloseMenu();
        }
        else
        {
            OpenMenu();
        }
    }

    /*
     * Loads the actual settings currently stored
     * in GaiaClusterFilter into the UI.
     */
    private void LoadCurrentSettings()
    {
        if (clusterFilter == null)
        {
            Debug.LogError(
                "ClusterFilterMenu: " +
                "Cluster Filter is not assigned."
            );

            return;
        }

        GaiaClusterFilterSettings settings =
            clusterFilter.CreateSnapshot();

        if (filterEnabledToggle != null)
            filterEnabledToggle.isOn = settings.enabled;

        if (membershipToggle != null)
            membershipToggle.isOn = settings.useMembershipProbability;

        if (membershipInput != null)
            membershipInput.text = settings.minMembershipProbability.ToString(
                CultureInfo.InvariantCulture
            );

        if (ageToggle != null)
            ageToggle.isOn = settings.useAgeRange;

        if (minAgeInput != null)
            minAgeInput.text = settings.minLogAge.ToString(
                CultureInfo.InvariantCulture
            );

        if (maxAgeInput != null)
            maxAgeInput.text = settings.maxLogAge.ToString(
                CultureInfo.InvariantCulture
            );

        if (distanceToggle != null)
            distanceToggle.isOn = settings.useDistanceRange;

        if (minDistanceInput != null)
            minDistanceInput.text = settings.minDistancePc.ToString(
                CultureInfo.InvariantCulture
            );

        if (maxDistanceInput != null)
            maxDistanceInput.text = settings.maxDistancePc.ToString(
                CultureInfo.InvariantCulture
            );

        if (totalMassToggle != null)
            totalMassToggle.isOn = settings.useTotalMassRange;

        if (minMassInput != null)
            minMassInput.text = settings.minTotalMass.ToString(
                CultureInfo.InvariantCulture
            );

        if (maxMassInput != null)
            maxMassInput.text = settings.maxTotalMass.ToString(
                CultureInfo.InvariantCulture
            );

        if (requireRjToggle != null)
            requireRjToggle.isOn = settings.requireInRj;

        if (requireRtToggle != null)
            requireRtToggle.isOn = settings.requireInRt;

        if (typeCodeToggle != null)
            typeCodeToggle.isOn = settings.useTypeCode;

        if (typeCodeInput != null)
            typeCodeInput.text = settings.typeCode.ToString(
                CultureInfo.InvariantCulture
            );
    }

    /*
     * Applies the values currently shown in the UI
     * to GaiaClusterFilter.
     */
    public void ApplyFilter()
    {
        if (clusterFilter == null)
        {
            Debug.LogError(
                "ClusterFilterMenu: " +
                "Cluster Filter is not assigned."
            );

            return;
        }

        GaiaClusterFilterSettings settings =
            clusterFilter.CreateSnapshot();

        settings.enabled =
            filterEnabledToggle != null &&
            filterEnabledToggle.isOn;

        settings.useMembershipProbability =
            membershipToggle != null &&
            membershipToggle.isOn;

        settings.minMembershipProbability =
            ParseFloat(membershipInput, settings.minMembershipProbability);

        settings.useAgeRange =
            ageToggle != null &&
            ageToggle.isOn;

        settings.minLogAge =
            ParseFloat(minAgeInput, settings.minLogAge);

        settings.maxLogAge =
            ParseFloat(maxAgeInput, settings.maxLogAge);

        settings.useDistanceRange =
            distanceToggle != null &&
            distanceToggle.isOn;

        settings.minDistancePc =
            ParseFloat(minDistanceInput, settings.minDistancePc);

        settings.maxDistancePc =
            ParseFloat(maxDistanceInput, settings.maxDistancePc);

        settings.useTotalMassRange =
            totalMassToggle != null &&
            totalMassToggle.isOn;

        settings.minTotalMass =
            ParseFloat(minMassInput, settings.minTotalMass);

        settings.maxTotalMass =
            ParseFloat(maxMassInput, settings.maxTotalMass);

        settings.requireInRj =
            requireRjToggle != null &&
            requireRjToggle.isOn;

        settings.requireInRt =
            requireRtToggle != null &&
            requireRtToggle.isOn;

        settings.useTypeCode =
            typeCodeToggle != null &&
            typeCodeToggle.isOn;

        int typeCode = ParseInt(typeCodeInput, settings.typeCode);

        settings.typeCode = (byte)Mathf.Clamp(typeCode, 0, 255);

        clusterFilter.ApplySettings(settings);
    }

    /*
     * Closes the menu without changing
     * the actual filter settings.
     */
    public void CancelChanges()
    {
        CloseMenu();
    }

    private float ParseFloat(TMP_InputField input, float fallback)
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

    private int ParseInt(TMP_InputField input, int fallback)
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