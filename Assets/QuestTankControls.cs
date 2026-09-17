using UnityEngine;
using UnityEngine.XR;

public class QuestTankControls : MonoBehaviour
{
    public DustCloudGenerator dustCloudGenerator;
    public LandmarkPopup landmarkPopup;
    public LandmarkMarkerGenerator landmarkMarkerGenerator;

    // New XR Origin → Camera Offset → Main Camera
    public Transform headsetCamera;

    // New XR Origin → Camera Offset → Right Controller
    public Transform rightControllerTransform;

    public float moveSpeed = 1.25f;
    public float verticalSpeed = 1.25f;
    public float strafeSpeed = 1.25f;
    public float turnSpeed = 35.0f;

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

    private bool previousA = false;
    private bool previousB = false;

    private bool previousLeftTrigger = false;
    private bool previousLeftGrip = false;
    private bool previousRightGrip = false;

    private bool previousRightTrigger = false;

    private bool usingQualityParticleCount = true;

    private void Start()
    {
        if (rightRayLine != null)
        {
            rightRayLine.enabled = false;
        }
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
            smoothedTurn * turnSpeed * Time.deltaTime
        );

        Transform movementReference =
            headsetCamera != null ? headsetCamera : transform;

        Vector3 forward = movementReference.forward;
        forward.y = 0f;

        if (forward.sqrMagnitude > 0.001f)
        {
            forward.Normalize();
        }

        Vector3 right =
            Vector3.Cross(Vector3.up, forward);

        right.Normalize();

        Vector3 movement =
            forward * smoothedMove * moveSpeed +
            right * rightStick.x * strafeSpeed +
            Vector3.up * rightStick.y * verticalSpeed;

        transform.position += movement * Time.deltaTime;

        if (dustCloudGenerator != null)
        {
            HandleColorControls(leftHand);
            HandleParticleCountControls(rightHand);
        }

        HandleLandmarkSelection(rightHand);

        HandleLandmarkPopupClose(rightHand);
    }

    private void HandleColorControls(InputDevice leftHand)
    {
        bool leftTriggerPressed = false;
        bool leftGripPressed = false;

        leftHand.TryGetFeatureValue(
            CommonUsages.triggerButton,
            out leftTriggerPressed
        );

        leftHand.TryGetFeatureValue(
            CommonUsages.gripButton,
            out leftGripPressed
        );

        if (leftTriggerPressed && !previousLeftTrigger)
        {
            dustCloudGenerator.NextColorScheme();
        }

        if (leftGripPressed && !previousLeftGrip)
        {
            dustCloudGenerator.CycleParticleSizeMultiplier();
        }

        previousLeftTrigger = leftTriggerPressed;
        previousLeftGrip = leftGripPressed;
    }

    private void HandleLandmarkPopupClose(InputDevice rightHand)
{
    bool rightGripPressed = false;

    rightHand.TryGetFeatureValue(
        CommonUsages.gripButton,
        out rightGripPressed
    );

    if (rightGripPressed && !previousRightGrip)
    {
        if (landmarkPopup != null)
        {
            landmarkPopup.Hide();
        }
    }

    previousRightGrip = rightGripPressed;
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

        if (buttonA && !previousA)
        {
            usingQualityParticleCount =
                !usingQualityParticleCount;

            int particleCount = usingQualityParticleCount
                ? qualityParticleCount
                : performanceParticleCount;

            dustCloudGenerator.SetParticleCount(particleCount);
        }

        if (buttonB && !previousB)
        {
            if (landmarkMarkerGenerator != null)
            {
                landmarkMarkerGenerator.ToggleMarkers();
            }
        }

        previousA = buttonA;
        previousB = buttonB;
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

        if (rightTriggerPressed && !previousRightTrigger)
        {
            TrySelectLandmark();
        }

        previousRightTrigger = rightTriggerPressed;
    }

    private void TrySelectLandmark()
    {
        if (rightControllerTransform == null)
        {
            Debug.LogWarning(
                "Right Controller Transform has not been assigned."
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
                rayDirection * selectionRayDistance
            );
        }

        Ray ray =
            new Ray(rayOrigin, rayDirection);

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