using System.Globalization;
using TMPro;
using UnityEngine;

public class SettingsMenu : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Canvas settingsCanvas;

    [Header("Dust Settings")]
    [SerializeField] private DustSpriteRenderer dustSpriteRenderer;
    [SerializeField] private TMP_InputField maxDustSpritesInput;

    [Header("Stars Settings")]
    [SerializeField] private GaiaMSLODLoader loader;
    [SerializeField] private TMP_InputField thresholdInput;
    [SerializeField] private float minThreshold = 2.4e-5f;
    [SerializeField] private float maxThreshold = 10f;

    private bool wasCanvasActive;
    private bool initialized;

    private void Start()
    {
        if (loader == null)
            loader = FindAnyObjectByType<GaiaMSLODLoader>();

        if (dustSpriteRenderer == null)
            dustSpriteRenderer = FindAnyObjectByType<DustSpriteRenderer>();

        if (settingsCanvas == null ||
            loader == null ||
            thresholdInput == null ||
            dustSpriteRenderer == null ||
            maxDustSpritesInput == null)
        {
            Debug.LogError(
                "SettingsMenu: A required Canvas, loader, or input field is missing."
            );

            enabled = false;
            return;
        }

        minThreshold = Mathf.Max(minThreshold, 1e-8f);
        maxThreshold = Mathf.Max(
            maxThreshold,
            minThreshold * 1.001f
        );

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

        maxDustSpritesInput.contentType =
            TMP_InputField.ContentType.IntegerNumber;

        thresholdInput.onEndEdit.AddListener(
            OnThresholdInputEndEdit
        );

        maxDustSpritesInput.onEndEdit.AddListener(
            OnMaxDustSpritesInputEndEdit
        );

        RefreshInputs();

        // The whole Canvas GameObject is activated/deactivated.
        wasCanvasActive = settingsCanvas.gameObject.activeInHierarchy;
        initialized = true;
    }

    private void Update()
    {
        if (!initialized)
            return;

        bool canvasActive =
            settingsCanvas.gameObject.activeInHierarchy;

        if (canvasActive == wasCanvasActive)
            return;

        wasCanvasActive = canvasActive;

        if (canvasActive)
        {
            RefreshInputs();
        }
        else
        {
            ApplyInputsAndSave();
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

        if (maxDustSpritesInput != null)
        {
            maxDustSpritesInput.onEndEdit.RemoveListener(
                OnMaxDustSpritesInputEndEdit
            );
        }

        if (initialized)
            ApplyInputsAndSave();
    }

    private void OnDisable()
    {
        if (initialized)
            ApplyInputsAndSave();
    }

    private void OnThresholdInputEndEdit(string input)
    {
        ApplyThresholdInputAndSave();
    }

    private void OnMaxDustSpritesInputEndEdit(string input)
    {
        ApplyMaxDustSpritesInputAndSave();
    }

    private void ApplyInputsAndSave()
    {
        if (loader != null && thresholdInput != null)
            ApplyThresholdInputAndSave();

        if (dustSpriteRenderer != null &&
            maxDustSpritesInput != null)
        {
            ApplyMaxDustSpritesInputAndSave();
        }
    }

    private void ApplyThresholdInputAndSave()
    {
        if (!TryParseThreshold(
                thresholdInput.text,
                out float threshold))
        {
            RefreshThresholdInput();
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

        RefreshThresholdInput();
    }

    private void ApplyMaxDustSpritesInputAndSave()
    {
        if (!int.TryParse(
                maxDustSpritesInput.text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int count))
        {
            RefreshDustSpritesInput();
            return;
        }

        count = Mathf.Clamp(
            count,
            1,
            DustSpriteRenderer.MaxAllowedSprites
        );

        dustSpriteRenderer.SetMaxSpritesToLoad(count);

        RefreshDustSpritesInput();
    }

    private bool TryParseThreshold(
        string input,
        out float threshold)
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

    private void RefreshInputs()
    {
        RefreshThresholdInput();
        RefreshDustSpritesInput();
    }

    private void RefreshThresholdInput()
    {
        if (loader == null || thresholdInput == null)
            return;

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

    private void RefreshDustSpritesInput()
    {
        if (dustSpriteRenderer == null ||
            maxDustSpritesInput == null)
        {
            return;
        }

        maxDustSpritesInput.SetTextWithoutNotify(
            dustSpriteRenderer.MaxSpritesToLoad.ToString(
                CultureInfo.InvariantCulture
            )
        );
    }
}