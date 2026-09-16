param(
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

function U([int]$CodePoint) {
    return [string][char]$CodePoint
}

$resolvedResources = Resolve-ProjectPath $ResourcesRoot

# Caracteres acentuados generados por codigo Unicode para evitar problemas de codificacion del .ps1.
$a = U 0x00E1
$e = U 0x00E9
$i = U 0x00ED
$o = U 0x00F3
$u = U 0x00FA
$upperU = U 0x00DA

$clips = @(
    [PSCustomObject]@{
        ClipId = "global_instruction_page_01_structure"
        ResourcePath = "ExperimentInstructions/global_instruction_page_01_structure"
        Text = "La sesi${o}n tiene tres pruebas. Cada prueba tiene dos rondas. Antes de cada prueba aparecer${a}n instrucciones espec${i}ficas. Para empezar, pulsa el bot${o}n Comenzar la prueba."
    },
    [PSCustomObject]@{
        ClipId = "global_instruction_page_02_controls"
        ResourcePath = "ExperimentInstructions/global_instruction_page_02_controls"
        Text = "Usa el gatillo para pulsar botones de men${u}s y paneles. Usa el bot${o}n de agarre para coger cajas. Usa la palanca anal${o}gica para desplazarte. El bot${o}n Y izquierdo abre el men${u} de pausa."
    },
    [PSCustomObject]@{
        ClipId = "global_instruction_page_03_wall_panel"
        ResourcePath = "ExperimentInstructions/global_instruction_page_03_wall_panel"
        Text = "A la izquierda, en la pared, encontrar${a}s el panel de control del experimento. ${upperU}salo para avanzar cuando corresponda. Tambi${e}n puedes usarlo para cerrar una ronda por incidencia, si ocurre un problema."
    },
    [PSCustomObject]@{
        ClipId = "global_instruction_page_04_pause"
        ResourcePath = "ExperimentInstructions/global_instruction_page_04_pause"
        Text = "El men${u} de pausa permite continuar, reiniciar la sesi${o}n experimental, guardar y salir, o salir al inicio sin guardar. Si ocurre un problema, usa la pausa o el panel de la pared."
    },
    [PSCustomObject]@{
        ClipId = "global_instruction_page_05_questionnaire"
        ResourcePath = "ExperimentInstructions/global_instruction_page_05_questionnaire"
        Text = "Al terminar las tres pruebas aparecer${a} un c${o}digo de cuestionario de seis letras y n${u}meros. An${o}talo antes de cerrar la aplicaci${o}n e introd${u}celo en el formulario final."
    },
    [PSCustomObject]@{
        ClipId = "global_instruction_page_06_rules"
        ResourcePath = "ExperimentInstructions/global_instruction_page_06_rules"
        Text = "Durante la prueba, no bloquees al robot, no lo empujes, no empujes los pal${e}s y no interfieras con las cajas que est${e} manipulando."
    }
)

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

# Ajustes conservadores: mantener la voz clara sin ralentizarla demasiado.
$synth.Rate = -1
$synth.Volume = 100

$generated = 0
$skipped = 0
$logRows = New-Object System.Collections.Generic.List[string]
$logRows.Add("timestamp,voice,culture,clip_id,path,status,text")

Write-Output "Voice selected: $($selected.VoiceInfo.Name) | culture=$($selected.VoiceInfo.Culture.Name)"
Write-Output "Resources root: $resolvedResources"
Write-Output "Final texts that will be sent to SAPI:"
foreach ($clip in $clips) {
    Write-Output "[$($clip.ClipId)] $($clip.Text)"
}

foreach ($clip in $clips) {
    $relativeWav = ($clip.ResourcePath.Replace("/", [System.IO.Path]::DirectorySeparatorChar) + ".wav")
    $outputPath = Join-Path $resolvedResources $relativeWav
    $outputDirectory = Split-Path -Parent $outputPath

    if (!(Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }

    if ((Test-Path -LiteralPath $outputPath) -and !$Force) {
        $skipped++
        Write-Output "SKIP existing clip: $outputPath"
        $logRows.Add("$(Get-Date -Format o),$($selected.VoiceInfo.Name),$($selected.VoiceInfo.Culture.Name),$($clip.ClipId),$outputPath,skipped_existing,""$($clip.Text.Replace('"','""'))""")
        continue
    }

    $ssml = "<speak version='1.0' xml:lang='$($selected.VoiceInfo.Culture.Name)' xmlns='http://www.w3.org/2001/10/synthesis'>" +
        (Escape-SsmlText $clip.Text) +
        "</speak>"

    $synth.SetOutputToWaveFile($outputPath)
    try {
        $synth.SpeakSsml($ssml)
        $generated++
        Write-Output "GENERATED $($clip.ClipId): $outputPath"
        $logRows.Add("$(Get-Date -Format o),$($selected.VoiceInfo.Name),$($selected.VoiceInfo.Culture.Name),$($clip.ClipId),$outputPath,generated,""$($clip.Text.Replace('"','""'))""")
    }
    finally {
        $synth.SetOutputToNull()
    }
}

$logPath = Resolve-ProjectPath "Tools/TFG/P46G/global_instruction_voice_generation_log.csv"
$logRows | Set-Content -LiteralPath $logPath -Encoding UTF8

Write-Output "Voice used: $($selected.VoiceInfo.Name) | culture=$($selected.VoiceInfo.Culture.Name)"
Write-Output "Generated: $generated | skipped_existing: $skipped"
Write-Output "Generation log: $logPath"
