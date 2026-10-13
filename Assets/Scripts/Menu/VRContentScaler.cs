using UnityEngine;
using UnityEngine.XR;

public class VRContentScaler : MonoBehaviour
{
    [Range(0.1f, 1f)]
    public float vrScale = 0.4f;

    private Vector3 originalScale;

    private void Awake()
    {
        originalScale = transform.localScale;
    }

    private void LateUpdate()
    {
        bool shouldScale = Application.platform == RuntimePlatform.Android || XRSettings.isDeviceActive;

        transform.localScale = originalScale * (shouldScale ? vrScale : 1f);
    }
}