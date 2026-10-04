using TMPro;
using UnityEngine;

using InputActionReference =
    UnityEngine.InputSystem.InputActionReference;

using Keyboard =
    UnityEngine.InputSystem.Keyboard;

using XRInputDevice =
    UnityEngine.XR.InputDevice;

using XRInputDevices =
    UnityEngine.XR.InputDevices;

using XRNode =
    UnityEngine.XR.XRNode;

using XRCommonUsages =
    UnityEngine.XR.CommonUsages;

public class VRMenuController : MonoBehaviour
{
    [Header("Menu References")]
    public GameObject menuCanvas;
    public TextMeshProUGUI titleText;
    public TextMeshProUGUI bodyText;
    public RectTransform bodyRect;
    public TextMeshProUGUI optionText;

    public FastTravelMenu fastTravelMenu;
    public ClusterFilterMenu clusterFilterMenu;

    [Header("Controller References")]
    public VRPlayerController vrPlayerController;
    public DesktopPlayerController desktopPlayerController;

    [Header("Link")]
    public string moreUrl = "https://star-formation.com/";

    [Header("Optional Input Actions")]
    [Tooltip(
        "Input action for the left-controller Y button. " +
        "Direct XR polling is also used as a fallback."
    )]
    public InputActionReference menuToggleAction;

    [Tooltip(
        "Input action for the left-controller X button. " +
        "Direct XR polling is also used as a fallback."
    )]
    public InputActionReference menuSelectAction;

    private bool menuOpen = false;
    private bool stickReleased = true;

    private bool previousX = false;
    private bool previousY = false;

    private int selectedIndex = 0;

    private readonly string[] options =
    {
        "Controls",
        "About",
        "Fast Travel",
        "Cluster Filter",
        "Show Me More",
        "Quit"
    };

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

        if (menuOpen)
        {
            RestoreSceneAfterMenu();
        }
    }

    private void Start()
    {
        menuOpen = false;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
        }

        if (vrPlayerController != null)
        {
            vrPlayerController.enabled = true;
        }

        if (desktopPlayerController != null)
        {
            desktopPlayerController.enabled = true;
        }
    }

    private void Update()
    {
        HandleKeyboardInput();
        HandleQuestInput();
    }

    private void HandleKeyboardInput()
    {
        Keyboard keyboard = Keyboard.current;

        if (keyboard == null)
        {
            return;
        }

        if (keyboard.mKey.wasPressedThisFrame)
        {
            ToggleMenu();
        }

        if (!menuOpen)
        {
            return;
        }

        if (keyboard.upArrowKey.wasPressedThisFrame)
        {
            selectedIndex--;

            selectedIndex = Mathf.Clamp(
                selectedIndex,
                0,
                options.Length - 1
            );

            ShowMainMenu();
        }

        if (keyboard.downArrowKey.wasPressedThisFrame)
        {
            selectedIndex++;

            selectedIndex = Mathf.Clamp(
                selectedIndex,
                0,
                options.Length - 1
            );

            ShowMainMenu();
        }

        if (keyboard.enterKey.wasPressedThisFrame)
        {
            SelectOption();
        }
    }

    private void HandleQuestInput()
    {
        XRInputDevice leftHand =
            XRInputDevices.GetDeviceAtXRNode(
                XRNode.LeftHand
            );

        bool buttonX = false;
        bool buttonY = false;
        Vector2 leftStick = Vector2.zero;

        leftHand.TryGetFeatureValue(
            XRCommonUsages.primaryButton,
            out buttonX
        );

        leftHand.TryGetFeatureValue(
            XRCommonUsages.secondaryButton,
            out buttonY
        );

        leftHand.TryGetFeatureValue(
            XRCommonUsages.primary2DAxis,
            out leftStick
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
         * Y BUTTON
         */
        if (yPressed)
        {
            /*
             * Cluster Filter is open:
             * return to main menu.
             */
            if (clusterFilterMenu != null &&
                clusterFilterMenu.IsOpen())
            {
                clusterFilterMenu.CloseMenu();

                if (menuCanvas != null)
                {
                    menuCanvas.SetActive(true);
                }

                menuOpen = true;

                selectedIndex = 3;

                stickReleased = true;

                ShowMainMenu();
            }
            /*
             * Fast Travel is open:
             * return to main menu.
             */
            else if (fastTravelMenu != null &&
                     fastTravelMenu.IsOpen())
            {
                fastTravelMenu.CloseMenu();

                if (menuCanvas != null)
                {
                    menuCanvas.SetActive(true);
                }

                menuOpen = true;

                selectedIndex = 2;

                stickReleased = true;

                ShowMainMenu();
            }
            /*
             * Otherwise toggle main menu.
             */
            else
            {
                ToggleMenu();
            }
        }

        /*
         * MAIN MENU
         */
        if (menuOpen)
        {
            HandleStickNavigation(leftStick);

            if (xPressed)
            {
                SelectOption();
            }
        }
        /*
         * FAST TRAVEL
         */
        else if (fastTravelMenu != null &&
                 fastTravelMenu.IsOpen())
        {
            fastTravelMenu.HandleStickNavigation(leftStick);

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
         * Cluster Filter is handled by the Unity UI/EventSystem.
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

        selectedIndex = 0;
        stickReleased = true;

        ShowMainMenu();

        Canvas.ForceUpdateCanvases();
    }

    private void CloseMenu()
    {
        menuOpen = false;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
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

    private void HandleStickNavigation(
        Vector2 stick
    )
    {
        if (Mathf.Abs(stick.y) < 0.3f)
        {
            stickReleased = true;
            return;
        }

        if (!stickReleased)
        {
            return;
        }

        if (stick.y > 0.3f)
        {
            selectedIndex--;
        }
        else if (stick.y < -0.3f)
        {
            selectedIndex++;
        }

        selectedIndex = Mathf.Clamp(
            selectedIndex,
            0,
            options.Length - 1
        );

        stickReleased = false;

        ShowMainMenu();
    }

    private void ShowMainMenu()
    {
        if (titleText != null)
        {
            titleText.text =
                "Dust Cloud Viewer";
        }

        if (optionText != null)
        {
            optionText.text = "";

            for (
                int i = 0;
                i < options.Length;
                i++
            )
            {
                if (i == selectedIndex)
                {
                    optionText.text +=
                        "> " +
                        options[i] +
                        "\n";
                }
                else
                {
                    optionText.text +=
                        "  " +
                        options[i] +
                        "\n";
                }
            }
        }

        if (bodyText != null)
        {
            bodyText.text =
                "Y: close menu\n" +
                "X: select\n" +
                "Left stick: choose option";
        }
    }

    private void SelectOption()
    {
        switch (selectedIndex)
        {
            case 0:
                ShowControls();
                break;

            case 1:
                ShowAbout();
                break;

            case 2:
                OpenFastTravel();
                break;

            case 3:
                OpenClusterFilter();
                break;

            case 4:
                Application.OpenURL(moreUrl);
                break;

            case 5:
                Application.Quit();
                break;
        }
    }

    private void ShowControls()
    {
        if (titleText != null)
        {
            titleText.text = "Controls";
        }

        if (optionText != null)
        {
            optionText.text = "";
        }

        if (bodyText != null)
        {
            bodyText.text =
                @"Left Stick: move / turn
Right Stick: strafe / rise / descend
X: increase speed
Y: decrease speed
A: quality / performance particles
B: show / hide landmarks
Left Trigger: next color scheme
Left Grip: cycle particle thickness
Right Trigger: select landmark
Right Grip: close landmark information
Y: open / close menu
X: select";
        }
    }

    private void ShowAbout()
    {
        if (titleText != null)
        {
            titleText.text = "About";
        }

        if (optionText != null)
        {
            optionText.text = "";
        }

        if (bodyText != null)
        {
            bodyText.text =
                "This visualization uses the publicly " +
                "available Edenhofer et al. (2024) " +
                "three-dimensional dust map of the " +
                "local Milky Way.\n\n" +
                "The map traces interstellar dust near " +
                "the Sun. In this VR version, dust " +
                "density is represented by " +
                "semi-transparent particles so users " +
                "can explore nearby Galactic dust " +
                "structure from inside the data.";
        }
    }

    private void OpenFastTravel()
    {
        if (fastTravelMenu == null)
        {
            Debug.LogError(
                "VRMenuController: " +
                "Fast Travel Menu is not assigned."
            );

            return;
        }

        menuOpen = false;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
        }

        fastTravelMenu.OpenMenu();
    }

    private void OpenClusterFilter()
    {
        if (clusterFilterMenu == null)
        {
            Debug.LogError(
                "VRMenuController: " +
                "Cluster Filter Menu is not assigned."
            );

            return;
        }

        menuOpen = false;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
        }

        clusterFilterMenu.OpenMenu();
    }
}