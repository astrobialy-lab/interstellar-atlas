using System.Globalization;
using TMPro;
using UnityEngine;

public class SettingsMenu : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Canvas settingsCanvas;

    [Header("Stars Settings")]
    [SerializeField] private StarsLODLoader loader;
    [SerializeField] private TMP_InputField starsLevelInput;
    [SerializeField] private TMP_InputField maxConcurrentLoadsInput;

    [Header("Dust Settings")]
    [SerializeField] private DustSpriteRenderer dustSpriteRenderer;
    [SerializeField] private TMP_InputField maxDustSpritesInput;

    private bool wasCanvasActive;
    private bool initialized;

    private void Start()
    {
        if (loader == null)
            loader = FindAnyObjectByType<StarsLODLoader>();

        if (dustSpriteRenderer == null)
            dustSpriteRenderer = FindAnyObjectByType<DustSpriteRenderer>();

        if (settingsCanvas == null ||
            loader == null ||
            !loader.IsReady ||
            loader.AvailableLayerCount < 1 ||
            starsLevelInput == null ||
            maxConcurrentLoadsInput == null ||
            dustSpriteRenderer == null ||
            maxDustSpritesInput == null)
        {
            Debug.LogError(
                "SettingsMenu: A required Canvas, ready StarsLODLoader, " +
                "DustSpriteRenderer, or input field is missing."
            );

            enabled = false;
            return;
        }

        starsLevelInput.contentType =
            TMP_InputField.ContentType.IntegerNumber;

        starsLevelInput.onEndEdit.AddListener(
            OnStarsLevelInputEndEdit
        );

        maxConcurrentLoadsInput.contentType =
            TMP_InputField.ContentType.IntegerNumber;

        maxConcurrentLoadsInput.onEndEdit.AddListener(
            OnMaxConcurrentLoadsInputEndEdit
        );

        maxDustSpritesInput.contentType =
            TMP_InputField.ContentType.IntegerNumber;

        maxDustSpritesInput.onEndEdit.AddListener(
            OnMaxDustSpritesInputEndEdit
        );

        RefreshInputs();

        wasCanvasActive =
            settingsCanvas.gameObject.activeInHierarchy;

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
        if (starsLevelInput != null)
        {
            starsLevelInput.onEndEdit.RemoveListener(
                OnStarsLevelInputEndEdit
            );
        }

        if (maxConcurrentLoadsInput != null)
        {
            maxConcurrentLoadsInput.onEndEdit.RemoveListener(
                OnMaxConcurrentLoadsInputEndEdit
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

    // ========================================================
    // APPLY ALL SETTINGS
    // ========================================================

    private void ApplyInputsAndSave()
    {
        if (loader != null && starsLevelInput != null)
            ApplyStarsLevelInputAndSave();

        if (loader != null && maxConcurrentLoadsInput != null)
            ApplyMaxConcurrentLoadsInputAndSave();

        if (dustSpriteRenderer != null &&
            maxDustSpritesInput != null)
        {
            ApplyMaxDustSpritesInputAndSave();
        }
    }

    // ========================================================
    // STARS LEVEL
    // ========================================================

    private void OnStarsLevelInputEndEdit(string input)
    {
        ApplyStarsLevelInputAndSave();
    }

    private void ApplyStarsLevelInputAndSave()
    {
        if (loader == null || starsLevelInput == null)
            return;

        if (!int.TryParse(
                starsLevelInput.text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int level))
        {
            RefreshStarsLevelInput();
            return;
        }

        if (!loader.IsReady ||
            loader.AvailableLayerCount < 1)
        {
            RefreshStarsLevelInput();
            return;
        }

        level = Mathf.Clamp(
            level,
            0,
            loader.AvailableLayerCount - 1
        );

        if (!loader.SetLayerIndex(level))
        {
            RefreshStarsLevelInput();
            return;
        }

        loader.CommitLayerIndex();

        RefreshStarsLevelInput();
    }

    private void RefreshStarsLevelInput()
    {
        if (loader == null || starsLevelInput == null)
            return;

        starsLevelInput.SetTextWithoutNotify(
            loader.LayerIndex.ToString(
                CultureInfo.InvariantCulture
            )
        );
    }

    // ========================================================
    // CONCURRENT LOADS
    // ========================================================

    private void OnMaxConcurrentLoadsInputEndEdit(string input)
    {
        ApplyMaxConcurrentLoadsInputAndSave();
    }

    private void ApplyMaxConcurrentLoadsInputAndSave()
    {
        if (loader == null || maxConcurrentLoadsInput == null)
            return;

        if (!int.TryParse(
                maxConcurrentLoadsInput.text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int count))
        {
            RefreshMaxConcurrentLoadsInput();
            return;
        }

        count = Mathf.Clamp(
            count,
            1,
            StarsLODLoader.MaxAllowedConcurrentLoads
        );

        loader.SetMaxConcurrentLoads(count);
        loader.CommitMaxConcurrentLoads();

        RefreshMaxConcurrentLoadsInput();
    }

    private void RefreshMaxConcurrentLoadsInput()
    {
        if (loader == null || maxConcurrentLoadsInput == null)
            return;

        maxConcurrentLoadsInput.SetTextWithoutNotify(
            loader.MaxConcurrentLoads.ToString(
                CultureInfo.InvariantCulture
            )
        );
    }

    // ========================================================
    // DUST
    // ========================================================

    private void OnMaxDustSpritesInputEndEdit(string input)
    {
        ApplyMaxDustSpritesInputAndSave();
    }

    private void ApplyMaxDustSpritesInputAndSave()
    {
        if (dustSpriteRenderer == null ||
            maxDustSpritesInput == null)
        {
            return;
        }

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

    // ========================================================
    // REFRESH
    // ========================================================

    private void RefreshInputs()
    {
        RefreshStarsLevelInput();
        RefreshMaxConcurrentLoadsInput();
        RefreshDustSpritesInput();
    }
}