
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;

public class VRPlayerController : MonoBehaviour
{
    public MarkerPopup markerPopup;
    public LandmarkMarkerGenerator landmarkMarkerGenerator;

    [Header("Orientation")]
    public GameObject orientation;
    public Transform xrOrigin;
    public Transform headsetCamera;
    public Transform rightControllerTransform;

    [Header("Look Controls")]
    public float maxLookAngle = 89f;
    private float smoothedLookPitch = 0f;
    private float virtualPitch = 0f;

    [Header("Roll")]
    public float rollSpeed = 20f;

    [Header("Stars")]
    public GaiaStarFilter starFilter;
    public GaiaMSLODBoundsVisualizer msLODBoundsVisualizer;

    [Header("Movement")]
    public float moveSpeed = 1.25f;
    public float speedChangeFactor = 2.0f;
    public float turnSpeed = 50.0f;

    public float inputDeadzone = 0.15f;
    public float movementSmoothing = 4.0f;
    public float turnSmoothing = 4.0f;

    [Header("Raycast Selection")]
    public float selectionRayDistance = 50.0f;
    public LayerMask landmarkLayerMask = ~0;
    public LineRenderer rightRayLine;

    [Header("Dust")]
    public DustSpriteRenderer dustSpriteRenderer;

    private float smoothedTurn = 0f;

    private bool previousLeftX = false;
    private bool previousLeftY = false;
    private bool previousRightA = false;
    private bool previousRightB = false;
    private bool previousLeftTrigger = false;
    private bool previousLeftGrip = false;
    private bool previousRightTrigger = false;
    private bool previousRightGrip = false;

    // Right trigger single/double-click detection.
    [SerializeField, Min(0.1f)]
    private float rightTriggerDoubleClickWindow = 0.3f;

    private bool rightTriggerSingleClickPending = false;
    private float rightTriggerFirstClickTime = 0f;

    private void Start()
    {
        if (!XRSettings.isDeviceActive)
        {
            Destroy(this);
            return;
        }

        if (dustSpriteRenderer == null)
            dustSpriteRenderer = FindAnyObjectByType<DustSpriteRenderer>();

        if (starFilter == null)
            starFilter = FindAnyObjectByType<GaiaStarFilter>();

        if (msLODBoundsVisualizer == null)
            msLODBoundsVisualizer =
                FindAnyObjectByType<GaiaMSLODBoundsVisualizer>();

        if (rightRayLine != null)
            rightRayLine.enabled = false;
    }

    private void Update()
    {
        InputDevice leftHand =
            InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);

        InputDevice rightHand =
            InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

        Vector2 leftStick = Vector2.zero;
        Vector2 rightStick = Vector2.zero;

        leftHand.TryGetFeatureValue(
            CommonUsages.primary2DAxis, out leftStick);

        rightHand.TryGetFeatureValue(
            CommonUsages.primary2DAxis, out rightStick);

        float rawMove =
            Mathf.Abs(leftStick.y) > inputDeadzone
                ? leftStick.y : 0f;

        float rawStrafe =
            Mathf.Abs(leftStick.x) > inputDeadzone
                ? leftStick.x : 0f;

        // Right stick left/right: turn the view horizontally.
        float rawLookTurn =
            Mathf.Abs(rightStick.x) > inputDeadzone
                ? rightStick.x : 0f;

        // Right stick up/down: tilt the view vertically.
        float rawLookPitch =
            Mathf.Abs(rightStick.y) > inputDeadzone
                ? rightStick.y : 0f;

        smoothedTurn = Mathf.Lerp(
            smoothedTurn,
            rawLookTurn,
            turnSmoothing * Time.deltaTime);

        smoothedLookPitch = Mathf.Lerp(
            smoothedLookPitch,
            rawLookPitch,
            turnSmoothing * Time.deltaTime);

        // Rotate around the headset position so the viewer stays in place.
        Transform rotationPivot =
            headsetCamera != null ? headsetCamera : transform;

        // Horizontal rotation around world up.
        transform.RotateAround(
            rotationPivot.position,
            Vector3.up,
            smoothedTurn * turnSpeed * Time.deltaTime);

        // Vertical rotation around the headset's current right axis.
        float pitchDelta =
            -smoothedLookPitch * turnSpeed * Time.deltaTime;

        float nextPitch = Mathf.Clamp(
            virtualPitch + pitchDelta,
            -maxLookAngle,
            maxLookAngle);

        pitchDelta = nextPitch - virtualPitch;
        virtualPitch = nextPitch;

        if (Mathf.Abs(pitchDelta) > 0.0001f)
        {
            transform.RotateAround(
                rotationPivot.position,
                rotationPivot.right,
                pitchDelta);
        }

        HandleRoll(leftHand, rightHand);

        // Left stick movement follows the headset's direction.
        Transform movementReference =
            headsetCamera != null ? headsetCamera : transform;

        Vector3 forward = movementReference.forward;
        Vector3 viewRight = movementReference.right;

        Vector3 movement =
            forward * rawMove +
            viewRight * rawStrafe;

        transform.position +=
            movement * moveSpeed * Time.deltaTime;

        HandleStarDisplayControl(rightHand);
        HandleVerticalMovement(rightHand);
        HandleSpeedControls(leftHand);
        HandleDustRenderingControls(leftHand);
        HandleRightTriggerGestures(rightHand);
    }

    private void HandleStarDisplayControl(InputDevice rightHand)
    {
        bool rightGripPressed = false;

        rightHand.TryGetFeatureValue(
            CommonUsages.gripButton,
            out rightGripPressed);

        // Cycle only once per press.
        if (rightGripPressed && !previousRightGrip)
        {
            if (starFilter != null)
            {
                starFilter.CycleDisplayStage();
            }
            else
            {
                Debug.LogWarning(
                    "VRPlayerController: GaiaStarFilter not found.");
            }
        }

        previousRightGrip = rightGripPressed;
    }

    private void HandleRoll(InputDevice leftHand, InputDevice rightHand)
    {
        bool leftStickPressed = false;
        bool rightStickPressed = false;

        leftHand.TryGetFeatureValue(
            CommonUsages.primary2DAxisClick,
            out leftStickPressed);

        rightHand.TryGetFeatureValue(
            CommonUsages.primary2DAxisClick,
            out rightStickPressed);

        // Left stick = counter-clockwise.
        // Right stick = clockwise.
        float rollDirection =
            (leftStickPressed ? 1f : 0f) -
            (rightStickPressed ? 1f : 0f);

        if (rollDirection == 0f || headsetCamera == null)
            return;

        transform.RotateAround(
            headsetCamera.position,
            headsetCamera.forward,
            rollDirection * rollSpeed * Time.deltaTime);
    }

    private void HandleVerticalMovement(InputDevice rightHand)
    {
        bool buttonA = false;
        bool buttonB = false;

        rightHand.TryGetFeatureValue(CommonUsages.primaryButton, out buttonA);
        rightHand.TryGetFeatureValue(CommonUsages.secondaryButton, out buttonB);

        Transform view = headsetCamera != null ? headsetCamera : transform;

        // Use the headset's up direction, including its tilt.
        Vector3 viewUp = view.up;

        if (buttonB)
            transform.position += viewUp * moveSpeed * Time.deltaTime;

        if (buttonA)
            transform.position -= viewUp * moveSpeed * Time.deltaTime;

        previousRightA = buttonA;
        previousRightB = buttonB;
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

    private void HandleDustRenderingControls(InputDevice leftHand)
    {
        bool leftTriggerPressed = false;
        bool leftGripPressed = false;

        leftHand.TryGetFeatureValue(
            CommonUsages.triggerButton,
            out leftTriggerPressed);

        leftHand.TryGetFeatureValue(
            CommonUsages.gripButton,
            out leftGripPressed);

        if (dustSpriteRenderer != null)
        {
            // Left Trigger: cycle dust color schemes.
            if (leftTriggerPressed && !previousLeftTrigger)
                dustSpriteRenderer.NextColorScheme();

            // Left Grip: cycle dust transparency.
            if (leftGripPressed && !previousLeftGrip)
                dustSpriteRenderer.CycleSpriteTransparency();
        }

        previousLeftTrigger = leftTriggerPressed;
        previousLeftGrip = leftGripPressed;
    }

    private void HandleRightTriggerGestures(InputDevice rightHand)
    {
        bool triggerPressed = false;

        rightHand.TryGetFeatureValue(
            CommonUsages.triggerButton,
            out triggerPressed);

        // Detect a new press, rather than a held trigger.
        bool triggerDown = triggerPressed && !previousRightTrigger;
        previousRightTrigger = triggerPressed;

        // Execute a single click if its double-click window has expired.
        if (rightTriggerSingleClickPending &&
            Time.time - rightTriggerFirstClickTime >
            rightTriggerDoubleClickWindow)
        {
            rightTriggerSingleClickPending = false;
            TrySelectLandmark();
        }

        if (!triggerDown)
            return;

        // Second click within the time window = double click.
        if (rightTriggerSingleClickPending &&
            Time.time - rightTriggerFirstClickTime <=
            rightTriggerDoubleClickWindow)
        {
            rightTriggerSingleClickPending = false;
            ToggleLandmarksAndOrientation();
        }
        else
        {
            // Wait briefly to see whether another click follows.
            rightTriggerSingleClickPending = true;
            rightTriggerFirstClickTime = Time.time;
        }
    }

    private void ToggleLandmarksAndOrientation()
    {
        if (landmarkMarkerGenerator != null)
            landmarkMarkerGenerator.ToggleMarkers();

        if (orientation != null)
            orientation.SetActive(!orientation.activeSelf);
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
            MarkerInfo info =
                hit.collider.GetComponentInParent<MarkerInfo>();

            if (info != null)
            {
                Debug.Log("Selected marker: " + info.objectName);

                if (markerPopup != null)
                    markerPopup.Show(info);
            }
            else
            {
                Debug.Log(
                    "Ray hit object without MarkerInfo: " + hit.collider.name);
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
        "Left Stick: move / strafe",
        "Right Stick: look around",
        "Left / Right Stick press: roll counter-clockwise / clockwise",
        "X / Y: speed increase / decrease",
        "B / A: move up / down relative to viewing direction",
        "Left Trigger: next dust color scheme",
        "Left Grip: cycle dust sprite transparency",
        "Right Trigger single-click / double-click: Select landmark / Display landmarks and orientation",
        "Right Grip: cycle stars and clusters display stage",
        "Left Menu Button: open menu / close any menu"
        });
    }
}
