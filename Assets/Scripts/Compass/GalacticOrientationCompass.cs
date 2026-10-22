
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class GalacticOrientationCompass : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Usually XR Origin / Camera Offset / Main Camera.")]
    public Transform viewerCamera;

    [Tooltip("Assign the Panel that contains your UI and is scaled in VR.")]
    public RectTransform uiPanel;

    [Header("HUD Placement")]
    [Tooltip("Position relative to the viewer camera.")]
    public Vector3 localPosition = new Vector3(0f, 0f, 1.0f);

    [Tooltip("Overall size of the compass.")]
    public float compassScale = 0.3f;

    [Header("Axis Appearance")]
    public float axisLength = 0.16f;
    public float axisWidth = 0.012f;

    [Header("Axis Labels")]
    [Tooltip("Font size of the X, Y and Z labels.")]
    public float labelFontSize = 28f;

    [Tooltip("Distance of each label beyond its axis endpoint, in canvas units.")]
    public float labelOffset = 20f;

    [Tooltip("Width and height of each label's text box.")]
    public Vector2 labelBoxSize = new Vector2(60f, 50f);

    [Header("Axis Colors")]
    public Color galacticXColor =
        new Color(1.0f, 0.2f, 0.2f, 1.0f);

    public Color galacticYColor =
        new Color(0.2f, 1.0f, 0.3f, 1.0f);

    public Color galacticZColor =
        new Color(0.25f, 0.55f, 1.0f, 1.0f);

    private readonly List<Image> axisImages = new List<Image>();
    private readonly List<RectTransform> axisRects =
        new List<RectTransform>();

    private readonly List<TextMeshProUGUI> axisLabels =
        new List<TextMeshProUGUI>();

    private readonly List<RectTransform> labelRects =
        new List<RectTransform>();

    // Galactic simulation axes expressed in Unity world coordinates:
    // +Xgal = Unity +X
    // +Ygal = Unity -Z
    // +Zgal = Unity +Y
    private readonly Vector3[] axisDirections =
    {
        Vector3.right,
        Vector3.back,
        Vector3.up
    };

    private readonly string[] axisLabelTexts =
    {
        "X", "Y", "Z"
    };

    private GameObject compassAnchor;
    private RectTransform compassRect;

    private Canvas uiCanvas;
    private RectTransform canvasRect;
    private Camera viewerUnityCamera;

    private Texture2D whiteTexture;
    private Sprite whiteSprite;

    private bool isBuilding;

    private void Awake()
    {
        ResolveReferences();
        BuildCompass();
    }

    private void OnEnable()
    {
        if (compassAnchor != null)
            compassAnchor.SetActive(true);
    }

    private void OnDisable()
    {
        if (compassAnchor != null)
            compassAnchor.SetActive(false);
    }

    private void OnDestroy()
    {
        ClearCompass();

        if (whiteSprite != null)
            Destroy(whiteSprite);

        if (whiteTexture != null)
            Destroy(whiteTexture);
    }

    private void LateUpdate()
    {
        ResolveReferences();

        if (viewerCamera == null ||
            viewerUnityCamera == null ||
            uiPanel == null ||
            uiCanvas == null ||
            canvasRect == null)
        {
            return;
        }

        if (compassAnchor == null || compassRect == null)
            BuildCompass();

        if (compassAnchor == null || compassRect == null)
            return;

        UpdateCompass();
    }

    [ContextMenu("Rebuild Compass")]
    public void RebuildCompass()
    {
        ClearCompass();
        BuildCompass();
    }

    private void ResolveReferences()
    {
        if (viewerCamera == null && Camera.main != null)
            viewerCamera = Camera.main.transform;

        if (viewerCamera != null && viewerUnityCamera == null)
            viewerUnityCamera = viewerCamera.GetComponent<Camera>();

        if (uiPanel == null)
        {
            GameObject panelObject = GameObject.Find("Panel");

            if (panelObject != null)
                uiPanel = panelObject.GetComponent<RectTransform>();
        }

        if (uiPanel != null)
        {
            uiCanvas = uiPanel.GetComponentInParent<Canvas>();

            canvasRect = uiCanvas != null
                ? uiCanvas.transform as RectTransform
                : null;
        }
    }

    private void BuildCompass()
    {
        if (isBuilding || compassAnchor != null)
            return;

        ResolveReferences();

        if (viewerCamera == null ||
            viewerUnityCamera == null ||
            uiPanel == null ||
            uiCanvas == null ||
            canvasRect == null)
        {
            return;
        }

        isBuilding = true;

        CreateWhiteSprite();

        compassAnchor = new GameObject(
            "Galactic Orientation Compass HUD",
            typeof(RectTransform)
        );

        // Parent to the scaled Panel.
        compassAnchor.transform.SetParent(uiPanel, false);

        compassRect = compassAnchor.GetComponent<RectTransform>();

        compassRect.anchorMin = new Vector2(0.5f, 0.5f);
        compassRect.anchorMax = new Vector2(0.5f, 0.5f);
        compassRect.pivot = new Vector2(0.5f, 0.5f);
        compassRect.sizeDelta = Vector2.zero;
        compassRect.localScale = Vector3.one;

        axisImages.Clear();
        axisRects.Clear();
        axisLabels.Clear();
        labelRects.Clear();

        CreateAxis("Galactic X", "X", galacticXColor);
        CreateAxis("Galactic Y", "Y", galacticYColor);
        CreateAxis("Galactic Z", "Z", galacticZColor);

        isBuilding = false;

        UpdateCompass();
    }

    private void CreateWhiteSprite()
    {
        if (whiteSprite != null)
            return;

        whiteTexture = new Texture2D(
            1, 1, TextureFormat.RGBA32, false
        );

        whiteTexture.name = "Compass White Pixel";
        whiteTexture.SetPixel(0, 0, Color.white);
        whiteTexture.Apply();

        whiteSprite = Sprite.Create(
            whiteTexture,
            new Rect(0, 0, 1, 1),
            new Vector2(0.5f, 0.5f),
            1f
        );
    }

    private void CreateAxis(
        string axisName,
        string labelText,
        Color color)
    {
        // Create the axis line.
        GameObject axisObject = new GameObject(
            axisName,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image)
        );

        axisObject.transform.SetParent(compassAnchor.transform, false);

        RectTransform rect =
            axisObject.GetComponent<RectTransform>();

        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = Vector2.zero;

        Image image = axisObject.GetComponent<Image>();
        image.sprite = whiteSprite;
        image.color = color;
        image.raycastTarget = false;

        axisRects.Add(rect);
        axisImages.Add(image);

        // Create the matching X, Y or Z label.
        GameObject labelObject = new GameObject(
            axisName + " Label",
            typeof(RectTransform),
            typeof(TextMeshProUGUI)
        );

        labelObject.transform.SetParent(
            compassAnchor.transform, false
        );

        RectTransform labelRect =
            labelObject.GetComponent<RectTransform>();

        labelRect.anchorMin = new Vector2(0.5f, 0.5f);
        labelRect.anchorMax = new Vector2(0.5f, 0.5f);
        labelRect.pivot = new Vector2(0.5f, 0.5f);
        labelRect.sizeDelta = labelBoxSize;

        TextMeshProUGUI label =
            labelObject.GetComponent<TextMeshProUGUI>();

        label.text = labelText;
        label.fontSize = labelFontSize;
        label.fontStyle = FontStyles.Bold;
        label.color = color;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.raycastTarget = false;

        labelRects.Add(labelRect);
        axisLabels.Add(label);
    }

    private void UpdateCompass()
    {
        if (compassRect == null)
            return;

        Vector3 anchorWorld =
            viewerCamera.TransformPoint(localPosition);

        if (!TryWorldToCanvasLocal(
            anchorWorld, out Vector2 anchorCanvas))
        {
            compassAnchor.SetActive(false);
            return;
        }

        compassAnchor.SetActive(true);

        compassRect.anchoredPosition = anchorCanvas;

        float scale = Mathf.Max(0f, compassScale);

        float worldAxisLength =
            Mathf.Max(0f, axisLength) * scale;

        float worldAxisWidth =
            Mathf.Max(0f, axisWidth) * scale;

        float canvasWidth = 1f;

        if (TryWorldToCanvasLocal(
            anchorWorld + viewerCamera.up * worldAxisWidth,
            out Vector2 widthPoint))
        {
            canvasWidth =
                Vector2.Distance(anchorCanvas, widthPoint);
        }

        canvasWidth = Mathf.Max(0.1f, canvasWidth);

        for (int i = 0; i < axisImages.Count; i++)
        {
            Image image = axisImages[i];
            RectTransform rect = axisRects[i];

            TextMeshProUGUI label = axisLabels[i];
            RectTransform labelRect = labelRects[i];

            if (image == null || rect == null ||
                label == null || labelRect == null)
            {
                continue;
            }

            Vector3 endpointWorld =
                anchorWorld +
                axisDirections[i] * worldAxisLength;

            if (!TryWorldToCanvasLocal(
                endpointWorld, out Vector2 endpointCanvas))
            {
                image.enabled = false;
                label.enabled = false;
                continue;
            }

            image.enabled = true;
            label.enabled = true;

            // Position and rotate the axis line.
            Vector2 delta = endpointCanvas - anchorCanvas;

            float length = delta.magnitude;

            float angle =
                Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

            rect.anchoredPosition = delta * 0.5f;
            rect.sizeDelta = new Vector2(length, canvasWidth);
            rect.localRotation =
                Quaternion.Euler(0f, 0f, angle);

            // Put the label slightly beyond the positive axis endpoint.
            // The projected direction is used rather than Unity's
            // direction labels, so the labels follow the simulation axes.
            Vector2 projectedDirection =
                delta.sqrMagnitude > 0.0001f
                    ? delta.normalized
                    : Vector2.up;

            float offset = Mathf.Max(0f, labelOffset);

            labelRect.anchoredPosition =
                delta + projectedDirection * offset;

            // Keep the text upright as the viewer rotates.
            labelRect.localRotation = Quaternion.identity;

            labelRect.sizeDelta = labelBoxSize;
            label.fontSize = labelFontSize;
        }
    }

    private bool TryWorldToCanvasLocal(
        Vector3 worldPosition,
        out Vector2 canvasPosition)
    {
        canvasPosition = Vector2.zero;

        if (viewerUnityCamera == null ||
            uiCanvas == null ||
            canvasRect == null)
        {
            return false;
        }

        Vector3 screenPosition =
            viewerUnityCamera.WorldToScreenPoint(worldPosition);

        if (screenPosition.z <= 0f)
            return false;

        Camera canvasCamera =
            uiCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : (uiCanvas.worldCamera != null
                    ? uiCanvas.worldCamera
                    : viewerUnityCamera);

        return RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvasRect,
            new Vector2(screenPosition.x, screenPosition.y),
            canvasCamera,
            out canvasPosition
        );
    }

    private void ClearCompass()
    {
        axisImages.Clear();
        axisRects.Clear();
        axisLabels.Clear();
        labelRects.Clear();

        if (compassAnchor != null)
        {
            if (Application.isPlaying)
                Destroy(compassAnchor);
            else
                DestroyImmediate(compassAnchor);
        }

        compassAnchor = null;
        compassRect = null;
    }
}
