using UnityEngine;

[DisallowMultipleComponent]
[DefaultExecutionOrder(100)]
public class MarkerDistanceScaling : MonoBehaviour
{
    [Header("Camera")]
    [Tooltip("XR headset camera. If empty, Camera.main is used.")]
    [SerializeField] private Camera targetCamera;

    [Header("Distance Compensation")]
    [Tooltip("Distance at which gradual enlargement begins, in world units.")]
    [Min(0.01f)]
    [SerializeField] private float compensationStartDistance = 25f;

    [Tooltip("0 = no distance growth, 1 = constant apparent width.")]
    [Range(0f, 1f)]
    [SerializeField] private float distanceExponent = 0.5f;

    [Header("Screen Size")]
    [Tooltip("Try to keep the marker at least this many pixels tall.")]
    [Min(1f)]
    [SerializeField] private float minimumScreenHeightPixels = 10f;

    [Tooltip("Maximum scale relative to the original local scale.")]
    [Min(1f)]
    [SerializeField] private float maxScale = 32f;

    private SpriteRenderer spriteRenderer;
    private Vector3 baseLocalScale;
    private float appliedScale = 1f;

    private static Camera cachedMainCamera;

    private void Start()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        baseLocalScale = transform.localScale;

        if (spriteRenderer == null || spriteRenderer.sprite == null)
        {
            Debug.LogWarning(
                "MarkerDistanceScaling requires a SpriteRenderer with a sprite.",
                this);

            enabled = false;
        }
    }

    private void LateUpdate()
    {
        Camera cameraToUse = GetCamera();

        if (cameraToUse == null || spriteRenderer == null)
            return;

        Vector3 markerPosition = transform.position;

        float distance = Vector3.Distance(
            cameraToUse.transform.position,
            markerPosition);

        // Gradual distance compensation.
        float distanceFactor = Mathf.Pow(
            Mathf.Max(1f, distance /
                Mathf.Max(compensationStartDistance, 0.01f)),
            distanceExponent);

        // Measure the current sprite height in screen pixels.
        // Divide out our previous scale multiplier to estimate its
        // apparent size at the original scale.
        float currentPixelHeight = GetScreenHeightPixels(cameraToUse);

        float basePixelHeight =
            currentPixelHeight / Mathf.Max(appliedScale, 0.0001f);

        // Calculate the scale needed to reach the minimum pixel height.
        float screenSizeFactor = 1f;

        if (basePixelHeight > 0.01f)
        {
            screenSizeFactor =
                minimumScreenHeightPixels / basePixelHeight;
        }

        // Use whichever compensation is stronger, within the scale cap.
        float desiredScale = Mathf.Clamp(
            Mathf.Max(1f, distanceFactor, screenSizeFactor),
            1f,
            Mathf.Max(1f, maxScale));

        if (!Mathf.Approximately(desiredScale, appliedScale))
        {
            transform.localScale = baseLocalScale * desiredScale;
            appliedScale = desiredScale;
        }
    }

    private float GetScreenHeightPixels(Camera cameraToUse)
    {
        Sprite sprite = spriteRenderer.sprite;
        Bounds bounds = sprite.bounds;

        Vector3 bottomLocal = new Vector3(
            bounds.center.x, bounds.min.y, bounds.center.z);

        Vector3 topLocal = new Vector3(
            bounds.center.x, bounds.max.y, bounds.center.z);

        Vector3 bottomScreen = cameraToUse.WorldToScreenPoint(
            transform.TransformPoint(bottomLocal));

        Vector3 topScreen = cameraToUse.WorldToScreenPoint(
            transform.TransformPoint(topLocal));

        if (bottomScreen.z <= 0f || topScreen.z <= 0f)
            return 0f;

        return Vector2.Distance(
            new Vector2(bottomScreen.x, bottomScreen.y),
            new Vector2(topScreen.x, topScreen.y));
    }

    private Camera GetCamera()
    {
        if (targetCamera != null)
            return targetCamera;

        if (cachedMainCamera == null ||
            !cachedMainCamera.isActiveAndEnabled)
        {
            cachedMainCamera = Camera.main;
        }

        return cachedMainCamera;
    }
}