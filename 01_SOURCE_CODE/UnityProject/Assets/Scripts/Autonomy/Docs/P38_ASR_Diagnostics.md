# P38 ASR diagnostics

## Activation

1. Select the GameObject that owns `VoiceRecognitionController`.
2. Enable `ASR Diagnostic Mode / Enable Asr Diagnostic Mode`.
3. Optional: add or use the auto-created `AsrDiagnosticRecorder` on the same GameObject.
4. Optional for controlled tests: fill `Expected Utterances` in `AsrDiagnosticRecorder`.
5. Optional for the standard P38 command battery: enable `Use Default P38 Expected Utterances` in `AsrDiagnosticRecorder`.
6. Leave the runtime ASR model unchanged unless `Diagnostic Override Whisper Model` is explicitly enabled.

`Diagnostic Override Whisper Model` supports `tiny`, `base`, and `small` through these StreamingAssets paths:

- `Whisper/ggml-tiny.bin`
- `Whisper/ggml-base.bin`
- `Whisper/ggml-small.bin`

Use `Tools/ASR Diagnostics/Report Whisper Model Availability` to check which files exist.

When `Diagnostic Override Whisper Model` is enabled, the override is applied before the Whisper model is loaded. If the selected model cannot be resolved or Whisper has already started loading, the diagnostic run is disabled instead of producing artefacts labelled with the wrong model. The console warning includes `requested_model_size`, `resolved_model_path`, `reason`, and `diagnostic_disabled=true`.

## Command battery

Record one utterance per phrase:

- lleva la caja A1 a su zona
- lleva A2 a su zona
- mueve la caja B1
- deposita la caja C1
- coge la caja mas cercana
- espera
- continua
- cancela la orden pendiente
- mas cercana a mi
- mas cercana al robot

## Outputs

Runs are written to:

`Logs/AsrDiagnostics/<run_id>/`

Each run contains:

- `utterances/utterance_001.wav`, etc. when WAV saving is enabled and available
- `asr_diagnostics_<run_id>_<model_size>.csv`
- `asr_diagnostics_<run_id>_<model_size>.jsonl`
- `asr_summary_<run_id>_<model_size>.md`

Canonical duplicate names are intentionally not generated. CSV and JSONL include `run_id`, `requested_model_size`, `effective_model_size`, `effective_model_file_name`, `effective_model_path`, `model_available_at_start`, `model_fallback_used`, `model_override_failed_reason`, and `diagnostic_model_override_enabled`. The Markdown summary repeats the run and effective model metadata in its header.

## Metrics

The summary reports utterance count, mean duration, empty transcripts, low confidence transcripts when confidence is available, intent/alias/destination recognition counts, mean and p95 voice pipeline latency, frequent errors, and expected-value accuracy for transcript, normalized text, intent, alias, and destination.

If no expected utterance list is configured, expected-value metrics remain `n/a` and the summary writes `expected_values_note: no expected utterance set configured`.

## Model comparison

Run the same command battery once per available model, enabling `Diagnostic Override Whisper Model` only for the diagnostic run. Then compare the generated `asr_summary_<run_id>_<model_size>.md` files. `Tools/ASR Diagnostics/Evaluate ASR Diagnostics Folder` can locate the traced diagnostics CSV inside a run folder and regenerate a traced summary.

Recommended threshold before using a model in C11:

- intent accuracy >= 90%
- alias accuracy >= 85-90%
- perceived latency ideally < 1500-2000 ms from phrase end to feedback

If aliases such as `A1` become `a uno` in `raw_transcript`, the likely issue is ASR/model quality. If `raw_transcript` is correct but `target_alias` is empty or wrong, inspect normalizer/mapper behavior.
