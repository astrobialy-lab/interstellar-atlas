using UnityEngine;
using UnityEngine.InputSystem;

public class DesktopPlayerController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 1.25f;
    public float speedChangeFactor = 2.0f;

    [Header("Mouse Look")]
    public Transform cameraTransform;
    public float mouseSensitivity = 0.1f;
    public float maxLookAngle = 89.0f;

    [Header("Camera Roll")]
    public float rollSpeed = 60.0f;

    [Header("Landmarks")]
    public LandmarkMarkerGenerator landmarkMarkerGenerator;
    public LandmarkPopup landmarkPopup;
    public float selectionRayDistance = 50.0f;
    public LayerMask landmarkLayerMask = ~0;

    [Header("Orientation")]
    public GameObject orientation;

    [Header("MS-LOD Debug")]
    public GaiaMSLODBoundsVisualizer msLODBoundsVisualizer;

    // Camera pitch and roll are stored independently.
    private float cameraPitch = 0f;
    private float cameraRoll = 0f;

    private void Start()
    {
        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }

        if (msLODBoundsVisualizer == null)
        {
            msLODBoundsVisualizer =
                FindAnyObjectByType<GaiaMSLODBoundsVisualizer>();
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
        HandleRoll();
        HandleControls();
    }

    private void HandleMovement()
    {
        if (cameraTransform == null)
            return;

        float move = 0f;

        if (Keyboard.current.wKey.isPressed)
            move += 1f;

        if (Keyboard.current.sKey.isPressed)
            move -= 1f;

        float strafe = 0f;

        if (Keyboard.current.aKey.isPressed)
            strafe -= 1f;

        if (Keyboard.current.dKey.isPressed)
            strafe += 1f;

        float vertical = 0f;

        if (Keyboard.current.spaceKey.isPressed)
            vertical += 1f;

        if (Keyboard.current.leftShiftKey.isPressed)
            vertical -= 1f;

        // Movement follows the camera, including camera roll.
        Vector3 movement =
            cameraTransform.forward * move +
            cameraTransform.right * strafe +
            cameraTransform.up * vertical;

        // Prevent diagonal movement from being faster.
        if (movement.sqrMagnitude > 1f)
            movement.Normalize();

        transform.position +=
            movement *
            moveSpeed *
            Time.deltaTime;
    }

    private void HandleMouseLook()
    {
        if (Mouse.current == null || cameraTransform == null)
            return;

        Vector2 mouseDelta =
            Mouse.current.delta.ReadValue();

        float mouseX =
            mouseDelta.x * mouseSensitivity;

        float mouseY =
            mouseDelta.y * mouseSensitivity;

        // Rotation caused by the current roll, but WITHOUT the current pitch.
        Quaternion rollRotation =
            Quaternion.AngleAxis(
                cameraRoll,
                Vector3.forward
            );

        // "Up" after applying roll, but before pitch.
        Vector3 rolledUp =
            transform.TransformDirection(
                rollRotation * Vector3.up
            );

        // "Right" after applying roll, but before pitch.
        Vector3 rolledRight =
            transform.TransformDirection(
                rollRotation * Vector3.right
            );

        // LEFT / RIGHT: rotate around the current rolled-up axis.
        if (mouseX != 0f)
        {
            transform.Rotate(
                rolledUp,
                mouseX,
                Space.World
            );
        }

        // UP / DOWN: rotate around the current rolled-right axis.
        cameraPitch -= mouseY;

        cameraPitch = Mathf.Clamp(
            cameraPitch,
            -maxLookAngle,
            maxLookAngle
        );

        ApplyCameraRotation();
    }

    private void HandleRoll()
    {
        if (cameraTransform == null)
            return;

        float roll = 0f;

        // E = clockwise
        if (Keyboard.current.eKey.isPressed)
            roll += 1f;

        // Q = counter-clockwise
        if (Keyboard.current.qKey.isPressed)
            roll -= 1f;

        if (roll == 0f)
            return;

        // Roll around the ZERO-PITCH forward axis.
        // This axis does not include the current pitch.
        cameraRoll -=
            roll *
            rollSpeed *
            Time.deltaTime;

        ApplyCameraRotation();
    }

    private void ApplyCameraRotation()
    {
        Quaternion rollRotation =
            Quaternion.AngleAxis(
                cameraRoll,
                Vector3.forward
            );

        Quaternion pitchRotation =
            Quaternion.AngleAxis(
                cameraPitch,
                Vector3.right
            );

        // Pitch is applied first, then the rolled reference frame is applied around it.
        cameraTransform.localRotation =
            rollRotation * pitchRotation;
    }

    private void HandleControls()
    {
        // Equals = increase movement speed
        if (Keyboard.current.equalsKey.wasPressedThisFrame)
        {
            moveSpeed *= speedChangeFactor;

            Debug.Log(
                $"Movement speed increased to {moveSpeed}"
            );
        }

        // Minus = decrease movement speed
        if (Keyboard.current.minusKey.wasPressedThisFrame)
        {
            moveSpeed /= speedChangeFactor;

            Debug.Log(
                $"Movement speed decreased to {moveSpeed}"
            );
        }

        // 6 = toggle landmark markers and Orientation
        if (Keyboard.current.digit6Key.wasPressedThisFrame)
        {
            if (landmarkMarkerGenerator != null)
            {
                landmarkMarkerGenerator.ToggleMarkers();
            }

            if (orientation != null)
            {
                orientation.SetActive(
                    !orientation.activeSelf
                );
            }
        }

        // 7 = select landmark
        if (Keyboard.current.digit7Key.wasPressedThisFrame)
        {
            TrySelectLandmark();
        }

        // 8 = refresh MS-LOD bounds
        if (Keyboard.current.digit8Key.wasPressedThisFrame)
        {
            if (msLODBoundsVisualizer != null)
            {
                msLODBoundsVisualizer.RefreshBounds();
            }
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
            LandmarkInfo info = hit.collider.GetComponentInParent<LandmarkInfo>();

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
