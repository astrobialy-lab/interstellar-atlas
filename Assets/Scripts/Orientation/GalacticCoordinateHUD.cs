using System;
using TMPro;
using UnityEditor.PackageManager.UI;
using UnityEngine;
using UnityEngine.Rendering;

public class GalacticCoordinateHUD : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Usually XR Origin / Camera Offset / Main Camera.")]
    public Transform viewerCamera;

    [Header("Scale")]
    public float parsecsPerUnityUnit = 50.0f;

    [Header("HUD Placement")]
    [Tooltip("Horizontal viewport position. 0 = left, 1 = right.")]
    [Range(0f, 1f)]
    public float viewportX = 0.82f;

    [Tooltip("Vertical viewport position. 0 = bottom, 1 = top.")]
    [Range(0f, 1f)]
    public float viewportY = 0.13f;

    [Tooltip("Distance of the HUD in front of the camera.")]
    public float distanceFromCamera = 1.2f;

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
    private Camera viewerUnityCamera;

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
        if (
            viewerCamera == null ||
            coordinateText == null ||
            viewerUnityCamera == null
        )
        {
            ResolveReferences();
            CreateHUD();

            if (
                viewerCamera == null ||
                coordinateText == null ||
                viewerUnityCamera == null
            )
            {
                return;
            }
        }

        // Unity world origin = Galactic coordinate origin (Sun).
        Vector3 galacticPosition =
            UnityToGalactic(viewerCamera.position)
            * parsecsPerUnityUnit;

        // Viewing direction in Galactic coordinates.
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

        // Position expressed in Galactic spherical coordinates.
        float radialDistance = galacticPosition.magnitude;

        float positionLongitude = Mathf.Atan2(
            galacticPosition.y,
            galacticPosition.x
        ) * Mathf.Rad2Deg;

        if (positionLongitude < 0f)
        {
            positionLongitude += 360f;
        }

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
            $"View:      l={longitude:000.0} deg   " +
            $"b={latitude:+00.0;-00.0;0.0} deg\n" +
            $"Position:  X={galacticPosition.x:+0;-0;0} pc   " +
            $"Y={galacticPosition.y:+0;-0;0} pc   " +
            $"Z={galacticPosition.z:+0;-0;0} pc\n" +
            $"           l={positionLongitude:000.0} deg   " +
            $"b={positionLatitude:+00.0;-00.0;0.0} deg   " +
            $"d={radialDistance:0} pc";

        UpdateHUDPosition();
    }

    private void ResolveReferences()
    {
        if (viewerCamera == null && Camera.main != null)
        {
            viewerCamera = Camera.main.transform;
        }

        if (viewerUnityCamera == null && viewerCamera != null)
        {
            viewerUnityCamera =
                viewerCamera.GetComponent<Camera>();
        }
    }

    private void UpdateHUDPosition()
    {
        if (
            hudObject == null ||
            viewerUnityCamera == null
        )
        {
            return;
        }

        Vector3 viewportPosition =
            new Vector3(
                viewportX,
                viewportY,
                distanceFromCamera
            );

        Vector3 worldPosition =
            viewerUnityCamera.ViewportToWorldPoint(
                viewportPosition
            );

        hudObject.transform.position = worldPosition;

        hudObject.transform.rotation =
            viewerCamera.rotation *
            Quaternion.Euler(localEulerAngles);

        // Use the Inspector-controlled localScale value.
        hudObject.transform.localScale = localScale;
    }

    private void CreateHUD()
    {
        if (viewerCamera == null || hudObject != null)
        {
            return;
        }

        hudObject =
            new GameObject("Galactic Coordinate HUD");

        hudObject.transform.SetParent(
            viewerCamera,
            true
        );

        coordinateText =
            hudObject.AddComponent<TextMeshPro>();

        coordinateText.fontSize = fontSize;
        coordinateText.color = textColor;
        coordinateText.alignment =
            TextAlignmentOptions.Center;

        coordinateText.rectTransform.sizeDelta =
            textBoxSize;

        coordinateText.text =
            "Galactic coordinates loading...";

        ConfigureOverlayRendering();
        UpdateHUDPosition();
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

        overlayMaterial =
            new Material(sourceMaterial);

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

        if (
            overlayMaterial.HasProperty(
                "unity_GUIZTestMode"
            )
        )
        {
            overlayMaterial.SetFloat(
                "unity_GUIZTestMode",
                (float)CompareFunction.Always
            );
        }

        if (overlayMaterial.HasProperty("_ZWrite"))
        {
            overlayMaterial.SetFloat(
                "_ZWrite",
                0f
            );
        }

        coordinateText.fontSharedMaterial =
            overlayMaterial;
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