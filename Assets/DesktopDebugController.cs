using UnityEngine;
using UnityEngine.InputSystem;

public class DesktopDebugController : MonoBehaviour
{
    public float moveSpeed = 3.0f;
    public float turnSpeed = 90.0f;
    public DustCloudGenerator dustCloudGenerator;
    public LandmarkMarkerGenerator landmarkMarkerGenerator;

    public int performanceParticleCount = 16000;
    public int qualityParticleCount = 20000;

    private bool usingQualityParticleCount = true;

    void Update()
    {
        if (Keyboard.current == null)
            return;

        float move = 0f;
        float turn = 0f;

        if (Keyboard.current.wKey.isPressed) move += 1f;
        if (Keyboard.current.sKey.isPressed) move -= 1f;
        if (Keyboard.current.aKey.isPressed) turn -= 1f;
        if (Keyboard.current.dKey.isPressed) turn += 1f;

        transform.Rotate(Vector3.up, turn * turnSpeed * Time.deltaTime);
        transform.position += transform.forward * move * moveSpeed * Time.deltaTime;

        if (Keyboard.current.qKey.isPressed)
            transform.position += Vector3.down * moveSpeed * Time.deltaTime;

        if (Keyboard.current.eKey.isPressed)
            transform.position += Vector3.up * moveSpeed * Time.deltaTime;
        // Desktop equivalents of the Quest appearance controls.
        if (dustCloudGenerator != null)
        {
            if (Keyboard.current.cKey.wasPressedThisFrame)
            {
                dustCloudGenerator.NextColorScheme();
            }

            if (Keyboard.current.tKey.wasPressedThisFrame)
            {
                dustCloudGenerator.CycleParticleSizeMultiplier();
            }

            if (Keyboard.current.pKey.wasPressedThisFrame)
            {
                usingQualityParticleCount =
                    !usingQualityParticleCount;

                int particleCount = usingQualityParticleCount
                    ? qualityParticleCount
                    : performanceParticleCount;

                dustCloudGenerator.SetParticleCount(particleCount);
            }

            // Fine-grained thickness adjustment for debugging.
            if (Keyboard.current.leftBracketKey.wasPressedThisFrame)
            {
                dustCloudGenerator.AdjustParticleSizeMultiplier(-0.2f);
            }

            if (Keyboard.current.rightBracketKey.wasPressedThisFrame)
            {
                dustCloudGenerator.AdjustParticleSizeMultiplier(0.2f);
            }
        }

        if (
            landmarkMarkerGenerator != null &&
            Keyboard.current.lKey.wasPressedThisFrame
        )
        {
            landmarkMarkerGenerator.ToggleMarkers();
        }
    }
}