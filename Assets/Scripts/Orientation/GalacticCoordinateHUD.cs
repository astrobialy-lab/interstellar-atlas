using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

public class GalacticCoordinateHUD : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Usually XR Origin / Camera Offset / Main Camera.")]
    public Transform viewerCamera;

    [Tooltip("Assign the scaled Panel containing your UI.")]
    public RectTransform uiPanel;

    [Header("HUD Placement")]
    [Tooltip("Horizontal viewport position. 0 = left, 1 = right.")]
    [Range(0f, 1f)]
    public float viewportX = 0.82f;

    [Tooltip("Vertical viewport position. 0 = bottom, 1 = top.")]
    [Range(0f, 1f)]
    public float viewportY = 0.13f;

    [Tooltip("Distance in front of the camera, used by World Space canvases.")]
    public float distanceFromCamera = 1.2f;

    [Tooltip("Rotation offset, used by World Space canvases.")]
    public Vector3 localEulerAngles = Vector3.zero;

    [Tooltip("Text box size in canvas UI units.")]
    public Vector2 textBoxSize = new Vector2(850f, 150f);

    [Tooltip("UI font size.")]
    public float fontSize = 24f;

    public Color textColor = Color.white;

    [Header("Rendering")]
    [Tooltip("Try to render the text over scene geometry, including dust.")]
    public bool alwaysDrawOnTop = true;

    private TextMeshProUGUI coordinateText;
    private RectTransform textRect;
    private Material overlayMaterial;

    private Canvas uiCanvas;
    private Camera viewerUnityCamera;
    private bool warnedMissingPanel;

    private void Awake()
    {
        ResolveReferences();
        CreateHUD();
    }

    private void OnEnable()
    {
        if (coordinateText != null)
            coordinateText.gameObject.SetActive(true);
    }

    private void OnDisable()
    {
        if (coordinateText != null)
            coordinateText.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (coordinateText != null)
            Destroy(coordinateText.gameObject);

        if (overlayMaterial != null)
            Destroy(overlayMaterial);
    }

    private void Update()
    {
        if (viewerCamera == null ||
            viewerUnityCamera == null ||
            uiPanel == null ||
            uiCanvas == null ||
            coordinateText == null)
        {
            ResolveReferences();

            if (uiPanel != null &&
                uiCanvas != null &&
                coordinateText == null)
            {
                CreateHUD();
            }

            if (viewerCamera == null ||
                viewerUnityCamera == null ||
                uiPanel == null ||
                uiCanvas == null ||
                coordinateText == null)
            {
                return;
            }
        }

        // Viewer position in Galactic Cartesian coordinates.
        Vector3 galacticPosition =
            UnityToGalactic(viewerCamera.position);

        // Viewing direction in Galactic coordinates.
        Vector3 galacticForward =
            UnityToGalactic(viewerCamera.forward).normalized;

        float longitude = Mathf.Atan2(
            galacticForward.y,
            galacticForward.x
        ) * Mathf.Rad2Deg;

        if (longitude < 0f)
            longitude += 360f;

        float latitude = Mathf.Asin(
            Mathf.Clamp(galacticForward.z, -1f, 1f)
        ) * Mathf.Rad2Deg;

        // Viewer position expressed in Galactic spherical coordinates.
        float radialDistance = galacticPosition.magnitude;

        float positionLongitude = Mathf.Atan2(
            galacticPosition.y,
            galacticPosition.x
        ) * Mathf.Rad2Deg;

        if (positionLongitude < 0f)
            positionLongitude += 360f;

        float positionLatitude = 0f;

        if (radialDistance > 0.0001f)
        {
            positionLatitude = Mathf.Asin(
                Mathf.Clamp(
                    galacticPosition.z / radialDistance,
                    -1f,
                    1f
                )
            ) * Mathf.Rad2Deg;
        }

        coordinateText.text =
            $"View: l={longitude:000.0} deg, " +
            $"b={latitude:+00.0;-00.0;0.0} deg\n" +

            $"Position: X={FormatPc(galacticPosition.x)} pc, " +
            $"Y={FormatPc(galacticPosition.y)} pc, " +
            $"Z={FormatPc(galacticPosition.z)} pc\n" +

            $"l={positionLongitude:000.0} deg, " +
            $"b={positionLatitude:+00.0;-00.0;0.0} deg, " +
            $"d={radialDistance.ToString("#,0", CultureInfo.InvariantCulture)} pc";

        UpdateHUDPosition();
    }

    private static string FormatPc(float value)
    {
        return value.ToString(
            "+#,0;-#,0;0",
            CultureInfo.InvariantCulture
        );
    }

    private void ResolveReferences()
    {
        if (viewerCamera == null && Camera.main != null)
            viewerCamera = Camera.main.transform;

        if (viewerCamera != null &&
            viewerUnityCamera == null)
        {
            viewerUnityCamera =
                viewerCamera.GetComponent<Camera>();
        }

        // Find the Panel if one was not assigned in the Inspector.
        if (uiPanel == null)
        {
            GameObject panelObject = GameObject.Find("Panel");

            if (panelObject != null)
                uiPanel = panelObject.GetComponent<RectTransform>();
        }

        // Find the Canvas that contains the assigned Panel.
        uiCanvas = uiPanel != null
            ? uiPanel.GetComponentInParent<Canvas>()
            : null;
    }

    private void CreateHUD()
    {
        if (uiPanel == null ||
            uiCanvas == null ||
            coordinateText != null)
        {
            if ((uiPanel == null || uiCanvas == null) &&
                !warnedMissingPanel)
            {
                Debug.LogWarning(
                    "GalacticCoordinateHUD: Assign the Panel " +
                    "under your UI Canvas in the Inspector."
                );

                warnedMissingPanel = true;
            }

            return;
        }

        warnedMissingPanel = false;

        GameObject hudObject = new GameObject(
            "Galactic Coordinate HUD",
            typeof(RectTransform),
            typeof(TextMeshProUGUI)
        );

        // Parent to the scaled Panel instead of the Canvas.
        hudObject.transform.SetParent(uiPanel, false);

        coordinateText = hudObject.GetComponent<TextMeshProUGUI>();
        textRect = coordinateText.rectTransform;

        coordinateText.fontSize = fontSize;
        coordinateText.color = textColor;
        coordinateText.alignment = TextAlignmentOptions.Center;
        coordinateText.raycastTarget = false;
        coordinateText.text = "Galactic coordinates loading...";

        textRect.sizeDelta = textBoxSize;

        ConfigureOverlayRendering();
        UpdateHUDPosition();
    }

    private void UpdateHUDPosition()
    {
        if (textRect == null ||
            uiPanel == null ||
            uiCanvas == null ||
            viewerCamera == null ||
            viewerUnityCamera == null)
        {
            return;
        }

        textRect.sizeDelta = textBoxSize;

        if (uiCanvas.renderMode == RenderMode.WorldSpace)
        {
            // Position the text at a point in front of the VR camera.
            Vector3 worldPosition =
                viewerUnityCamera.ViewportToWorldPoint(
                    new Vector3(
                        viewportX,
                        viewportY,
                        distanceFromCamera
                    )
                );

            textRect.position = worldPosition;

            textRect.rotation =
                viewerCamera.rotation *
                Quaternion.Euler(localEulerAngles);
        }
        else
        {
            // Anchor relative to the Panel, not the Canvas.
            Vector2 anchor = new Vector2(viewportX, viewportY);

            textRect.anchorMin = anchor;
            textRect.anchorMax = anchor;
            textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.anchoredPosition = Vector2.zero;
        }
    }

    private void ConfigureOverlayRendering()
    {
        if (!alwaysDrawOnTop || coordinateText == null)
            return;

        Material sourceMaterial =
            coordinateText.fontSharedMaterial;

        if (sourceMaterial == null)
        {
            Debug.LogWarning(
                "The Galactic HUD has no TextMeshPro material."
            );

            return;
        }

        // Copy the material so the shared TMP font material is unchanged.
        overlayMaterial = new Material(sourceMaterial);
        overlayMaterial.renderQueue = 5000;

        if (overlayMaterial.HasProperty("_ZTestMode"))
        {
            overlayMaterial.SetFloat(
                "_ZTestMode",
                (float)CompareFunction.Always
            );
        }

        if (overlayMaterial.HasProperty("_ZTest"))
        {
            overlayMaterial.SetFloat(
                "_ZTest",
                (float)CompareFunction.Always
            );
        }

        if (overlayMaterial.HasProperty("unity_GUIZTestMode"))
        {
            overlayMaterial.SetFloat(
                "unity_GUIZTestMode",
                (float)CompareFunction.Always
            );
        }

        if (overlayMaterial.HasProperty("_ZWrite"))
            overlayMaterial.SetFloat("_ZWrite", 0f);

        coordinateText.fontSharedMaterial = overlayMaterial;
    }

    private Vector3 UnityToGalactic(Vector3 unityVector)
    {
        // Standard Galactic axes:
        // +X = Galactic center
        // +Y = Galactic rotation direction
        // +Z = Galactic north
        return new Vector3(
            unityVector.x,
            -unityVector.z,
            unityVector.y
        );
    }
}