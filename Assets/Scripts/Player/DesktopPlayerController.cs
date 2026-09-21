using UnityEngine;
using UnityEngine.InputSystem;

public class DesktopPlayerController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 3.0f;
    public float verticalSpeed = 3.0f;

    [Header("Mouse Look")]
    public Transform cameraTransform;
    public float mouseSensitivity = 0.1f;
    public float maxLookAngle = 89.0f;

    [Header("Dust")]
    public DustCloudGenerator dustCloudGenerator;
    public int performanceParticleCount = 16000;
    public int qualityParticleCount = 20000;

    [Header("Landmarks")]
    public LandmarkMarkerGenerator landmarkMarkerGenerator;
    public LandmarkPopup landmarkPopup;
    public float selectionRayDistance = 50.0f;
    public LayerMask landmarkLayerMask = ~0;

    private bool usingQualityParticleCount = true;

    private float cameraPitch = 0f;

    private void Start()
    {
        // Use the Main Camera automatically if no camera was assigned.
        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Update()
    {
        if (Keyboard.current == null)
            return;

        if (Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        HandleMovement();
        HandleMouseLook();
        HandleControls();
    }

    private void HandleMovement()
    {
        float move = 0f;

        if (Keyboard.current.wKey.isPressed)
            move += 1f;

        if (Keyboard.current.sKey.isPressed)
            move -= 1f;

        transform.position +=
            transform.forward *
            move *
            moveSpeed *
            Time.deltaTime;

        float strafe = 0f;

        if (Keyboard.current.aKey.isPressed)
            strafe -= 1f;

        if (Keyboard.current.dKey.isPressed)
            strafe += 1f;

        transform.position +=
            transform.right *
            strafe *
            moveSpeed *
            Time.deltaTime;

        if (Keyboard.current.leftShiftKey.isPressed)
        {
            transform.position +=
                Vector3.down *
                verticalSpeed *
                Time.deltaTime;
        }

        if (Keyboard.current.spaceKey.isPressed)
        {
            transform.position +=
                Vector3.up *
                verticalSpeed *
                Time.deltaTime;
        }
    }

    private void HandleMouseLook()
    {
        if (Mouse.current == null)
            return;

        Vector2 mouseDelta =
            Mouse.current.delta.ReadValue();

        float mouseX =
            mouseDelta.x * mouseSensitivity;

        float mouseY =
            mouseDelta.y * mouseSensitivity;

        // Horizontal look: rotate the player.
        transform.Rotate(
            Vector3.up,
            mouseX,
            Space.World
        );

        // Vertical look: rotate only the camera.
        if (cameraTransform != null)
        {
            cameraPitch -= mouseY;

            cameraPitch =
                Mathf.Clamp(
                    cameraPitch,
                    -maxLookAngle,
                    maxLookAngle
                );

            cameraTransform.localRotation =
                Quaternion.Euler(
                    cameraPitch,
                    0f,
                    0f
                );
        }
    }

    private void HandleControls()
    {
        if (dustCloudGenerator != null)
        {
            // R = next color scheme
            if (Keyboard.current.rKey.wasPressedThisFrame)
            {
                dustCloudGenerator.NextColorScheme();
            }

            // T = cycle particle size
            if (Keyboard.current.tKey.wasPressedThisFrame)
            {
                dustCloudGenerator.CycleParticleSizeMultiplier();
            }

            // Y = toggle particle count
            if (Keyboard.current.yKey.wasPressedThisFrame)
            {
                usingQualityParticleCount =
                    !usingQualityParticleCount;

                int particleCount =
                    usingQualityParticleCount
                        ? qualityParticleCount
                        : performanceParticleCount;

                dustCloudGenerator.SetParticleCount(
                    particleCount
                );
            }

            // [ = decrease particle size
            if (Keyboard.current.leftBracketKey.wasPressedThisFrame)
            {
                dustCloudGenerator.AdjustParticleSizeMultiplier(
                    -0.2f
                );
            }

            // ] = increase particle size
            if (Keyboard.current.rightBracketKey.wasPressedThisFrame)
            {
                dustCloudGenerator.AdjustParticleSizeMultiplier(
                    0.2f
                );
            }
        }

        // Tab = toggle landmark markers
        if (
            landmarkMarkerGenerator != null &&
            Keyboard.current.tabKey.wasPressedThisFrame
        )
        {
            landmarkMarkerGenerator.ToggleMarkers();
        }

        // E = select landmark
        if (Keyboard.current.eKey.wasPressedThisFrame)
        {
            TrySelectLandmark();
        }

        // Q = close landmark popup
        if (
            landmarkPopup != null &&
            Keyboard.current.qKey.wasPressedThisFrame
        )
        {
            landmarkPopup.Hide();
        }
    }

    private void TrySelectLandmark()
    {
        if (cameraTransform == null)
        {
            Debug.LogWarning(
                "DesktopPlayerController: No camera assigned."
            );

            return;
        }

        Ray ray = new Ray(
            cameraTransform.position,
            cameraTransform.forward
        );

        if (Physics.Raycast(
            ray,
            out RaycastHit hit,
            selectionRayDistance
        ))
        {
            LandmarkInfo info =
                hit.collider.GetComponentInParent<LandmarkInfo>();

            if (info != null)
            {
                Debug.Log(
                    "Selected landmark: " +
                    info.objectName
                );

                if (landmarkPopup != null)
                {
                    landmarkPopup.Show(info);
                }
            }
            else
            {
                Debug.Log(
                    "Ray hit object without LandmarkInfo: " +
                    hit.collider.name
                );
            }
        }
        else
        {
            Debug.Log("Raycast hit nothing.");
        }
    }
}