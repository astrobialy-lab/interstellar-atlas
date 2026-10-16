using System.Globalization;
using TMPro;
using UnityEngine;

public class SettingsMenu : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Canvas settingsCanvas;
    [SerializeField] private GaiaMSLODLoader loader;
    [SerializeField] private TMP_InputField thresholdInput;

    [Header("Solid Angle Range (steradians)")]
    [SerializeField] private float minThreshold = 2.4e-5f;
    [SerializeField] private float maxThreshold = 10f;

    private bool wasCanvasEnabled;
    private bool initialized;

    private void Start()
    {
        if (loader == null)
            loader = FindAnyObjectByType<GaiaMSLODLoader>();

        if (settingsCanvas == null ||
            loader == null ||
            thresholdInput == null)
        {
            Debug.LogError(
                "SettingsMenu: Canvas, Loader, or Input Field is missing."
            );

            enabled = false;
            return;
        }

        minThreshold = Mathf.Max(minThreshold, 1e-8f);
        maxThreshold = Mathf.Max(
            maxThreshold,
            minThreshold * 1.001f
        );

        // Keep the saved value within the allowed range.
        float threshold = Mathf.Clamp(
            loader.SolidAngleThreshold,
            minThreshold,
            maxThreshold
        );

        if (threshold != loader.SolidAngleThreshold)
        {
            loader.SetSolidAngleThreshold(threshold);
            loader.CommitSolidAngleThreshold();
        }

        thresholdInput.onEndEdit.AddListener(
            OnThresholdInputEndEdit
        );

        RefreshInput();

        wasCanvasEnabled = settingsCanvas.enabled;
        initialized = true;
    }

    private void Update()
    {
        if (!initialized)
            return;

        bool canvasEnabled = settingsCanvas.enabled;

        if (canvasEnabled == wasCanvasEnabled)
            return;

        wasCanvasEnabled = canvasEnabled;

        if (canvasEnabled)
        {
            // Menu opened: show the current setting.
            RefreshInput();
        }
        else
        {
            // Menu closed: apply and save the input.
            ApplyInputAndSave();
        }
    }

    private void OnDestroy()
    {
        if (thresholdInput != null)
        {
            thresholdInput.onEndEdit.RemoveListener(
                OnThresholdInputEndEdit
            );
        }

        if (initialized && loader != null)
            ApplyInputAndSave();
    }

    private void OnDisable()
    {
        if (initialized && loader != null)
            ApplyInputAndSave();
    }

    private void OnThresholdInputEndEdit(string input)
    {
        ApplyInputAndSave();
    }

    private void ApplyInputAndSave()
    {
        if (!TryParseThreshold(thresholdInput.text, out float threshold))
        {
            // Invalid input: restore the previous valid value.
            RefreshInput();
            loader.CommitSolidAngleThreshold();
            return;
        }

        threshold = Mathf.Clamp(
            threshold,
            minThreshold,
            maxThreshold
        );

        loader.SetSolidAngleThreshold(threshold);
        loader.CommitSolidAngleThreshold();

        RefreshInput();
    }

    private bool TryParseThreshold(string input, out float threshold)
    {
        bool valid = float.TryParse(
            input,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out threshold
        );

        return valid &&
               !float.IsNaN(threshold) &&
               !float.IsInfinity(threshold);
    }

    private void RefreshInput()
    {
        float threshold = Mathf.Clamp(
            loader.SolidAngleThreshold,
            minThreshold,
            maxThreshold
        );

        thresholdInput.SetTextWithoutNotify(
            threshold.ToString(
                "G6",
                CultureInfo.InvariantCulture
            )
        );
    }
}