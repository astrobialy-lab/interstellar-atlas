
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;

public class VRPlayerController : MonoBehaviour
{
    public LandmarkPopup landmarkPopup;
    public LandmarkMarkerGenerator landmarkMarkerGenerator;

    [Header("Orientation")]
    public GameObject orientation;
    public Transform xrOrigin;
    public Transform headsetCamera;
    public Transform rightControllerTransform;

    [Header("MS-LOD Debug")]
    public GaiaMSLODBoundsVisualizer msLODBoundsVisualizer;

    [Header("Movement")]
    public float moveSpeed = 1.25f;
    public float speedChangeFactor = 2.0f;
    public float turnSpeed = 35.0f;

    public float inputDeadzone = 0.15f;
    public float movementSmoothing = 4.0f;
    public float turnSmoothing = 4.0f;

    [Header("Raycast Selection")]
    public float selectionRayDistance = 50.0f;
    public LayerMask landmarkLayerMask = ~0;
    public LineRenderer rightRayLine;

    [Header("Dust")]
    public DustSpriteRenderer dustSpriteRenderer;

    private float smoothedMove = 0f;
    private float smoothedTurn = 0f;

    private bool previousLeftX = false;
    private bool previousLeftY = false;
    private bool previousLeftTrigger = false;
    private bool previousLeftGrip = false;
    private bool previousRightTrigger = false;
    private bool previousRightGrip = false;
    private bool previousRightB = false;
    private bool previousRightA = false;

    private void Start()
    {
        if (!XRSettings.isDeviceActive)
        {
            Destroy(this);
            return;
        }

        if (dustSpriteRenderer == null)
            dustSpriteRenderer = FindAnyObjectByType<DustSpriteRenderer>();

        if (msLODBoundsVisualizer == null)
            msLODBoundsVisualizer =
                FindAnyObjectByType<GaiaMSLODBoundsVisualizer>();

        if (rightRayLine != null)
            rightRayLine.enabled = false;
    }

    private void Update()
    {
        InputDevice leftHand = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        InputDevice rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

        Vector2 leftStick = Vector2.zero;
        Vector2 rightStick = Vector2.zero;

        leftHand.TryGetFeatureValue(
            CommonUsages.primary2DAxis, out leftStick);

        rightHand.TryGetFeatureValue(
            CommonUsages.primary2DAxis, out rightStick);

        float rawMove =
            Mathf.Abs(leftStick.y) > inputDeadzone
                ? leftStick.y
                : 0f;

        float rawTurn =
            Mathf.Abs(leftStick.x) > inputDeadzone
                ? leftStick.x
                : 0f;

        smoothedMove = Mathf.Lerp(
            smoothedMove,
            rawMove,
            movementSmoothing * Time.deltaTime);

        smoothedTurn = Mathf.Lerp(
            smoothedTurn,
            rawTurn,
            turnSmoothing * Time.deltaTime);

        transform.Rotate(
            Vector3.up,
            smoothedTurn * turnSpeed * Time.deltaTime);

        Transform movementReference =
            headsetCamera != null ? headsetCamera : transform;

        Vector3 forward = movementReference.forward;
        forward.y = 0f;

        if (forward.sqrMagnitude > 0.001f)
            forward.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, forward);
        right.Normalize();

        Vector3 movement =
            forward * smoothedMove * moveSpeed +
            right * rightStick.x * moveSpeed +
            Vector3.up * rightStick.y * moveSpeed;

        transform.position += movement * Time.deltaTime;

        HandleSpeedControls(leftHand);
        HandleDustRenderingControls(leftHand, rightHand);
        HandleMSLODBoundsControls(rightHand);
        HandleLandmarkToggle(rightHand);
        HandleLandmarkSelection(rightHand);
    }

    private void HandleSpeedControls(InputDevice leftHand)
    {
        bool leftX = false;
        bool leftY = false;

        leftHand.TryGetFeatureValue(CommonUsages.primaryButton, out leftX);
        leftHand.TryGetFeatureValue(CommonUsages.secondaryButton, out leftY);

        // X = increase speed.
        if (leftX && !previousLeftX)
        {
            moveSpeed *= speedChangeFactor;
        }

        // Y = decrease speed.
        if (leftY && !previousLeftY)
        {
            moveSpeed /= speedChangeFactor;
        }

        previousLeftX = leftX;
        previousLeftY = leftY;
    }

    private void HandleDustRenderingControls(InputDevice leftHand, InputDevice rightHand)
    {
        bool leftTriggerPressed = false;
        bool leftGripPressed = false;
        bool rightTriggerPressed = false;

        leftHand.TryGetFeatureValue(
            CommonUsages.triggerButton,
            out leftTriggerPressed);

        leftHand.TryGetFeatureValue(
            CommonUsages.gripButton,
            out leftGripPressed);

        rightHand.TryGetFeatureValue(
            CommonUsages.triggerButton,
            out rightTriggerPressed);

        if (dustSpriteRenderer != null)
        {
            // Left Trigger: cycle through color schemes.
            if (leftTriggerPressed && !previousLeftTrigger)
                dustSpriteRenderer.NextColorScheme();

            // Left Grip: cycle through sprite sizes.
            if (leftGripPressed && !previousLeftGrip)
                dustSpriteRenderer.CycleSpriteSizeMultiplier();

            // Right Trigger = cycle dust transparency.
            if (rightTriggerPressed && !previousRightTrigger)
                dustSpriteRenderer.CycleSpriteTransparency();
        }

        previousLeftTrigger = leftTriggerPressed;
        previousLeftGrip = leftGripPressed;
        previousRightTrigger = rightTriggerPressed;
    }

    private void HandleMSLODBoundsControls(InputDevice rightHand)
    {
        bool rightGripPressed = false;

        rightHand.TryGetFeatureValue(
            CommonUsages.gripButton,
            out rightGripPressed);

        if (rightGripPressed && !previousRightGrip)
        {
            if (msLODBoundsVisualizer != null)
                msLODBoundsVisualizer.RefreshBounds();
        }

        previousRightGrip = rightGripPressed;
    }

    private void HandleLandmarkToggle(InputDevice rightHand)
    {
        bool rightBPressed = false;

        rightHand.TryGetFeatureValue(
            CommonUsages.secondaryButton,
            out rightBPressed);

        // B = toggle landmark markers and orientation.
        if (rightBPressed && !previousRightB)
        {
            if (landmarkMarkerGenerator != null)
                landmarkMarkerGenerator.ToggleMarkers();

            if (orientation != null)
                orientation.SetActive(!orientation.activeSelf);
        }

        previousRightB = rightBPressed;
    }

    private void HandleLandmarkSelection(InputDevice rightHand)
    {
        bool rightAPressed = false;

        rightHand.TryGetFeatureValue(
            CommonUsages.primaryButton,
            out rightAPressed);

        // Right A = select landmark.
        if (rightAPressed && !previousRightA)
            TrySelectLandmark();

        previousRightA = rightAPressed;
    }

    private void TrySelectLandmark()
    {
        if (rightControllerTransform == null)
        {
            Debug.LogWarning(
                "Right Controller Transform has not been assigned.");
            return;
        }

        Vector3 rayOrigin = rightControllerTransform.position;
        Vector3 rayDirection = rightControllerTransform.forward;

        if (rightRayLine != null)
        {
            rightRayLine.enabled = true;
            rightRayLine.useWorldSpace = true;
            rightRayLine.positionCount = 2;
            rightRayLine.SetPosition(0, rayOrigin);
            rightRayLine.SetPosition(
                1, rayOrigin + rayDirection * selectionRayDistance);
        }

        Ray ray = new Ray(rayOrigin, rayDirection);

        if (Physics.Raycast(ray, out RaycastHit hit, selectionRayDistance))
        {
            LandmarkInfo info =
                hit.collider.GetComponentInParent<LandmarkInfo>();

            if (info != null)
            {
                Debug.Log("Selected landmark: " + info.objectName);

                if (landmarkPopup != null)
                    landmarkPopup.Show(info);
            }
            else
            {
                Debug.Log(
                    "Ray hit object without LandmarkInfo: " + hit.collider.name);
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
        "Left Stick: move / turn",
        "Right Stick: strafe / rise / descend",
        "X: increase speed",
        "Y: decrease speed",
        "A: select landmark",
        "B: show / hide landmarks and orientation",
        "Left Trigger: next dust color scheme",
        "Left Grip: cycle dust sprite size",
        "Right Trigger: cycle dust sprite transparency",
        "Right Grip: refresh Gaia MS-LOD bounds",
        "Left Menu Button: open menu / close current menu"
        });
    }
}
