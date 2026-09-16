param(
    [string]$CsvPath = "Tools/TFG/P45B/robot_voice_phrases_es-ES.csv",
    [string]$ResourcesRoot = "Assets/Resources",
    [string]$PreferredVoiceName = "Microsoft Helena Desktop",
    [switch]$AllowFallback,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Resolve-ProjectPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Escape-SsmlText([string]$Value) {
    return [System.Security.SecurityElement]::Escape($Value)
}

$resolvedCsv = Resolve-ProjectPath $CsvPath
$resolvedResources = Resolve-ProjectPath $ResourcesRoot
if (!(Test-Path -LiteralPath $resolvedCsv)) {
    throw "Phrase CSV not found: $resolvedCsv"
}

Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voices = @($synth.GetInstalledVoices() | Where-Object { $_.Enabled } | Sort-Object { $_.VoiceInfo.Name })
$selected = $voices | Where-Object { $_.VoiceInfo.Name -eq $PreferredVoiceName } | Select-Object -First 1
if ($null -eq $selected) {
    $available = ($voices | ForEach-Object { $_.VoiceInfo.Name }) -join ", "
    if (!$AllowFallback) {
        throw "Preferred voice '$PreferredVoiceName' is not installed/enabled. Available voices: $available. Re-run with -AllowFallback to use the first es-ES/default voice."
    }

    $selected = $voices | Where-Object { $_.VoiceInfo.Culture.Name -eq "es-ES" } | Select-Object -First 1
    if ($null -eq $selected) {
        $selected = $voices | Select-Object -First 1
    }

    Write-Warning "Preferred voice '$PreferredVoiceName' not found. Falling back to '$($selected.VoiceInfo.Name)'."
}

if ($null -eq $selected) {
    throw "No enabled SAPI voices found."
}

$synth.SelectVoice($selected.VoiceInfo.Name)
$synth.Rate = -1
$synth.Volume = 100

$phrases = Import-Csv -LiteralPath $resolvedCsv -Encoding UTF8
$generated = 0
$skipped = 0
$logRows = New-Object System.Collections.Generic.List[string]
$logRows.Add("timestamp,voice,culture,clip_id,path,status,text")

foreach ($phrase in $phrases) {
    if ([string]::IsNullOrWhiteSpace($phrase.resource_path) -or [string]::IsNullOrWhiteSpace($phrase.text)) {
        Write-Warning "Skipping row with empty resource_path or text."
        continue
    }

    $relativeWav = ($phrase.resource_path.Trim().Replace("/", [System.IO.Path]::DirectorySeparatorChar) + ".wav")
    $outputPath = Join-Path $resolvedResources $relativeWav
    $outputDirectory = Split-Path -Parent $outputPath
    if (!(Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }

    if ((Test-Path -LiteralPath $outputPath) -and !$Force) {
        $skipped++
        Write-Output "SKIP existing clip: $outputPath"
        $logRows.Add("$(Get-Date -Format o),$($selected.VoiceInfo.Name),$($selected.VoiceInfo.Culture.Name),$($phrase.clip_id),$outputPath,skipped_existing,""$($phrase.text.Replace('"','""'))""")
        continue
    }

    $ssml = "<speak version='1.0' xml:lang='$($selected.VoiceInfo.Culture.Name)' xmlns='http://www.w3.org/2001/10/synthesis'>" +
        (Escape-SsmlText $phrase.text) +
        "</speak>"

    $synth.SetOutputToWaveFile($outputPath)
    try {
        $synth.SpeakSsml($ssml)
        $generated++
        Write-Output "GENERATED $($phrase.clip_id): $outputPath"
        $logRows.Add("$(Get-Date -Format o),$($selected.VoiceInfo.Name),$($selected.VoiceInfo.Culture.Name),$($phrase.clip_id),$outputPath,generated,""$($phrase.text.Replace('"','""'))""")
    }
    finally {
        $synth.SetOutputToNull()
    }
}

$logPath = Resolve-ProjectPath "Tools/TFG/P45B/robot_voice_generation_log.csv"
$logRows | Set-Content -LiteralPath $logPath -Encoding UTF8
Write-Output "Voice used: $($selected.VoiceInfo.Name) | culture=$($selected.VoiceInfo.Culture.Name)"
Write-Output "Generated: $generated | skipped_existing: $skipped"
Write-Output "Generation log: $logPath"
