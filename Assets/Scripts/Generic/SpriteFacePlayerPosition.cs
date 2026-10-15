using UnityEngine;

public class FacePlayerPosition : MonoBehaviour
{
    [SerializeField]
    private Transform playerCamera;

    public void SetPlayerCamera(Transform cameraTransform)
    {
        playerCamera = cameraTransform;
    }

    private void LateUpdate()
    {
        if (playerCamera == null)
        {
            Camera mainCamera = Camera.main;

            if (mainCamera == null)
                return;

            playerCamera = mainCamera.transform;
        }

        Vector3 direction = playerCamera.position - transform.position;

        if (direction.sqrMagnitude > 0.000001f)
            transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
    }
}