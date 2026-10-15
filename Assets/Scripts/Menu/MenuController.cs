using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

using XRCommonUsages = UnityEngine.XR.CommonUsages;
using XRInputDevice = UnityEngine.XR.InputDevice;
using XRInputDevices = UnityEngine.XR.InputDevices;
using XRNode = UnityEngine.XR.XRNode;

public class MenuController : MonoBehaviour
{
    [Header("Main Menu References")]
    public GameObject menuCanvas;

    [Header("Information Canvas")]
    public GameObject infoCanvas;
    public TextMeshProUGUI infoTitleText;
    public TextMeshProUGUI infoBodyText;

    [Header("Other Menus")]
    public FastTravelMenu fastTravelMenu;
    public ClusterFilterMenu clusterFilterMenu;
    public LandmarkPopup landmarkPopup;

    [Header("Player References")]
    public VRPlayerController vrPlayerController;
    public DesktopPlayerController desktopPlayerController;

    [Header("Link")]
    public string moreUrl = "https://star-formation.com/";

    private bool menuOpen = false;
    private bool fastTravelWasOpen = false;

    private bool previousMenuButton = false;

    private void Start()
    {
        menuOpen = false;
        fastTravelWasOpen = false;

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        ClearCurrentSelection();
        RestoreSceneAfterMenu();
    }

    private void Update()
    {
        HandleKeyboardInput();
        HandleVRInput();
        MonitorFastTravel();
    }

    private void LateUpdate()
    {
        if (EventSystem.current == null)
            return;

        bool clearSelection =
            (menuCanvas != null && menuCanvas.activeInHierarchy) ||
            (infoCanvas != null && infoCanvas.activeInHierarchy) ||
            (clusterFilterMenu != null && clusterFilterMenu.IsOpen());

        if (!clearSelection)
            return;

        GameObject selected =
            EventSystem.current.currentSelectedGameObject;

        if (selected == null)
            return;

        // Keep input fields selected so they retain focus.
        if (selected.GetComponentInParent<TMP_InputField>() != null)
            return;

        // Clear selection from other UI elements.
        ClearCurrentSelection();
    }

    private void ClearCurrentSelection()
    {
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    private void MonitorFastTravel()
    {
        bool fastTravelActive =
            fastTravelMenu != null &&
            fastTravelMenu.IsOpen();

        if (fastTravelWasOpen &&
            !fastTravelActive &&
            !menuOpen)
        {
            ClearCurrentSelection();
            RestoreSceneAfterMenu();
        }

        fastTravelWasOpen = fastTravelActive;
    }

    private void HandleKeyboardInput()
    {
        Keyboard keyboard = Keyboard.current;

        if (keyboard == null)
            return;

        if (keyboard.escapeKey.wasPressedThisFrame ||
            keyboard.mKey.wasPressedThisFrame)
        {
            HandleMenuKeyboardToggle();
        }
    }

    private void HandleMenuKeyboardToggle()
    {
        // Information / Controls
        if (infoCanvas != null && infoCanvas.activeSelf)
        {
            CloseInfoMenu();
            RestoreSceneAfterMenu();
            return;
        }

        // Cluster Filter
        if (clusterFilterMenu != null &&
            clusterFilterMenu.IsOpen())
        {
            clusterFilterMenu.CloseMenu();
            ClearCurrentSelection();
            RestoreSceneAfterMenu();
            return;
        }

        // Fast Travel
        if (fastTravelMenu != null &&
            fastTravelMenu.IsOpen())
        {
            fastTravelMenu.CloseMenu();
            ClearCurrentSelection();
            RestoreSceneAfterMenu();
            return;
        }

        // Landmark Popup
        if (landmarkPopup != null &&
            landmarkPopup.gameObject.activeSelf)
        {
            landmarkPopup.Hide();
            ClearCurrentSelection();
            RestoreSceneAfterMenu();
            return;
        }

        // Main Menu
        if (menuOpen)
            CloseMenu();
        else
            OpenMenu();
    }

    private void HandleVRInput()
    {
        XRInputDevice leftHand =
            XRInputDevices.GetDeviceAtXRNode(XRNode.LeftHand);

        bool menuButton = false;
        bool leftHandValid = leftHand.isValid;

        if (leftHandValid)
        {
            // Left controller Menu button.
            leftHand.TryGetFeatureValue(
                XRCommonUsages.menuButton,
                out menuButton);
        }

        bool menuPressed =
            menuButton && !previousMenuButton;

        previousMenuButton = menuButton;

        // Keep the Menu button functionality.
        if (menuPressed)
        {
            HandleMenuKeyboardToggle();
            return;
        }

        // Information screens do not use stick navigation.
        if (infoCanvas != null && infoCanvas.activeSelf)
            return;

        // Preserve Fast Travel's existing left-stick navigation.
        if (fastTravelMenu != null && fastTravelMenu.IsOpen())
        {
            if (leftHandValid)
            {
                Vector2 leftStick = Vector2.zero;

                leftHand.TryGetFeatureValue(
                    XRCommonUsages.primary2DAxis,
                    out leftStick);

                fastTravelMenu.HandleStickNavigation(leftStick);
            }

            return;
        }

        // No manual X-button selection.
        // Controller pointing, hovering, and clicking are handled by the UI.
    }

    private void OpenMenu()
    {
        menuOpen = true;
        fastTravelWasOpen = false;

        ClearCurrentSelection();

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        if (menuCanvas != null)
            menuCanvas.SetActive(true);

        if (vrPlayerController != null)
            vrPlayerController.enabled = false;

        if (desktopPlayerController != null)
            desktopPlayerController.enabled = false;

        Canvas.ForceUpdateCanvases();
    }

    private void CloseMenu()
    {
        menuOpen = false;

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        ClearCurrentSelection();
        RestoreSceneAfterMenu();
    }

    private void RestoreSceneAfterMenu()
    {
        if (vrPlayerController != null)
            vrPlayerController.enabled = true;

        if (desktopPlayerController != null)
            desktopPlayerController.enabled = true;
    }

    public void ShowControls()
    {
        string controls = "";

        if (desktopPlayerController != null)
        {
            controls += "Desktop:\n" + desktopPlayerController.GetControls();
        }

        if (vrPlayerController != null)
        {
            if (!string.IsNullOrEmpty(controls))
                controls += "\n\n";

            controls += "VR:\n" + vrPlayerController.GetControls();
        }

        OpenInfoMenu("Controls", controls);
    }

    public void ShowAbout()
    {
        OpenInfoMenu(
            "About",
            "This visualization uses the publicly " +
            "available Edenhofer et al. (2024) " +
            "three-dimensional dust map of the " +
            "local Milky Way.\n\n" +
            "The map traces interstellar dust near " +
            "the Sun. In this VR version, dust " +
            "density is represented by " +
            "semi-transparent particles so users " +
            "can explore nearby Galactic dust " +
            "structure from inside the data."
        );
    }

    private void OpenInfoMenu(string title, string body)
    {
        if (infoCanvas == null)
        {
            Debug.LogError(
                "MenuController: Information Canvas is not assigned."
            );
            return;
        }

        menuOpen = false;

        ClearCurrentSelection();

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoTitleText != null)
            infoTitleText.text = title;

        if (infoBodyText != null)
            infoBodyText.text = body;

        infoCanvas.SetActive(true);
        Canvas.ForceUpdateCanvases();
    }

    private void CloseInfoMenu()
    {
        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        ClearCurrentSelection();
    }

    public void OpenFastTravel()
    {
        if (fastTravelMenu == null)
        {
            Debug.LogError(
                "MenuController: Fast Travel Menu is not assigned."
            );
            return;
        }

        menuOpen = false;

        ClearCurrentSelection();

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        fastTravelWasOpen = true;
        fastTravelMenu.OpenMenu();
    }

    public void OpenClusterFilter()
    {
        if (clusterFilterMenu == null)
        {
            Debug.LogError(
                "MenuController: Cluster Filter Menu is not assigned."
            );
            return;
        }

        menuOpen = false;

        ClearCurrentSelection();

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        clusterFilterMenu.OpenMenu();
    }

    public void OpenLink()
    {
        Application.OpenURL(moreUrl);
    }

    public void Quit()
    {
        Application.Quit();
    }
}