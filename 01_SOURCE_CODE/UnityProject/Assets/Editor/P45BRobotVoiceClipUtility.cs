using Autonomy.UnityIntegration;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class P45BRobotVoiceClipUtility
{
    private const string ManifestAssetPath = "Assets/Resources/RobotVoice/robot_voice_clip_manifest.json";

    [MenuItem("Tools/TFG/P45B/Validate Robot Voice Clip Manifest")]
    public static void ValidateRobotVoiceClipManifest()
    {
        TextAsset manifestAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestAssetPath);
        if (manifestAsset == null)
        {
            Debug.LogError($"[P45B] Robot voice clip manifest missing: {ManifestAssetPath}");
            return;
        }

        RobotVoiceClipManifest manifest;
        try
        {
            manifest = JsonUtility.FromJson<RobotVoiceClipManifest>(manifestAsset.text);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[P45B] Robot voice clip manifest invalid: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        RobotVoiceClipEntry[] entries = manifest?.clips ?? Array.Empty<RobotVoiceClipEntry>();
        HashSet<string> referencedAssetPaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> keys = new(StringComparer.Ordinal);
        int missingText = 0;
        int missingClip = 0;
        int duplicateKeys = 0;

        foreach (RobotVoiceClipEntry entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.text))
            {
                missingText++;
                continue;
            }

            string key = entry.EffectiveKey;
            if (!keys.Add(key))
            {
                duplicateKeys++;
                Debug.LogWarning($"[P45B] Duplicate robot voice clip key '{key}' for text '{entry.text}'.");
            }

            string assetPath = ToAudioAssetPath(entry.resourcePath);
            referencedAssetPaths.Add(assetPath);
            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
            if (clip == null)
            {
                missingClip++;
                Debug.LogWarning($"[P45B] Missing AudioClip for '{entry.text}' | path={assetPath}");
            }
        }

        int orphanClipCount = 0;
        string audioRoot = "Assets/Resources/RobotVoice/es-ES";
        if (Directory.Exists(audioRoot))
        {
            foreach (string wavPath in Directory.GetFiles(audioRoot, "*.wav", SearchOption.TopDirectoryOnly))
            {
                string normalizedPath = wavPath.Replace('\\', '/');
                if (!referencedAssetPaths.Contains(normalizedPath))
                {
                    orphanClipCount++;
                    Debug.LogWarning($"[P45B] AudioClip not referenced by manifest: {normalizedPath}");
                }
            }
        }

        Debug.Log(
            $"[P45B] Robot voice clip validation | entries={entries.Length} missing_text={missingText} missing_clips={missingClip} duplicate_keys={duplicateKeys} orphan_clips={orphanClipCount}");
    }

    private static string ToAudioAssetPath(string resourcePath)
    {
        string cleaned = (resourcePath ?? string.Empty).Trim().TrimStart('/', '\\').Replace('\\', '/');
        return $"Assets/Resources/{cleaned}.wav";
    }
}
