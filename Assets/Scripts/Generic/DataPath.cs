using System;
using System.IO;
using UnityEngine;

public static class DataPath
{
    public static readonly string Root = Resolve();

    public static string Resolve()
    {
#if UNITY_EDITOR
        return Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "Data")
        );
#elif UNITY_ANDROID
        try
        {
            using (var unityPlayer =
                   new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity =
                   unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var obbDirectory =
                   activity.Call<AndroidJavaObject>("getObbDir"))
            {
                string obbRoot =
                    obbDirectory.Call<string>("getAbsolutePath");

                if (string.IsNullOrEmpty(obbRoot))
                    throw new IOException("OBB path is empty.");

                return Path.Combine(obbRoot, "Data");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("DataPath: " + e);
            return null;
        }
#else
        return Path.Combine(Application.persistentDataPath, "Data");
#endif
    }
}