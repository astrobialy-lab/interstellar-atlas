using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Management;

public class DesktopPlayerController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 1.25f;
    public float speedChangeFactor = 2.0f;

    [Header("Mouse Look")]
    public Transform cameraTransform;
    public float mouseSensitivity = 0.1f;
    public float arrowLookSpeed = 30.0f;
    public float maxLookAngle = 89.0f;

    [Header("Camera Roll")]
    public float rollSpeed = 60.0f;

    [Header("Landmarks")]
    public LandmarkMarkerGenerator landmarkMarkerGenerator;
    public MarkerPopup markerPopup;
    public float selectionRayDistance = 50.0f;
    public LayerMask landmarkLayerMask = ~0;

    [Header("Orientation")]
    public GameObject orientation;

    [Header("Star Display")]
    public GaiaStarFilter starFilter;

    [Header("Stars LOD")]
    public StarsLODLoader starsLodLoader;

    private static readonly int[] StarDisplayModes = { 6, 4, 2, -1, -1 };

    private int starDisplayModePosition = 0;

    [Header("Stars LOD Debug")]
    public StarsLODNodesVisualizer starsLODNodesVisualizer;

    [Header("Dust Rendering")]
    public DustSpriteRenderer dustSpriteRenderer;

    // Camera pitch and roll are stored independently.
    private float cameraPitch = 0f;
    private float cameraRoll = 0f;

    private void Start()
    {
        if (!Application.isEditor &&
            Application.platform != RuntimePlatform.WindowsPlayer &&
            Application.platform != RuntimePlatform.OSXPlayer &&
            Application.platform != RuntimePlatform.LinuxPlayer &&
            Application.platform != RuntimePlatform.WindowsEditor &&
            Application.platform != RuntimePlatform.OSXEditor &&
            Application.platform != RuntimePlatform.LinuxEditor)
        {
            Destroy(this);
            return;
        }

        if (starFilter == null)
        {
            starFilter = FindAnyObjectByType<GaiaStarFilter>();
        }

        if (starsLodLoader == null)
        {
            starsLodLoader = FindAnyObjectByType<StarsLODLoader>();
        }

        if (dustSpriteRenderer == null)
        {
            dustSpriteRenderer = FindAnyObjectByType<DustSpriteRenderer>();
        }

        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }

        if (starsLODNodesVisualizer == null)
        {
            starsLODNodesVisualizer =
                FindAnyObjectByType<StarsLODNodesVisualizer>();
        }

        // Start in the default display mode: layer 6
        starDisplayModePosition = 0;

        if (starFilter != null)
        {
            starFilter.SetDisplayVisibility(true, false);
        }

        if (starsLodLoader != null)
        {
            starsLodLoader.SetLayerIndex(6);
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
        HandleArrowKeyLook();
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

        Vector2 mouseDelta = Mouse.current.delta.ReadValue();

        ApplyLook(
            mouseDelta.x * mouseSensitivity,
            mouseDelta.y * mouseSensitivity
        );
    }

    private void HandleArrowKeyLook()
    {
        if (cameraTransform == null)
            return;

        float horizontal = 0f;
        float vertical = 0f;

        if (Keyboard.current.leftArrowKey.isPressed)
            horizontal -= 1f;

        if (Keyboard.current.rightArrowKey.isPressed)
            horizontal += 1f;

        if (Keyboard.current.upArrowKey.isPressed)
            vertical += 1f;

        if (Keyboard.current.downArrowKey.isPressed)
            vertical -= 1f;

        if (horizontal == 0f && vertical == 0f)
            return;

        float lookDelta = arrowLookSpeed * Time.deltaTime;

        ApplyLook(
            horizontal * lookDelta,
            vertical * lookDelta
        );
    }

    private void ApplyLook(float lookX, float lookY)
    {
        // Rotation caused by the current roll, but WITHOUT the current pitch.
        Quaternion rollRotation =
            Quaternion.AngleAxis(cameraRoll, Vector3.forward);

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
        if (lookX != 0f)
        {
            transform.Rotate(
                rolledUp,
                lookX,
                Space.World
            );
        }

        // UP / DOWN: rotate around the current rolled-right axis.
        cameraPitch -= lookY;

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

        // 3 = cycle dust sprite transparency
        if (Keyboard.current.digit3Key.wasPressedThisFrame)
        {
            if (dustSpriteRenderer != null)
            {
                dustSpriteRenderer.CycleSpriteTransparency();
            }
        }

        // 4 = cycle dust color schemes
        if (Keyboard.current.digit4Key.wasPressedThisFrame)
        {
            if (dustSpriteRenderer != null)
            {
                dustSpriteRenderer.NextColorScheme();
            }
        }

        // 5 = cycle star LOD modes and clusters-only mode
        if (Keyboard.current.digit5Key.wasPressedThisFrame)
        {
            CycleStarDisplayMode();
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

        // 8 = toggle Stars LOD node bounds
        if (Keyboard.current.digit8Key.wasPressedThisFrame)
        {
            if (starsLODNodesVisualizer != null)
            {
                starsLODNodesVisualizer.RefreshBounds();
            }
        }
    }

    private void CycleStarDisplayMode()
    {
        int nextPosition =
            (starDisplayModePosition + 1) % StarDisplayModes.Length;

        int nextMode = StarDisplayModes[nextPosition];

        bool showStars = nextMode != -1;
        bool showClusters = nextPosition == 4;

        if (starFilter == null)
        {
            Debug.LogWarning("DesktopPlayerController: GaiaStarFilter not found.");
            return;
        }

        // Only select a real LOD layer.
        if (showStars)
        {
            if (starsLodLoader == null)
            {
                Debug.LogWarning("DesktopPlayerController: StarsLODLoader not found.");
                return;
            }

            if (!starsLodLoader.SetLayerIndex(nextMode))
            {
                Debug.LogWarning(
                    $"DesktopPlayerController: Could not select LOD layer {nextMode}."
                );
                return;
            }
        }

        starFilter.SetDisplayVisibility(
            showStars,
            showClusters
        );

        starDisplayModePosition = nextPosition;

        if (showStars)
            Debug.Log($"Star display: LOD layer {nextMode}");
        else if (showClusters)
            Debug.Log("Star display: clusters only");
        else
            Debug.Log("Star display: no stars, no clusters");
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
            MarkerInfo info = hit.collider.GetComponentInParent<MarkerInfo>();

            if (info != null)
            {
                Debug.Log(
                    "Selected landmark: " +
                    info.objectName
                );

                if (markerPopup != null)
                {
                    markerPopup.Show(info);
                }
            }
            else
            {
                Debug.Log(
                    "Ray hit object without MarkerInfo: " +
                    hit.collider.name
                );
            }
        }
        else
        {
            Debug.Log("Raycast hit nothing.");
        }
    }

    public string GetControls()
    {
        return string.Join("\n", new string[]
        {
        "W / S: forward / backward",
        "A / D: strafe left / right",
        "Space / Shift: move up / down",
        "Mouse / Arrow keys: look around",
        "Q / E: roll",
        "= / -: increase / decrease speed",
        "3: cycle dust sprite transparency",
        "4: next dust color scheme",
        "5: cycle star display (level: 6 / 4 / 2 / none / clusters only)",
        "6: show / hide landmarks and orientation",
        "7: select landmark",
        "8: toggle Stars LOD node bounds",
        "Escape / M: release mouse / open or close menu"
        });
    }
}
