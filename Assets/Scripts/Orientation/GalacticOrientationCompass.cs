using System.Collections.Generic;
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

    // Galactic axes expressed in Unity world coordinates.
    // +Xgal = Unity +X
    // +Ygal = Unity +Z
    // +Zgal = Unity +Y
    private readonly Vector3[] axisDirections =
    {
        Vector3.right,
        Vector3.forward,
        Vector3.up
    };

    private GameObject compassAnchor;
    private RectTransform compassRect;

    // Retrieved automatically from the assigned Panel.
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

        // Convenience fallback. Assign the correct Panel explicitly
        // in the Inspector if your scene has multiple panels.
        if (uiPanel == null)
        {
            GameObject panelObject = GameObject.Find("Panel");

            if (panelObject != null)
                uiPanel = panelObject.GetComponent<RectTransform>();
        }

        // The Panel replaces the public Canvas reference.
        // We still need its parent Canvas for coordinate conversion.
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

        // IMPORTANT: parent the generated compass to the scaled Panel,
        // not directly to the Canvas.
        compassAnchor.transform.SetParent(uiPanel, false);

        compassRect = compassAnchor.GetComponent<RectTransform>();

        compassRect.anchorMin = new Vector2(0.5f, 0.5f);
        compassRect.anchorMax = new Vector2(0.5f, 0.5f);
        compassRect.pivot = new Vector2(0.5f, 0.5f);
        compassRect.sizeDelta = Vector2.zero;
        compassRect.localScale = Vector3.one;

        axisImages.Clear();
        axisRects.Clear();

        CreateAxis("Galactic X", galacticXColor);
        CreateAxis("Galactic Y", galacticYColor);
        CreateAxis("Galactic Z", galacticZColor);

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

    private void CreateAxis(string axisName, Color color)
    {
        GameObject axisObject = new GameObject(
            axisName,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image)
        );

        axisObject.transform.SetParent(compassAnchor.transform, false);

        RectTransform rect = axisObject.GetComponent<RectTransform>();

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

        // These coordinates are in Canvas units. Because the compass
        // is parented to the Panel, the Panel's scale affects the
        // rendered compass as a whole.
        compassRect.anchoredPosition = anchorCanvas;

        float scale = Mathf.Max(0f, compassScale);

        float worldAxisLength = Mathf.Max(0f, axisLength) * scale;
        float worldAxisWidth = Mathf.Max(0f, axisWidth) * scale;

        float canvasWidth = 1f;

        if (TryWorldToCanvasLocal(
            anchorWorld + viewerCamera.up * worldAxisWidth,
            out Vector2 widthPoint))
        {
            canvasWidth = Vector2.Distance(anchorCanvas, widthPoint);
        }

        canvasWidth = Mathf.Max(0.1f, canvasWidth);

        for (int i = 0; i < axisImages.Count; i++)
        {
            Image image = axisImages[i];
            RectTransform rect = axisRects[i];

            if (image == null || rect == null)
                continue;

            Vector3 endpointWorld =
                anchorWorld + axisDirections[i] * worldAxisLength;

            if (!TryWorldToCanvasLocal(
                endpointWorld, out Vector2 endpointCanvas))
            {
                image.enabled = false;
                continue;
            }

            image.enabled = true;

            Vector2 delta = endpointCanvas - anchorCanvas;

            float length = delta.magnitude;
            float angle =
                Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

            rect.anchoredPosition = delta * 0.5f;

            rect.sizeDelta = new Vector2(length, canvasWidth);

            rect.localRotation = Quaternion.Euler(0f, 0f, angle);
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