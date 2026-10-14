using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using XRCommonUsages = UnityEngine.XR.CommonUsages;
using XRInputDevice = UnityEngine.XR.InputDevice;
using XRInputDevices = UnityEngine.XR.InputDevices;
using XRNode = UnityEngine.XR.XRNode;

public class MenuController : MonoBehaviour
{
    [Header("Main Menu References")]
    public GameObject menuCanvas;
    public Button firstMenuButton;

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

    private bool previousX = false;
    private bool previousMenuButton = false;

    private GameObject lastSelectedButton;

    private void Start()
    {
        menuOpen = false;
        fastTravelWasOpen = false;
        lastSelectedButton = null;

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        RestoreSceneAfterMenu();
    }

    private void Update()
    {
        HandleKeyboardInput();
        HandleVRInput();
        MonitorFastTravel();
    }

    private void MonitorFastTravel()
    {
        bool fastTravelOpen =
            fastTravelMenu != null &&
            fastTravelMenu.IsOpen();

        if (fastTravelWasOpen &&
            !fastTravelOpen &&
            !menuOpen)
        {
            RestoreSceneAfterMenu();
        }

        fastTravelWasOpen = fastTravelOpen;
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
            RestoreSceneAfterMenu();
            return;
        }

        // Fast Travel
        if (fastTravelMenu != null &&
            fastTravelMenu.IsOpen())
        {
            fastTravelMenu.CloseMenu();
            RestoreSceneAfterMenu();
            return;
        }

        // Landmark Popup
        if (landmarkPopup != null &&
            landmarkPopup.gameObject.activeSelf)
        {
            landmarkPopup.Hide();
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

        bool buttonX = false;
        bool menuButton = false;

        bool leftHandValid = leftHand.isValid;

        if (leftHandValid)
        {
            // X button, using the same input feature as VRPlayerController.
            leftHand.TryGetFeatureValue(
                XRCommonUsages.primaryButton,
                out buttonX);

            // Left controller Menu button.
            leftHand.TryGetFeatureValue(
                XRCommonUsages.menuButton,
                out menuButton);
        }

        bool xPressed = buttonX && !previousX;

        bool menuPressed =
            menuButton && !previousMenuButton;

        previousX = buttonX;
        previousMenuButton = menuButton;

        // Menu button: open the main menu or close the current menu/popup.
        if (menuPressed)
        {
            HandleMenuKeyboardToggle();
            return;
        }

        // Don't submit X while an information screen is displayed.
        if (infoCanvas != null && infoCanvas.activeSelf)
            return;

        // Fast Travel has its own stick navigation.
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

            if (xPressed)
                HandleXSelection();

            return;
        }

        // X selects the highlighted button in the main or cluster menu.
        if (xPressed)
            HandleXSelection();
    }

    private void HandleXSelection()
    {
        // Fast Travel: select the highlighted landmark.
        if (fastTravelMenu != null && fastTravelMenu.IsOpen())
        {
            fastTravelMenu.SelectCurrentLandmark();

            menuOpen = false;

            if (menuCanvas != null)
                menuCanvas.SetActive(false);

            RestoreSceneAfterMenu();
            return;
        }

        bool mainMenuIsOpen = menuOpen;

        bool clusterMenuIsOpen =
            clusterFilterMenu != null &&
            clusterFilterMenu.IsOpen();

        if (!mainMenuIsOpen && !clusterMenuIsOpen)
            return;

        SubmitCurrentSelection();
    }

    private void SubmitCurrentSelection()
    {
        if (EventSystem.current == null)
        {
            Debug.LogWarning(
                "MenuController: No EventSystem found.");
            return;
        }

        GameObject selected =
            EventSystem.current.currentSelectedGameObject;

        if ((selected == null || !selected.activeInHierarchy) &&
            menuOpen)
        {
            SelectRememberedButton();

            selected =
                EventSystem.current.currentSelectedGameObject;
        }

        if (selected == null || !selected.activeInHierarchy)
        {
            Debug.LogWarning(
                "MenuController: No menu item is selected.");
            return;
        }

        // Submit through Unity's standard UI event system.
        BaseEventData eventData =
            new BaseEventData(EventSystem.current);

        ExecuteEvents.Execute(
            selected,
            eventData,
            ExecuteEvents.submitHandler);
    }

    private void OpenMenu()
    {
        menuOpen = true;
        fastTravelWasOpen = false;

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        if (menuCanvas != null)
            menuCanvas.SetActive(true);

        if (vrPlayerController != null)
            vrPlayerController.enabled = false;

        if (desktopPlayerController != null)
            desktopPlayerController.enabled = false;

        Canvas.ForceUpdateCanvases();
        SelectRememberedButton();
    }

    private void CloseMenu()
    {
        menuOpen = false;

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        RestoreSceneAfterMenu();
    }

    private void RestoreSceneAfterMenu()
    {
        if (vrPlayerController != null)
            vrPlayerController.enabled = true;

        if (desktopPlayerController != null)
            desktopPlayerController.enabled = true;
    }

    private void RememberCurrentButton()
    {
        if (EventSystem.current == null)
            return;

        GameObject current =
            EventSystem.current.currentSelectedGameObject;

        if (current == null)
            return;

        if (current.GetComponent<Button>() != null)
            lastSelectedButton = current;
    }

    private void SelectRememberedButton()
    {
        if (EventSystem.current == null)
            return;

        GameObject buttonToSelect = lastSelectedButton;

        if (buttonToSelect == null ||
            !buttonToSelect.activeInHierarchy)
        {
            buttonToSelect =
                firstMenuButton != null
                    ? firstMenuButton.gameObject
                    : null;
        }

        if (buttonToSelect == null)
            return;

        EventSystem.current.SetSelectedGameObject(null);
        EventSystem.current.SetSelectedGameObject(buttonToSelect);

        Button button = buttonToSelect.GetComponent<Button>();

        if (button != null)
            button.Select();
    }

    public void ShowControls()
    {
        RememberCurrentButton();

        OpenInfoMenu(
            "Controls",
            @"Desktop:
W / S: forward / backward
A / D: strafe left / right
Space / Shift: move up / down
Mouse: look around
Q / E: roll
= / -: increase / decrease speed
1: next color scheme
2: cycle particle thickness
3: quality / performance particles
6: show / hide landmarks and orientation
7: select landmark
8: show / hide Gaia LOD nodes
Escape / M: open menu / close current menu or popup

VR:
Left Stick: move / turn
Right Stick: strafe / rise / descend
X: increase speed when no menu is open
Y: decrease speed
A: quality / performance particles
B: show / hide landmarks and orientation
Left Trigger: next color scheme
Left Grip: cycle particle thickness
Right Grip: show / hide Gaia LOD nodes
Right Trigger: select landmark
Left Menu Button: open menu / close current menu
X: select highlighted menu item"
        );
    }

    public void ShowAbout()
    {
        RememberCurrentButton();

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
    }

    public void OpenFastTravel()
    {
        RememberCurrentButton();

        if (fastTravelMenu == null)
        {
            Debug.LogError(
                "MenuController: Fast Travel Menu is not assigned."
            );
            return;
        }

        menuOpen = false;

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        fastTravelWasOpen = true;
        fastTravelMenu.OpenMenu();
    }

    public void OpenClusterFilter()
    {
        RememberCurrentButton();

        if (clusterFilterMenu == null)
        {
            Debug.LogError(
                "MenuController: Cluster Filter Menu is not assigned."
            );
            return;
        }

        menuOpen = false;

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        clusterFilterMenu.OpenMenu();
    }

    public void OpenLink()
    {
        RememberCurrentButton();
        Application.OpenURL(moreUrl);
    }

    public void Quit()
    {
        RememberCurrentButton();
        Application.Quit();
    }
}