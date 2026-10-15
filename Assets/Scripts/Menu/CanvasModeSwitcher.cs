using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.XR.Management;

public class CanvasModeSwitcher : MonoBehaviour
{
    private Canvas canvas;
    private bool isVR;
    private bool initialized;

    private float WorlSpaceDistance = 5f;
    private float WorlSpaceScale => WorlSpaceDistance / 200f;

    private void Start()
    {
        canvas = GetComponent<Canvas>();

        isVR = XRGeneralSettings.Instance != null &&
               XRGeneralSettings.Instance.Manager != null &&
               XRGeneralSettings.Instance.Manager.activeLoader != null;

        if (isVR)
        {
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            canvas.transform.localScale = Vector3.one * WorlSpaceScale;
            canvas.GetComponent<RectTransform>().sizeDelta = new Vector2(950f, 520f);

            PositionInFrontOfPlayer();
        }
        else
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        }

        initialized = true;
    }

    private void OnEnable()
    {
        // On the first enable, Start() hasn't run yet.
        if (initialized && isVR)
        {
            PositionInFrontOfPlayer();
        }
    }

    private void PositionInFrontOfPlayer()
    {
        Camera cam = canvas.worldCamera;

        if (cam == null)
        {
            Debug.LogWarning("CanvasModeSwitcher: No camera assigned.");
            return;
        }

        canvas.transform.position = cam.transform.position + cam.transform.forward * WorlSpaceDistance;
        canvas.transform.rotation = cam.transform.rotation;
    }
}