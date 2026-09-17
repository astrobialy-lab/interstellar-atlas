using UnityEngine;

public class LandmarkInfo : MonoBehaviour
{
    public string objectName;
    public string typeCode;
    public string shortDescription;
    [TextArea(3, 8)]
    public string paragraphDescription;
}