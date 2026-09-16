using System.IO;
using UnityEditor;
using UnityEngine;

public static class P45E05BSherpaAndroidPluginImportSettings
{
    private static readonly string[] PluginPaths =
    {
        "Assets/Plugins/Android/sherpa-onnx-v1.13.3.aar"
    };

    public static void Apply()
    {
        AssetDatabase.Refresh();

        foreach (string pluginPath in PluginPaths)
        {
            if (!File.Exists(pluginPath))
            {
                Debug.LogWarning($"[P45E05B] p45e05_sherpa_android_runtime_import_settings | missing_plugin='{pluginPath}'");
                continue;
            }

            PluginImporter importer = AssetImporter.GetAtPath(pluginPath) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[P45E05B] p45e05_sherpa_android_runtime_import_settings | importer_missing='{pluginPath}'");
                continue;
            }

            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, true);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, false);
            if (pluginPath.EndsWith(".so", System.StringComparison.OrdinalIgnoreCase))
            {
                importer.SetPlatformData(BuildTarget.Android, "CPU", "ARM64");
            }
            importer.SaveAndReimport();

            FileInfo info = new(pluginPath);
            string eventName = pluginPath.EndsWith(".aar", System.StringComparison.OrdinalIgnoreCase)
                ? "p45e05_sherpa_android_bridge_import_settings"
                : "p45e05_sherpa_android_runtime_import_settings";
            Debug.Log($"[P45E05B] {eventName} | plugin='{pluginPath}' size_bytes={info.Length} platform=Android architecture_abi=arm64-v8a fallback_used=False");
        }
    }
}
