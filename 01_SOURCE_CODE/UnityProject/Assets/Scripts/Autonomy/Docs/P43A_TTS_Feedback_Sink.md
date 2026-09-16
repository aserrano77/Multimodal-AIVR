# P43A TTS feedback sink

P43A adds an audible text-to-speech output for the existing structured robot voice feedback.

The TTS layer is only a sink. It does not create new voice commands, does not change ASR, normalization, intent mapping, P40 replacement/deferred-order behavior, P41 autonomous policy, Pick & Place, navigation, or experiment condition decisions.

## Integration

`VoiceAutonomyCommandRouter` still emits `RobotVoiceFeedbackMessage` through `IRobotVoiceFeedbackSink`.

`StructuredTtsFeedbackSink` implements that sink and can be assigned to `VoiceAutonomyCommandConnector` through the existing structured robot feedback sink field. If a `StructuredTtsFeedbackSink` is present on the same GameObject, the connector can also resolve it automatically.

The diagnostic recorder remains independent. `VoiceAutonomyCommandConnector` still wraps the primary sink with `RobotVoiceFeedbackDiagnosticRecorder` when diagnostics are enabled, so CSV/JSONL/MD exports are preserved.

## Backend

The default real backend is local Windows SAPI, accessed through reflection over `System.Speech.Synthesis.SpeechSynthesizer`.

If SAPI or the requested voice is unavailable, the sink logs a warning and does not block robot execution. Diagnostic no-speech mode can be enabled for development machines where audible TTS should not be played.

No cloud service is used.

## Diagnostics and direct test

`StructuredTtsFeedbackSink` emits explicit TTS telemetry so a silent run can be diagnosed from the console and `events.jsonl`.

Key events include:

- `tts_sink_awake`
- `tts_sink_configured`
- `tts_backend_init_started`
- `tts_backend_init_succeeded`
- `tts_backend_init_failed`
- `tts_feedback_received`
- `tts_condition_gate_evaluated`
- `tts_message_formatted`
- `tts_message_enqueued`
- `tts_message_dropped`
- `tts_backend_unavailable`
- `tts_speak_requested`
- `tts_speak_completed`
- `tts_speak_failed`
- `tts_debug_speak_test_requested`
- `tts_prespeech_silence_applied`
- `tts_ssml_speak_requested`
- `tts_ssml_fallback_used`

The sink also exposes a `Debug Speak Test` context menu and a `debugSpeakTestNow` inspector trigger. In Play Mode this bypasses ASR, mapping, P40/P41, and Pick & Place, and sends `Prueba de voz del robot.` directly to the configured TTS backend. If the backend is not usable, the diagnostic event names and payload fields identify whether the issue is condition gating, queueing, diagnostic no-speech mode, backend unavailability, or a SAPI exception.

## Message formatting

`RobotVoiceFeedbackTtsFormatter` turns structured feedback into closed Spanish templates for the user. It uses only existing fields such as feedback kind, box alias, destination, current task text, and reason. Missing data degrades safely.

Examples:

- `OrderAccepted`, box `A1`, destination `SELF`: `Orden aceptada. Voy a por la caja A1.`
- `TargetChanged`: `Orden aceptada: cambio de objetivo y voy a llevar la caja A1 a su zona de deposito.`
- `CommandQueuedAsPending`, current box `B2`: `Orden recibida: la ejecutare cuando termine de depositar la caja B2.`
- `CommandRejectedTargetNotFound`, box `A1`: `Orden rechazada. No encuentro esa caja.`
- `RobotBusy`, current box `B2`: `Orden rechazada. Estoy ocupado.`
- `PendingCommandCancelled`: `Orden cancelada: detengo la orden pendiente.`
- missing box/destination: `Orden aceptada. Voy a por la caja solicitada.`

Internal names such as Behavior Tree, blackboard, P40/P41, enum names, class names, and debug identifiers should not be exposed in spoken text.

## Queue and spam protection

`TtsFeedbackQueue` preserves message order while avoiding overlap:

- only one message is sent to the backend when it is idle;
- identical messages inside the cooldown window are dropped;
- if the queue is full, normal messages are dropped before critical messages;
- critical messages include rejection, cancellation, deferred/pending orders, and stop/continue acknowledgements.

## Conditions

By default the sink only speaks when a resolved condition provider says both robot and voice are enabled, which matches C11. C10 and C00 are silent by default. If no condition provider is available, the sink remains usable for isolated diagnostics.

## P43B Spanish voice selection

P43B keeps the local `powershell_sapi` backend and adds explicit voice selection for Spanish output.

`StructuredTtsFeedbackSink` exposes:

- `preferredCulture`, default `es-ES`;
- `preferredVoiceName`, optional exact Windows voice name;
- `preferredVoiceGender`, optional filter when multiple voices match the culture;
- `voiceRate`, default `-1`;
- `voiceVolume`, default `100`;
- `logInstalledVoicesOnAwake`;
- `requirePreferredCulture`, default `false`.

When `powershell_sapi` is used, the PowerShell script loads `System.Speech`, enumerates installed voices, tries `preferredVoiceName` first, then tries voices compatible with `preferredCulture`, and finally falls back to the Windows default voice if no Spanish voice is installed and `requirePreferredCulture` is false.

If `preferredVoiceName` is set, the backend tries that exact installed SAPI voice first. If it is missing, the backend emits `tts_preferred_voice_not_found` and continues with the culture/gender fallback unless `requirePreferredCulture` makes the configuration fatal.

Diagnostic events:

- `tts_installed_voice_detected`: installed voice name, culture, gender, and age;
- `tts_voice_selection_started`: requested culture/name/gender;
- `tts_preferred_voice_not_found`: exact requested voice name is not installed or not enabled;
- `tts_voice_selected`: selected voice, culture, gender, age, and `selection_mode` (`exact_name`, `culture_match`, `culture_gender_match`, or `default_fallback`);
- `tts_voice_selection_failed`: explicit selection failure reason;
- `tts_voice_default_fallback_used`: Windows default voice was used because no preferred voice was available.

The component context menu provides `List Installed TTS Voices` and `Debug Speak Spanish Test`. The editor utility under `Tools/TFG/P43B` exposes the same diagnostics for batch runs.

The Unity Inspector includes a `Simple SAPI voice selection` panel:

- `Refresh installed SAPI voices` lists voices from local Windows SAPI.
- `Installed voice` is a popup with labels such as `Microsoft David Desktop | en-US | Male | Adult`.
- `Apply selected voice` writes the exact selected voice name to `preferredVoiceName`, records Undo, marks the component dirty, and marks the scene dirty when the component belongs to a scene.
- `Clear exact voice and use culture/gender fallback` empties `preferredVoiceName`, returning runtime selection to `preferredCulture` and optional `preferredVoiceGender`.
- `Test selected voice` speaks with the highlighted popup voice without needing to apply it.
- `Test currently applied voice` speaks with the voice that the current serialized configuration will use.

Exact `preferredVoiceName` has priority over culture and gender. This means `Microsoft David Desktop` can be selected explicitly even though it is `en-US`; Spanish text may then be pronounced with an English accent. Use that only as a diagnostic or if the experimental setup intentionally wants that installed voice. If `preferredVoiceName` is empty, the sink falls back to `preferredCulture=es-ES`, then optional gender, then Windows default if allowed.

This backend depends on voices installed in Windows. If no `es-ES` voice exists, Windows may speak Spanish text with an English/default voice; that is logged as `tts_voice_default_fallback_used` and is not a runtime failure. The naturalness of local SAPI voices is limited and accepted for the no-cloud P43B backend.

## P43C pre-speech silence and intelligibility

P43C keeps the same local `powershell_sapi` backend and adds a short silent lead-in before each spoken message to avoid audible clipping of the first phonemes. The default scene configuration uses `preSpeechSilenceMs=350` and `useSsmlForPowershellSapi=true`.

When SSML is enabled, the PowerShell backend calls `SpeakSsml` with an initial `<break>` and the selected Spanish voice. If SSML fails on a local machine, the script falls back to normal `Speak(text)` and emits `tts_ssml_fallback_used`. The diagnostic event `tts_prespeech_silence_applied` records the configured silence, backend mode, and TTS source.

The most frequent spoken messages were shortened for intelligibility while preserving the same structured feedback decision:

- accepted: `Orden aceptada. Voy a por la caja A1.`
- target not found: `Orden rechazada. No encuentro esa caja.`
- busy: `Orden rechazada. Estoy ocupado.`

The voice remains a local Windows SAPI voice such as `Microsoft Helena Desktop`. Its naturalness is limited compared with neural/cloud TTS, and that limitation is accepted to keep the experiment local, offline, and reproducible.
