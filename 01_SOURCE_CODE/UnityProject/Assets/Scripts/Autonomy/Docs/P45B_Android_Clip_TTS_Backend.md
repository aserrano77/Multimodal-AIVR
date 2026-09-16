# P45B Android clip TTS backend

P45B-02 adds a local `AudioClip` backend for structured robot voice feedback on Android/Quest. It does not add runtime speech synthesis, cloud TTS, neural TTS, or a new voice-command path.

The spoken text remains owned by `RobotVoiceFeedbackTtsFormatter`. The clip backend receives the final formatted text, normalizes it deterministically, resolves it through `Resources/RobotVoice/robot_voice_clip_manifest.json`, and plays the matching `AudioClip` with Unity `AudioSource`.

Backend selection is explicit through `StructuredTtsFeedbackSink`:

- `Auto`: Windows Editor/Standalone keeps the existing Windows SAPI backend; Android runtime uses local clips.
- `WindowsSapi`: forces the existing PC-VR/Editor backend.
- `AudioClips`: forces local clips for Editor validation or Android.
- `DiagnosticNoSpeech`: keeps the existing silent diagnostic path.

Android/Quest must not call System.Speech, SAPI COM, PowerShell, external processes, Windows paths, or cloud services. If a clip is missing, the backend emits `tts_audio_clip_missing`/`tts_audio_clip_reference_missing`, does not block robot execution, and the existing sink records `tts_speak_failed`.

Offline clip generation is in `Tools/TFG/P45B/GenerateRobotVoiceClips.ps1`. It reads `robot_voice_phrases_es-ES.csv`, uses local Windows SAPI, prefers `Microsoft Helena Desktop`, and refuses fallback unless `-AllowFallback` is passed. Existing clips are not overwritten unless `-Force` is passed.

Current limitations:

- Quest standalone is only a candidate after APK/device validation.
- ASR/Whisper remains `RISK` until a real Quest 3 APK test confirms microphone permission, capture, Whisper latency, and recognition quality.
- The clip table covers the closed structured templates and aliases `A1/A2/A3/B1/B2/C1`; new formatter text requires adding clips before Android runs.

P45B-02 does not modify navigation, Pick & Place, slots, NavMesh, carving, logistics geometry, P40/P41, P43F/P43G, P44I, P45A, or the C00/C10/C11 matrix.
