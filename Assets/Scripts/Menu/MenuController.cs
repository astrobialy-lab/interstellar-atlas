using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using XRCommonUsages = UnityEngine.XR.CommonUsages;
using XRInputDevice = UnityEngine.XR.InputDevice;
using XRInputDevices = UnityEngine.XR.InputDevices;
using XRNode = UnityEngine.XR.XRNode;
using InputActionReference =
    UnityEngine.InputSystem.InputActionReference;

public class MenuController : MonoBehaviour
{
    [Header("Main Menu References")]
    public GameObject menuCanvas;

    [Tooltip("The Controls button. Used when opening the menu for the first time.")]
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

    [Header("VR Input Actions")]
    public InputActionReference menuToggleAction;
    public InputActionReference menuSelectAction;

    private bool menuOpen = false;
    private bool fastTravelWasOpen = false;

    private bool previousX = false;
    private bool previousY = false;

    private GameObject lastSelectedButton;

    private void OnEnable()
    {
        if (menuToggleAction != null)
        {
            menuToggleAction.action.Enable();
        }

        if (menuSelectAction != null)
        {
            menuSelectAction.action.Enable();
        }
    }

    private void OnDisable()
    {
        if (menuToggleAction != null)
        {
            menuToggleAction.action.Disable();
        }

        if (menuSelectAction != null)
        {
            menuSelectAction.action.Disable();
        }

        RestoreSceneAfterMenu();
    }

    private void Start()
    {
        menuOpen = false;
        fastTravelWasOpen = false;
        lastSelectedButton = null;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
        }

        if (infoCanvas != null)
        {
            infoCanvas.SetActive(false);
        }

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
        {
            return;
        }

        /*
         * ESCAPE / M
         */
        if (keyboard.escapeKey.wasPressedThisFrame ||
            keyboard.mKey.wasPressedThisFrame)
        {
            HandleMenuKeyboardToggle();
            return;
        }

        /*
         * CLUSTER FILTER
         */
        if (clusterFilterMenu != null &&
            clusterFilterMenu.IsOpen())
        {
            return;
        }

        /*
         * INFORMATION
         */
        if (infoCanvas != null &&
            infoCanvas.activeSelf)
        {
            return;
        }
    }

    private void HandleMenuKeyboardToggle()
    {
        // INFORMATION / CONTROLS
        if (infoCanvas != null &&
            infoCanvas.activeSelf)
        {
            CloseInfoMenu();
            RestoreSceneAfterMenu();
            return;
        }

        // CLUSTER FILTER
        if (clusterFilterMenu != null &&
            clusterFilterMenu.IsOpen())
        {
            clusterFilterMenu.CloseMenu();
            RestoreSceneAfterMenu();
            return;
        }

        // FAST TRAVEL
        if (fastTravelMenu != null &&
            fastTravelMenu.IsOpen())
        {
            fastTravelMenu.CloseMenu();
            RestoreSceneAfterMenu();
            return;
        }

        // LANDMARK POPUP
        if (landmarkPopup != null &&
            landmarkPopup.gameObject.activeSelf)
        {
            landmarkPopup.Hide();
            RestoreSceneAfterMenu();
            return;
        }

        // MAIN MENU
        if (menuOpen)
        {
            CloseMenu();
        }
        else
        {
            OpenMenu();
        }
    }

    private void HandleVRInput()
    {
        XRInputDevice leftHand =
            XRInputDevices.GetDeviceAtXRNode(
                XRNode.LeftHand
            );

        bool buttonX = false;
        bool buttonY = false;

        leftHand.TryGetFeatureValue(
            XRCommonUsages.primaryButton,
            out buttonX
        );

        leftHand.TryGetFeatureValue(
            XRCommonUsages.secondaryButton,
            out buttonY
        );

        bool xPressedDirectly =
            buttonX && !previousX;

        bool yPressedDirectly =
            buttonY && !previousY;

        bool xPressedByAction =
            menuSelectAction != null &&
            menuSelectAction.action.WasPressedThisFrame();

        bool yPressedByAction =
            menuToggleAction != null &&
            menuToggleAction.action.WasPressedThisFrame();

        bool xPressed =
            xPressedDirectly || xPressedByAction;

        bool yPressed =
            yPressedDirectly || yPressedByAction;

        /*
         * Y = OPEN / CLOSE MENU
         */
        if (yPressed)
        {
            // INFORMATION / CONTROLS
            if (infoCanvas != null &&
                infoCanvas.activeSelf)
            {
                CloseInfoMenu();
                RestoreSceneAfterMenu();
            }
            // CLUSTER FILTER
            else if (clusterFilterMenu != null &&
                     clusterFilterMenu.IsOpen())
            {
                clusterFilterMenu.CloseMenu();
                RestoreSceneAfterMenu();
            }
            // FAST TRAVEL
            else if (fastTravelMenu != null &&
                     fastTravelMenu.IsOpen())
            {
                fastTravelMenu.CloseMenu();
                RestoreSceneAfterMenu();
            }
            // LANDMARK POPUP
            else if (landmarkPopup != null &&
                     landmarkPopup.gameObject.activeSelf)
            {
                landmarkPopup.Hide();
                RestoreSceneAfterMenu();
            }
            // MAIN MENU
            else
            {
                ToggleMenu();
            }
        }

        /*
         * INFORMATION
         */
        if (infoCanvas != null &&
            infoCanvas.activeSelf)
        {
            previousX = buttonX;
            previousY = buttonY;
            return;
        }

        /*
         * FAST TRAVEL
         */
        if (fastTravelMenu != null &&
            fastTravelMenu.IsOpen())
        {
            Vector2 leftStick = Vector2.zero;

            leftHand.TryGetFeatureValue(
                XRCommonUsages.primary2DAxis,
                out leftStick
            );

            fastTravelMenu.HandleStickNavigation(
                leftStick
            );

            if (xPressed)
            {
                fastTravelMenu.SelectCurrentLandmark();

                menuOpen = false;

                if (menuCanvas != null)
                {
                    menuCanvas.SetActive(false);
                }

                RestoreSceneAfterMenu();
            }
        }

        /*
         * Main menu is handled by the UI Buttons.
         */

        previousX = buttonX;
        previousY = buttonY;
    }

    private void ToggleMenu()
    {
        if (menuOpen)
        {
            CloseMenu();
        }
        else
        {
            OpenMenu();
        }
    }

    private void OpenMenu()
    {
        menuOpen = true;
        fastTravelWasOpen = false;

        if (infoCanvas != null)
        {
            infoCanvas.SetActive(false);
        }

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(true);
        }

        if (vrPlayerController != null)
        {
            vrPlayerController.enabled = false;
        }

        if (desktopPlayerController != null)
        {
            desktopPlayerController.enabled = false;
        }

        Canvas.ForceUpdateCanvases();

        SelectRememberedButton();
    }

    private void CloseMenu()
    {
        menuOpen = false;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
        }

        if (infoCanvas != null)
        {
            infoCanvas.SetActive(false);
        }

        RestoreSceneAfterMenu();
    }

    private void RestoreSceneAfterMenu()
    {
        if (vrPlayerController != null)
        {
            vrPlayerController.enabled = true;
        }

        if (desktopPlayerController != null)
        {
            desktopPlayerController.enabled = true;
        }
    }

    private void RememberCurrentButton()
    {
        if (EventSystem.current == null)
        {
            return;
        }

        GameObject current =
            EventSystem.current.currentSelectedGameObject;

        if (current == null)
        {
            return;
        }

        Button button =
            current.GetComponent<Button>();

        if (button != null)
        {
            lastSelectedButton = current;
        }
    }

    private void SelectRememberedButton()
    {
        if (EventSystem.current == null)
        {
            return;
        }

        GameObject buttonToSelect =
            lastSelectedButton;

        if (buttonToSelect == null ||
            !buttonToSelect.activeInHierarchy)
        {
            buttonToSelect =
                firstMenuButton != null
                    ? firstMenuButton.gameObject
                    : null;
        }

        if (buttonToSelect == null)
        {
            return;
        }

        EventSystem.current.SetSelectedGameObject(null);
        EventSystem.current.SetSelectedGameObject(
            buttonToSelect
        );

        Button button =
            buttonToSelect.GetComponent<Button>();

        if (button != null)
        {
            button.Select();
        }
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
4 / 5: decrease / increase particle size
6: show / hide landmarks
7: select landmark
Escape / M: open menu / close current menu or popup

VR:
Left Stick: move / turn
Right Stick: strafe / rise / descend
X: increase speed
Y: decrease speed
A: quality / performance particles
B: show / hide landmarks
Left Trigger: next color scheme
Left Grip: cycle particle thickness
Right Trigger: select landmark
Y: open menu / close current menu or popup
X: select"
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

    private void OpenInfoMenu(
        string title,
        string body
    )
    {
        if (infoCanvas == null)
        {
            Debug.LogError(
                "MenuController: Information Canvas " +
                "is not assigned."
            );

            return;
        }

        menuOpen = false;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
        }

        if (infoTitleText != null)
        {
            infoTitleText.text = title;
        }

        if (infoBodyText != null)
        {
            infoBodyText.text = body;
        }

        infoCanvas.SetActive(true);

        Canvas.ForceUpdateCanvases();
    }

    private void CloseInfoMenu()
    {
        if (infoCanvas != null)
        {
            infoCanvas.SetActive(false);
        }
    }

    public void OpenFastTravel()
    {
        RememberCurrentButton();

        if (fastTravelMenu == null)
        {
            Debug.LogError(
                "MenuController: Fast Travel Menu " +
                "is not assigned."
            );

            return;
        }

        menuOpen = false;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
        }

        if (infoCanvas != null)
        {
            infoCanvas.SetActive(false);
        }

        fastTravelWasOpen = true;

        fastTravelMenu.OpenMenu();
    }

    public void OpenClusterFilter()
    {
        RememberCurrentButton();

        if (clusterFilterMenu == null)
        {
            Debug.LogError(
                "MenuController: Cluster Filter Menu " +
                "is not assigned."
            );

            return;
        }

        menuOpen = false;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
        }

        if (infoCanvas != null)
        {
            infoCanvas.SetActive(false);
        }

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