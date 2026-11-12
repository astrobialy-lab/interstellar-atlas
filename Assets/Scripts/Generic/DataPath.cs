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
        // DataInstaller extracts the Store-delivered TAR files here before
        // the main simulation scene is loaded.
        return Path.Combine(Application.persistentDataPath, "Data");
#elif UNITY_STANDALONE_OSX || UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX
        return Application.streamingAssetsPath;
#else
        return Path.Combine(Application.persistentDataPath, "Data");
#endif
    }
}
