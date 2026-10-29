
using UnityEngine;

public class SkyboxDistanceFade : MonoBehaviour
{
    [Header("Camera")]
    [SerializeField] private Transform playerCamera;

    [Header("Distance From Center")]
    [SerializeField] private Vector3 center = Vector3.zero;
    [SerializeField] private float fadeStartDistance = 1000f;
    [SerializeField] private float fadeEndDistance = 1100f;

    private static readonly int TintProperty =
        Shader.PropertyToID("_Tint");

    private Material originalSkybox;
    private Material runtimeSkybox;
    private Color originalTint;

    private void Awake()
    {
        if (playerCamera == null)
        {
            Debug.LogError(
                "SkyboxDistanceFade: Assign the player camera.", this);
            enabled = false;
            return;
        }

        originalSkybox = RenderSettings.skybox;

        if (originalSkybox == null ||
            !originalSkybox.HasProperty(TintProperty))
        {
            Debug.LogError(
                "SkyboxDistanceFade requires a skybox material " +
                "with a _Tint property, such as Skybox/Cubemap.", this);
            enabled = false;
            return;
        }

        // Use a copy so the project material stays unchanged.
        runtimeSkybox = new Material(originalSkybox);
        runtimeSkybox.name = originalSkybox.name + " (Runtime Fade)";
        originalTint = runtimeSkybox.GetColor(TintProperty);

        RenderSettings.skybox = runtimeSkybox;

        UpdateSkyboxFade();
    }

    private void Update()
    {
        UpdateSkyboxFade();
    }

    private void UpdateSkyboxFade()
    {
        if (playerCamera == null || runtimeSkybox == null)
            return;

        float distance =
            Vector3.Distance(playerCamera.position, center);

        float fade;

        if (fadeEndDistance <= fadeStartDistance)
        {
            fade = distance >= fadeStartDistance ? 1f : 0f;
        }
        else
        {
            fade = Mathf.InverseLerp(
                fadeStartDistance, fadeEndDistance, distance);

            fade = Mathf.SmoothStep(0f, 1f, fade);
        }

        runtimeSkybox.SetColor(
            TintProperty,
            Color.Lerp(originalTint, Color.black, fade));
    }

    private void OnDestroy()
    {
        if (RenderSettings.skybox == runtimeSkybox)
            RenderSettings.skybox = originalSkybox;

        if (runtimeSkybox != null)
            Destroy(runtimeSkybox);
    }
}
