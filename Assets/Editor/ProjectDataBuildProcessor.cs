
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

public sealed class ProjectDataBuildProcessor : BuildPlayerProcessor
{
    public override int callbackOrder => -1000;

    public override void PrepareForBuild(BuildPlayerContext context)
    {
        BuildTarget target = context.BuildPlayerOptions.target;

        bool isDesktop =
            target == BuildTarget.StandaloneWindows64 ||
            target == BuildTarget.StandaloneLinux64 ||
            target == BuildTarget.StandaloneOSX;

        if (!isDesktop)
            return;

        if (Environment.GetEnvironmentVariable(
                "UNITY_AUTOBUILD_COPY_DATA") == "0")
            return;

        string projectRoot = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..")
        );

        string sourceData = Path.Combine(projectRoot, "Data");

        if (!Directory.Exists(sourceData))
        {
            throw new BuildFailedException(
                "Project Data folder not found: " + sourceData
            );
        }

        // Add each top-level directory/file directly to StreamingAssets.
        foreach (string entry in Directory.EnumerateFileSystemEntries(sourceData))
        {
            string destinationName = Path.GetFileName(entry);

            context.AddAdditionalPathToStreamingAssets(
                entry,
                destinationName
            );

            Debug.Log(
                "[ProjectDataBuildProcessor] Included: " + destinationName
            );
        }
    }
}
