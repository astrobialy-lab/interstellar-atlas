using UnityEngine;
using UnityEngine.XR;

public class VRPlayerController : MonoBehaviour
{
    public LandmarkPopup landmarkPopup;
    public LandmarkMarkerGenerator landmarkMarkerGenerator;

    [Header("Orientation")]
    public GameObject orientation;

    public Transform xrOrigin;

    // New XR Origin → Camera Offset → Main Camera
    public Transform headsetCamera;

    // New XR Origin → Camera Offset → Right Controller
    public Transform rightControllerTransform;

    [Header("MS-LOD Debug")]
    public GaiaMSLODBoundsVisualizer msLODBoundsVisualizer;

    [Header("Movement")]
    public float moveSpeed = 1.25f;
    public float speedChangeFactor = 2.0f;
    public float turnSpeed = 35.0f;

    [Header("Fast Travel")]
    public float fastTravelDuration = 3.0f;
    private bool isFastTraveling = false;
    private Vector3 fastTravelStart;
    private Vector3 fastTravelTarget;
    private float fastTravelTime;

    public float inputDeadzone = 0.15f;
    public float movementSmoothing = 4.0f;
    public float turnSmoothing = 4.0f;

    public int performanceParticleCount = 16000;
    public int qualityParticleCount = 20000;

    [Header("Raycast Selection")]
    public float selectionRayDistance = 50.0f;
    public LayerMask landmarkLayerMask = ~0;

    public LineRenderer rightRayLine;

    private float smoothedMove = 0f;
    private float smoothedTurn = 0f;

    private bool previousLeftX = false;
    private bool previousLeftY = false;
    private bool previousB = false;

    private bool previousRightTrigger = false;
    private bool previousRightGrip = false;

    private void Start()
    {
        if (msLODBoundsVisualizer == null)
        {
            msLODBoundsVisualizer =
                FindAnyObjectByType<GaiaMSLODBoundsVisualizer>();
        }

        if (rightRayLine != null)
        {
            rightRayLine.enabled = false;
        }
    }

    private void Update()
    {
        if (isFastTraveling)
        {
            UpdateFastTravel();
            return;
        }

        InputDevice leftHand =
            InputDevices.GetDeviceAtXRNode(
                XRNode.LeftHand
            );

        InputDevice rightHand =
            InputDevices.GetDeviceAtXRNode(
                XRNode.RightHand
            );

        Vector2 leftStick = Vector2.zero;
        Vector2 rightStick = Vector2.zero;

        leftHand.TryGetFeatureValue(
            CommonUsages.primary2DAxis,
            out leftStick
        );

        rightHand.TryGetFeatureValue(
            CommonUsages.primary2DAxis,
            out rightStick
        );

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
            movementSmoothing * Time.deltaTime
        );

        smoothedTurn = Mathf.Lerp(
            smoothedTurn,
            rawTurn,
            turnSmoothing * Time.deltaTime
        );

        transform.Rotate(
            Vector3.up,
            smoothedTurn *
            turnSpeed *
            Time.deltaTime
        );

        Transform movementReference =
            headsetCamera != null
                ? headsetCamera
                : transform;

        Vector3 forward =
            movementReference.forward;

        forward.y = 0f;

        if (forward.sqrMagnitude > 0.001f)
        {
            forward.Normalize();
        }

        Vector3 right =
            Vector3.Cross(
                Vector3.up,
                forward
            );

        right.Normalize();

        Vector3 movement =
            forward *
            smoothedMove *
            moveSpeed
            +
            right *
            rightStick.x *
            moveSpeed
            +
            Vector3.up *
            rightStick.y *
            moveSpeed;

        transform.position +=
            movement *
            Time.deltaTime;

        HandleSpeedControls(
            leftHand
        );

        HandleMSLODBoundsControls(
            rightHand
        );

        HandleLandmarkSelection(
            rightHand
        );
    }

    public void StartFastTravel(
        Vector3 destination
    )
    {
        if (isFastTraveling)
        {
            return;
        }

        fastTravelStart =
            transform.position;

        fastTravelTarget =
            destination;

        fastTravelTime = 0f;

        isFastTraveling = true;

        Debug.Log(
            $"Starting fast travel from " +
            $"{fastTravelStart} to " +
            $"{fastTravelTarget}"
        );
    }

    private void UpdateFastTravel()
    {
        fastTravelTime +=
            Time.deltaTime;

        float t =
            Mathf.Clamp01(
                fastTravelTime /
                fastTravelDuration
            );

        // Smooth ease-in/ease-out.
        float smoothT =
            t *
            t *
            (3f - 2f * t);

        transform.position =
            Vector3.Lerp(
                fastTravelStart,
                fastTravelTarget,
                smoothT
            );

        if (t >= 1f)
        {
            transform.position =
                fastTravelTarget;

            isFastTraveling = false;

            Debug.Log(
                "Fast travel complete."
            );
        }
    }

    private void HandleSpeedControls(
        InputDevice leftHand
    )
    {
        bool leftX = false;
        bool leftY = false;

        leftHand.TryGetFeatureValue(
            CommonUsages.primaryButton,
            out leftX
        );

        leftHand.TryGetFeatureValue(
            CommonUsages.secondaryButton,
            out leftY
        );

        // X = increase speed
        if (leftX && !previousLeftX)
        {
            moveSpeed *=
                speedChangeFactor;

            if (xrOrigin != null)
            {
                xrOrigin.localScale /=
                    speedChangeFactor;
            }
        }

        // Y = decrease speed
        if (leftY && !previousLeftY)
        {
            moveSpeed /=
                speedChangeFactor;

            if (xrOrigin != null)
            {
                xrOrigin.localScale *=
                    speedChangeFactor;
            }
        }

        previousLeftX =
            leftX;

        previousLeftY =
            leftY;
    }

    private void HandleParticleCountControls(
        InputDevice rightHand
    )
    {
        bool buttonA = false;
        bool buttonB = false;

        rightHand.TryGetFeatureValue(
            CommonUsages.primaryButton,
            out buttonA
        );

        rightHand.TryGetFeatureValue(
            CommonUsages.secondaryButton,
            out buttonB
        );

        // B = toggle landmark markers and Orientation
        if (buttonB && !previousB)
        {
            if (landmarkMarkerGenerator != null)
            {
                landmarkMarkerGenerator
                    .ToggleMarkers();
            }

            if (orientation != null)
            {
                orientation.SetActive(
                    !orientation.activeSelf
                );
            }
        }

        previousB =
            buttonB;
    }

    private void HandleMSLODBoundsControls(
        InputDevice rightHand
    )
    {
        bool rightGripPressed = false;

        rightHand.TryGetFeatureValue(
            CommonUsages.gripButton,
            out rightGripPressed
        );

        // Right Grip = toggle MS-LOD bounds
        if (rightGripPressed &&
            !previousRightGrip)
        {
            if (msLODBoundsVisualizer != null)
            {
                msLODBoundsVisualizer
                    .RefreshBounds();
            }
        }

        previousRightGrip =
            rightGripPressed;
    }

    private void HandleLandmarkSelection(
        InputDevice rightHand
    )
    {
        bool rightTriggerPressed = false;

        rightHand.TryGetFeatureValue(
            CommonUsages.triggerButton,
            out rightTriggerPressed
        );

        if (rightTriggerPressed &&
            !previousRightTrigger)
        {
            TrySelectLandmark();
        }

        previousRightTrigger =
            rightTriggerPressed;
    }

    private void TrySelectLandmark()
    {
        if (rightControllerTransform == null)
        {
            Debug.LogWarning(
                "Right Controller Transform " +
                "has not been assigned."
            );

            return;
        }

        Vector3 rayOrigin =
            rightControllerTransform.position;

        Vector3 rayDirection =
            rightControllerTransform.forward;

        if (rightRayLine != null)
        {
            rightRayLine.enabled = true;
            rightRayLine.useWorldSpace = true;
            rightRayLine.positionCount = 2;

            rightRayLine.SetPosition(
                0,
                rayOrigin
            );

            rightRayLine.SetPosition(
                1,
                rayOrigin +
                rayDirection *
                selectionRayDistance
            );
        }

        Ray ray =
            new Ray(
                rayOrigin,
                rayDirection
            );

        if (Physics.Raycast(
            ray,
            out RaycastHit hit,
            selectionRayDistance
        ))
        {
            LandmarkInfo info =
                hit.collider
                    .GetComponentInParent<
                        LandmarkInfo
                    >();

            if (info != null)
            {
                Debug.Log(
                    "Selected landmark: " +
                    info.objectName
                );

                if (landmarkPopup != null)
                {
                    landmarkPopup.Show(
                        info
                    );
                }
            }
            else
            {
                Debug.Log(
                    "Ray hit object without " +
                    "LandmarkInfo: " +
                    hit.collider.name
                );
            }
        }
        else
        {
            Debug.Log(
                "Raycast hit nothing."
            );
        }
    }
}