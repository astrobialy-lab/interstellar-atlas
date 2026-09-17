using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

public class GalacticCoordinateHUD : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Usually XR Origin / Camera Offset / Main Camera.")]
    public Transform viewerCamera;

    [Tooltip("Sun-centered origin of the Galactic coordinate system.")]
    public Transform coordinateOrigin;

    [Header("Scale")]
    public float parsecsPerUnityUnit = 50.0f;

    [Header("HUD Placement")]
    public Vector3 localPosition = new Vector3(0f, -0.34f, 1.2f);
    public Vector3 localEulerAngles = Vector3.zero;
    public Vector3 localScale = Vector3.one * 0.14f;
    public Vector2 textBoxSize = new Vector2(10.0f, 2.5f);
    public float fontSize = 3.2f;
    public Color textColor = Color.white;

    [Header("Overlay Rendering")]
    [Tooltip("Draw the HUD over dust and other scene geometry.")]
    public bool alwaysDrawOnTop = true;

    private TextMeshPro coordinateText;
    private GameObject hudObject;
    private Material overlayMaterial;

    private void Awake()
    {
        ResolveReferences();
        CreateHUD();
    }

    private void OnEnable()
    {
        if (hudObject != null)
        {
            hudObject.SetActive(true);
        }
    }

    private void OnDisable()
    {
        if (hudObject != null)
        {
            hudObject.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (hudObject != null)
        {
            Destroy(hudObject);
        }

        if (overlayMaterial != null)
        {
            Destroy(overlayMaterial);
        }
    }

    private void Update()
    {
        if (viewerCamera == null || coordinateText == null)
        {
            ResolveReferences();
            CreateHUD();

            if (viewerCamera == null || coordinateText == null)
            {
                return;
            }
        }

        Vector3 originPosition = coordinateOrigin != null
            ? coordinateOrigin.position
            : Vector3.zero;

        Vector3 unityPosition =
            viewerCamera.position - originPosition;

        Vector3 galacticPosition = UnityToGalactic(unityPosition)
            * parsecsPerUnityUnit;

        Vector3 galacticForward =
            UnityToGalactic(viewerCamera.forward).normalized;

        float longitude = Mathf.Atan2(
            galacticForward.y,
            galacticForward.x
        ) * Mathf.Rad2Deg;

        if (longitude < 0f)
        {
            longitude += 360f;
        }

        float latitude = Mathf.Asin(
            Mathf.Clamp(galacticForward.z, -1f, 1f)
        ) * Mathf.Rad2Deg;

        float radialDistance = galacticPosition.magnitude;

        coordinateText.text =
            $"View:  l={longitude:000.0} deg   " +
            $"b={latitude:+00.0;-00.0;0.0} deg   " +
            $"d={radialDistance:0} pc\n" +
            $"Position:  X={galacticPosition.x:+0;-0;0} pc   " +
            $"Y={galacticPosition.y:+0;-0;0} pc   " +
            $"Z={galacticPosition.z:+0;-0;0} pc";
    }

    private void ResolveReferences()
    {
        if (viewerCamera == null && Camera.main != null)
        {
            viewerCamera = Camera.main.transform;
        }

        if (coordinateOrigin == null)
        {
            coordinateOrigin = transform;
        }
    }

    private void CreateHUD()
    {
        if (viewerCamera == null || hudObject != null)
        {
            return;
        }

        hudObject = new GameObject("Galactic Coordinate HUD");
        hudObject.transform.SetParent(viewerCamera, false);
        hudObject.transform.localPosition = localPosition;
        hudObject.transform.localEulerAngles = localEulerAngles;
        hudObject.transform.localScale = localScale;

        coordinateText = hudObject.AddComponent<TextMeshPro>();
        coordinateText.fontSize = fontSize;
        coordinateText.color = textColor;
        coordinateText.alignment = TextAlignmentOptions.Center;
        coordinateText.rectTransform.sizeDelta = textBoxSize;
        coordinateText.text = "Galactic coordinates loading...";

        ConfigureOverlayRendering();
    }

    private void ConfigureOverlayRendering()
    {
        Renderer textRenderer =
            coordinateText.GetComponent<Renderer>();

        if (textRenderer != null)
        {
            textRenderer.sortingOrder = short.MaxValue;
        }

        if (!alwaysDrawOnTop)
        {
            return;
        }

        Material sourceMaterial =
            coordinateText.fontSharedMaterial;

        if (sourceMaterial == null)
        {
            Debug.LogWarning(
                "The Galactic HUD has no TextMeshPro material."
            );
            return;
        }

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
        {
            overlayMaterial.SetFloat("_ZWrite", 0f);
        }

        coordinateText.fontSharedMaterial = overlayMaterial;
    }

    private Vector3 UnityToGalactic(Vector3 unityVector)
    {
        // Xgal = Unity X, Ygal = Unity Z, Zgal = Unity Y.
        return new Vector3(
            unityVector.x,
            unityVector.z,
            unityVector.y
        );
    }
}