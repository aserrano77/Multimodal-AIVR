using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class P45DQuestStandaloneBuild
{
    private const string P45D01OutputPath = "Builds/Android/P45D01_QuestStandalone_BaseModel_DataPath_Test_20260625.apk";
    private const string P45D02OutputPath = "Builds/Android/P45D02_QuestStandalone_AsrReadinessGate_Test_20260626.apk";
    private const string P45D03OutputPath = "Builds/Android/P45D03_QuestStandalone_WhisperBaseFileUri_Test_20260626.apk";
    private const string P45E01OutputPath = "Builds/Android/P45E01_QuestStandalone_SherpaOnnxAsrPrototype_Test_20260626.apk";
    private const string P45E05DOutputPath = "Builds/Android/P45E05D_QuestStandalone_SherpaOnnxAndroidRecognizer_Test_20260627.apk";
    private const string P45E05EOutputPath = "Builds/Android/P45E05E_QuestStandalone_SherpaOnnxAndroidModelResolve_Test_20260627.apk";
    private const string P45E05FOutputPath = "Builds/Android/P45E05F_QuestStandalone_SherpaOnnxNonBlockingShortCommands_Test_20260627.apk";
    private const string P45E05GOutputPath = "Builds/Android/P45E05G_QuestStandalone_SherpaOnnxStableDecode_Test_20260627.apk";
    private const string P45E05HOutputPath = "Builds/Android/P45E05H_QuestStandalone_SherpaOnnxDedicatedDecode_Test_20260627.apk";
    private const string P45FOutputPath = "Builds/Android/P45F_QuestStandalone_ExperimentDataPersistence_Test_20260627.apk";
    private const string P45F02OutputPath = "Builds/Android/P45F02_QuestStandalone_AutoParticipantId_Test_20260627.apk";
    private const string P45F03OutputPath = "Builds/Android/P45F03_QuestStandalone_StableSessionPersistence_Test_20260627.apk";
    private const string P45F04OutputPath = "Builds/Android/P45F04_QuestStandalone_ConditionVoiceGateCanvasAndCleanSessionClose_Test_20260627.apk";
    private const string P45F05OutputPath = "Builds/Android/P45F05_QuestStandalone_NoLegacyP001InRuntimeExport_Test_20260627.apk";
    private const string P46A01OutputPath = "Builds/Android/P46A01_QuestStandalone_UiRayRecoveryAndStopPickGate_Test_20260702.apk";
    private const string P46A01BOutputPath = "Builds/Android/P46A01B_QuestStandalone_UiRayHotfix_NoLocomotionRegression_StopFeedback_Test_20260703.apk";
    private const string P46A01COutputPath = "Builds/Android/P46A01C_QuestStandalone_UiRayRollback_StopTtsClip_Test_20260703.apk";
    private const string P46A01DOutputPath = "Builds/Android/P46A01D_QuestStandalone_XrUiRayDirectionAndDiagnostics_Test_20260703.apk";
    private const string P46A01EOutputPath = "Builds/Android/P46A01E_QuestStandalone_RestoreNativeXriHandsControllersUiRays_Test_20260703.apk";
    private const string P46A01FOutputPath = "Builds/Android/P46A01F_QuestStandalone_EnableNativeXriLineVisual_Test_20260703.apk";
    private const string P46A01GOutputPath = "Builds/Android/P46A01G_QuestStandalone_NativeXriCurveVisualStabilization_Test_20260703.apk";
    private const string P46A01HOutputPath = "Builds/Android/P46A01H_QuestStandalone_XriModalityOwnerStableRays_Test_20260703.apk";
    private const string P46A01IOutputPath = "Builds/Android/P46A01I_QuestStandalone_StableProtocolUiPointer_Test_20260703.apk";
    private const string P46A01JOutputPath = "Builds/Android/P46A01J_QuestStandalone_StableProtocolUiPointerDualController_Test_20260703.apk";
    private const string P46A01KOutputPath = "Builds/Android/P46A01K_QuestStandalone_XriUiModalityDeepDiagnostics_Test_20260704.apk";
    private const string P46A01LOutputPath = "Builds/Android/P46A01L_QuestStandalone_StableUiPointerAimAndInputRecovery_Test_20260704.apk";
    private const string P46A01MOutputPath = "Builds/Android/P46A01M_QuestStandalone_StableUiPointerRealAimNoCenterLock_Test_20260704.apk";
    private const string P46A01NOutputPath = "Builds/Android/P46A01N_QuestStandalone_StableUiPointerDirectControllerRay_Test_20260704.apk";
    private const string P46B01OutputPath = "Builds/Android/P46B01_QuestStandalone_StopLatchRejectsPickAndPlace_Test_20260704.apk";
    private const string P46C01OutputPath = "Builds/Android/P46C01_QuestStandalone_ManualSingleBoxGrab_Test_20260705.apk";
    private const string P46D01OutputPath = "Builds/Android/P46D01_QuestStandalone_PauseMenuAndSessionIdHistory_Test_20260705.apk";
    private const string P46D02OutputPath = "Builds/Android/P46D02_QuestStandalone_PauseHistoryButtonPersistenceFix_Test_20260705.apk";
    private const string P46D03OutputPath = "Builds/Android/P46D03_QuestStandalone_StartSessionCrashFix_PauseHistory_Test_20260705.apk";
    private const string P46D04OutputPath = "Builds/Android/P46D04_QuestStandalone_HistoryBackButtonAndCanvasReadability_Test_20260705.apk";
    private const string P46D05OutputPath = "Builds/Android/P46D05_QuestStandalone_TextSharpnessReadabilityFix_Test_20260705.apk";
    private const string P46D06OutputPath = "Builds/Android/P46D06_QuestStandalone_VisualRollbackKeepPauseHistory_Test_20260705.apk";
    private const string P46D07OutputPath = "Builds/Android/P46D07_QuestStandalone_HistoryScrollFixedViewport_Test_20260705.apk";
    private const string P46D08OutputPath = "Builds/Android/P46D08_QuestStandalone_HistoryPagination_Test_20260705.apk";
    private const string P46D09OutputPath = "Builds/Android/P46D09_QuestStandalone_PauseMenuExperimentalSessionControl_Test_20260705.apk";
    private const string P46D10OutputPath = "Builds/Android/P46D10_QuestStandalone_PauseMenuYButtonRobust_Test_20260705.apk";
    private const string P46D11OutputPath = "Builds/Android/P46D11_QuestStandalone_PauseMenuClickableButtons_Test_20260705.apk";
    private const string P46D12OutputPath = "Builds/Android/P46D12_QuestStandalone_PauseMenuRuntimeCanvasModal_Test_20260706.apk";
    private const string P46D13OutputPath = "Builds/Android/P46D13_QuestStandalone_PauseMenuFrontPanelRestartSaveExit_Test_20260706.apk";
    private const string P46D14OutputPath = "Builds/Android/P46D14_QuestStandalone_PauseMenuFrontPanelClickable_Test_20260706.apk";
    private const string P46D15OutputPath = "Builds/Android/P46D15_QuestStandalone_PauseMenuIncidentRoundBoundary_Test_20260706.apk";
    private const string P46D16OutputPath = "Builds/Android/P46D16_QuestStandalone_PauseStateAndSavedExitCheckpoint_Test_20260706.apk";
    private const string P46D17OutputPath = "Builds/Android/P46D17_QuestStandalone_PauseRestartAndSavedExitPrompt_Test_20260706.apk";
    private const string P46D18OutputPath = "Builds/Android/P46D18_QuestStandalone_PauseRestartWorldResetAndRecoveryPrompt_Test_20260706.apk";
    private const string P46D19OutputPath = "Builds/Android/P46D19_QuestStandalone_SavedExitPromptButtons_Test_20260706.apk";
    private const string P46D20OutputPath = "Builds/Android/P46D20_QuestStandalone_PauseRestartReturnsToIntro_Test_20260707.apk";
    private const string P46GOutputPath = "Builds/Android/P46G_QuestStandalone_GlobalInstructionsFinalQuestionnaireCode_Test_20260707.apk";
    private const string P46G01OutputPath = "Builds/Android/P46G01_QuestStandalone_GlobalInstructionsNoHistoryFrontFinalCode_Test_20260707.apk";
    private const string P46G02OutputPath = "Builds/Android/P46G02_QuestStandalone_StableGlobalInstructionsWizardFrontFinalCode_Test_20260707.apk";
    private const string P46G03OutputPath = "Builds/Android/P46G03_QuestStandalone_GlobalInstructionsStableButtonsAndAudio_Test_20260707.apk";
    private const string P46G04OutputPath = "Builds/Android/P46G04_QuestStandalone_InstructionsAudioExitToStartNoSave_Test_20260707.apk";
    private const string P46G05OutputPath = "Builds/Android/P46G05_QuestStandalone_FrontInstructionsModalBulletsPauseStartNoSave_Test_20260708.apk";
    private const string P46G06OutputPath = "Builds/Android/P46G06_QuestStandalone_GlobalInstructionsAfterStartOnly_Test_20260708.apk";
    private const string P46G07OutputPath = "Builds/Android/P46G07_QuestStandalone_GlobalInstructionsRuntimePreSession_Test_20260708.apk";
    private const string P46G08OutputPath = "Builds/Android/P46G08_QuestStandalone_GlobalInstructionsIndependentFrontCanvas_Test_20260708.apk";
    private const string P46G09OutputPath = "Builds/Android/P46G09_QuestStandalone_GlobalInstructionsFrontCanvasPoseLayoutFix_Test_20260708.apk";
    private const string P46G10OutputPath = "Builds/Android/P46G10_QuestStandalone_GlobalInstructionsFrontCanvasClickable_Test_20260708.apk";
    private const string P46G11OutputPath = "Builds/Android/P46G11_QuestStandalone_GlobalInstructionsFrontCanvasFixedPoseStyle_Test_20260708.apk";
    private const string P46H01OutputPath = "Builds/Android/P46H01_QuestStandalone_CanvasHeightAndLayoutFit_Test_20260709.apk";
    private const string P46H02OutputPath = "Builds/Android/P46H02_QuestStandalone_CanvasVisualRefinement_Test_20260710.apk";
    private const string P46H03OutputPath = "Builds/Android/P46H03_QuestStandalone_StartScreenLayoutFinal_Test_20260710.apk";
    private const string P46H04OutputPath = "Builds/Android/P46H04_QuestStandalone_StartScreenButtonBoundsFix_Test_20260710.apk";
    private const string P46I01OutputPath = "Builds/Android/P46I01_QuestStandalone_UiRaycastHitMarker_Test_20260710.apk";
    private const string P46I02OutputPath = "Builds/Android/P46I02_QuestStandalone_UiRaycastHitMarkerFix_Test_20260710.apk";
    private const string P46I03OutputPath = "Builds/Android/P46I03_QuestStandalone_PauseRayClipAndStartPoseFix_Test_20260713.apk";
    private const string P46I04OutputPath = "Builds/Android/P46I04_QuestStandalone_RestoreStartScreenAfterP46I03_Test_20260713.apk";
    private const string P46J01OutputPath = "Builds/Android/P46J01_QuestStandalone_RobotBaseNoBlueTint_Test_20260714.apk";
    private const string P46J02OutputPath = "Builds/Android/P46J02_QuestStandalone_CanvasTextNoWhiteHalo_Test_20260714.apk";
    private const string P46J03OutputPath = "Builds/Android/P46J03_QuestStandalone_CanvasButtonSemanticPalette_Test_20260715.apk";
    private const string P46J03R1OutputPath = "Builds/Android/P46J03R1_QuestStandalone_ResumePreparingFix_Test_20260715.apk";
    private const string P46J03R2OutputPath = "Builds/Android/P46J03R2_QuestStandalone_ButtonSemanticPolish_Test_20260715.apk";
    private const string P46K01OutputPath = "Builds/Android/P46K01_QuestStandalone_StopIdempotencySafeResume_Test_20260714.apk";
    private const string P46K02OutputPath = "Builds/Android/P46K02_QuestStandalone_AlreadyStoppedTtsFeedback_Test_20260714.apk";
    private const string P46L01OutputPath = "Builds/Android/P46L01_QuestStandalone_GlobalExperimentPause_XRLocomotionLock_Test_20260714.apk";
    private const string P46L02OutputPath = "Builds/Android/P46L02_QuestStandalone_SavedExitImmutableConditionOrder_Test_20260714.apk";
    private const string P46M01OutputPath = "Builds/Android/P46M01_QuestStandalone_VoicePermissionAndPrewarm_Test_20260715.apk";
    private const string P46J04OutputPath = "Builds/Android/P46J04_QuestStandalone_HistoryTableAlignment_Test_20260715.apk";
    private const string P46J04R1OutputPath = "Builds/Android/P46J04R1_QuestStandalone_HistoryPaginationCompact_Test_20260716.apk";

    public static void BuildP45D01QuestStandalone()
    {
        BuildQuestStandalone(P45D01OutputPath);
    }

    public static void BuildP45D02QuestStandalone()
    {
        BuildQuestStandalone(P45D02OutputPath);
    }

    public static void BuildP45D03QuestStandalone()
    {
        BuildQuestStandalone(P45D03OutputPath);
    }

    public static void BuildP45E01QuestStandalone()
    {
        BuildQuestStandalone(P45E01OutputPath);
    }

    public static void BuildP45E05DQuestStandalone()
    {
        BuildQuestStandalone(P45E05DOutputPath);
    }

    public static void BuildP45E05EQuestStandalone()
    {
        BuildQuestStandalone(P45E05EOutputPath);
    }

    public static void BuildP45E05FQuestStandalone()
    {
        BuildQuestStandalone(P45E05FOutputPath);
    }

    public static void BuildP45E05GQuestStandalone()
    {
        BuildQuestStandalone(P45E05GOutputPath);
    }

    public static void BuildP45E05HQuestStandalone()
    {
        BuildQuestStandalone(P45E05HOutputPath);
    }

    public static void BuildP45FQuestStandalone()
    {
        BuildQuestStandalone(P45FOutputPath);
    }

    public static void BuildP45F02QuestStandalone()
    {
        BuildQuestStandalone(P45F02OutputPath);
    }

    public static void BuildP45F03QuestStandalone()
    {
        BuildQuestStandalone(P45F03OutputPath);
    }

    public static void BuildP45F04QuestStandalone()
    {
        BuildQuestStandalone(P45F04OutputPath);
    }

    public static void BuildP45F05QuestStandalone()
    {
        BuildQuestStandalone(P45F05OutputPath);
    }

    public static void BuildP46A01QuestStandalone()
    {
        BuildQuestStandalone(P46A01OutputPath);
    }

    public static void BuildP46A01BQuestStandalone()
    {
        BuildQuestStandalone(P46A01BOutputPath);
    }

    public static void BuildP46A01CQuestStandalone()
    {
        BuildQuestStandalone(P46A01COutputPath);
    }

    public static void BuildP46A01DQuestStandalone()
    {
        BuildQuestStandalone(P46A01DOutputPath);
    }

    public static void BuildP46A01EQuestStandalone()
    {
        BuildQuestStandalone(P46A01EOutputPath);
    }

    public static void BuildP46A01FQuestStandalone()
    {
        BuildQuestStandalone(P46A01FOutputPath);
    }

    public static void BuildP46A01GQuestStandalone()
    {
        BuildQuestStandalone(P46A01GOutputPath);
    }

    public static void BuildP46A01HQuestStandalone()
    {
        BuildQuestStandalone(P46A01HOutputPath);
    }

    public static void BuildP46A01IQuestStandalone()
    {
        BuildQuestStandalone(P46A01IOutputPath);
    }

    public static void BuildP46A01JQuestStandalone()
    {
        BuildQuestStandalone(P46A01JOutputPath);
    }

    public static void BuildP46A01KQuestStandalone()
    {
        BuildQuestStandalone(P46A01KOutputPath);
    }

    public static void BuildP46A01LQuestStandalone()
    {
        BuildQuestStandalone(P46A01LOutputPath);
    }

    public static void BuildP46A01MQuestStandalone()
    {
        BuildQuestStandalone(P46A01MOutputPath);
    }

    public static void BuildP46A01NQuestStandalone()
    {
        BuildQuestStandalone(P46A01NOutputPath);
    }

    public static void BuildP46B01QuestStandalone()
    {
        BuildQuestStandalone(P46B01OutputPath);
    }

    public static void BuildP46C01QuestStandalone()
    {
        BuildQuestStandalone(P46C01OutputPath);
    }

    public static void BuildP46D01QuestStandalone()
    {
        BuildQuestStandalone(P46D01OutputPath);
    }

    public static void BuildP46D02QuestStandalone()
    {
        BuildQuestStandalone(P46D02OutputPath);
    }

    public static void BuildP46D03QuestStandalone()
    {
        BuildQuestStandalone(P46D03OutputPath);
    }

    public static void BuildP46D04QuestStandalone()
    {
        BuildQuestStandalone(P46D04OutputPath);
    }

    public static void BuildP46D05QuestStandalone()
    {
        BuildQuestStandalone(P46D05OutputPath);
    }

    public static void BuildP46D06QuestStandalone()
    {
        BuildQuestStandalone(P46D06OutputPath);
    }

    public static void BuildP46D07QuestStandalone()
    {
        BuildQuestStandalone(P46D07OutputPath);
    }

    public static void BuildP46D08QuestStandalone()
    {
        BuildQuestStandalone(P46D08OutputPath);
    }

    public static void BuildP46D09QuestStandalone()
    {
        BuildQuestStandalone(P46D09OutputPath);
    }

    public static void BuildP46D10QuestStandalone()
    {
        BuildQuestStandalone(P46D10OutputPath);
    }

    public static void BuildP46D11QuestStandalone()
    {
        BuildQuestStandalone(P46D11OutputPath);
    }

    public static void BuildP46D12QuestStandalone()
    {
        BuildQuestStandalone(P46D12OutputPath);
    }

    public static void BuildP46D13QuestStandalone()
    {
        BuildQuestStandalone(P46D13OutputPath);
    }

    public static void BuildP46D14QuestStandalone()
    {
        BuildQuestStandalone(P46D14OutputPath);
    }

    public static void BuildP46D15QuestStandalone()
    {
        BuildQuestStandalone(P46D15OutputPath);
    }

    public static void BuildP46D16QuestStandalone()
    {
        BuildQuestStandalone(P46D16OutputPath);
    }

    public static void BuildP46D17QuestStandalone()
    {
        BuildQuestStandalone(P46D17OutputPath);
    }

    public static void BuildP46D18QuestStandalone()
    {
        BuildQuestStandalone(P46D18OutputPath);
    }

    public static void BuildP46D19QuestStandalone()
    {
        BuildQuestStandalone(P46D19OutputPath);
    }

    public static void BuildP46D20QuestStandalone()
    {
        BuildQuestStandalone(P46D20OutputPath);
    }

    public static void BuildP46GQuestStandalone()
    {
        BuildQuestStandalone(P46GOutputPath);
    }

    public static void BuildP46G01QuestStandalone()
    {
        BuildQuestStandalone(P46G01OutputPath);
    }

    public static void BuildP46G02QuestStandalone()
    {
        BuildQuestStandalone(P46G02OutputPath);
    }

    public static void BuildP46G03QuestStandalone()
    {
        BuildQuestStandalone(P46G03OutputPath);
    }

    public static void BuildP46G04QuestStandalone()
    {
        BuildQuestStandalone(P46G04OutputPath);
    }

    public static void BuildP46G05QuestStandalone()
    {
        BuildQuestStandalone(P46G05OutputPath);
    }

    public static void BuildP46G06QuestStandalone()
    {
        BuildQuestStandalone(P46G06OutputPath);
    }

    public static void BuildP46G07QuestStandalone()
    {
        BuildQuestStandalone(P46G07OutputPath);
    }

    public static void BuildP46G08QuestStandalone()
    {
        BuildQuestStandalone(P46G08OutputPath);
    }

    public static void BuildP46G09QuestStandalone()
    {
        BuildQuestStandalone(P46G09OutputPath);
    }

    public static void BuildP46G10QuestStandalone()
    {
        BuildQuestStandalone(P46G10OutputPath);
    }

    public static void BuildP46G11QuestStandalone()
    {
        BuildQuestStandalone(P46G11OutputPath);
    }

    public static void BuildP46H01QuestStandalone()
    {
        BuildQuestStandalone(P46H01OutputPath);
    }

    public static void BuildP46H02QuestStandalone()
    {
        BuildQuestStandalone(P46H02OutputPath);
    }

    public static void BuildP46H03QuestStandalone()
    {
        BuildQuestStandalone(P46H03OutputPath);
    }

    public static void BuildP46H04QuestStandalone()
    {
        BuildQuestStandalone(P46H04OutputPath);
    }

    public static void BuildP46I01QuestStandalone()
    {
        BuildQuestStandalone(P46I01OutputPath);
    }

    public static void BuildP46I02QuestStandalone()
    {
        BuildQuestStandalone(P46I02OutputPath);
    }

    public static void BuildP46I03QuestStandalone()
    {
        BuildQuestStandalone(P46I03OutputPath);
    }

    public static void BuildP46I04QuestStandalone()
    {
        BuildQuestStandalone(P46I04OutputPath);
    }

    public static void BuildP46J01QuestStandalone()
    {
        BuildQuestStandalone(P46J01OutputPath);
    }

    public static void BuildP46J02QuestStandalone()
    {
        BuildQuestStandalone(P46J02OutputPath);
    }

    public static void BuildP46J03QuestStandalone()
    {
        BuildQuestStandalone(P46J03OutputPath);
    }

    public static void BuildP46J03R1QuestStandalone()
    {
        BuildQuestStandalone(P46J03R1OutputPath);
    }

    public static void BuildP46J03R2QuestStandalone()
    {
        BuildQuestStandalone(P46J03R2OutputPath);
    }

    public static void BuildP46K01QuestStandalone()
    {
        BuildQuestStandalone(P46K01OutputPath);
    }

    public static void BuildP46K02QuestStandalone()
    {
        BuildQuestStandalone(P46K02OutputPath);
    }

    public static void BuildP46L01QuestStandalone()
    {
        BuildQuestStandalone(P46L01OutputPath);
    }

    public static void BuildP46L02QuestStandalone()
    {
        BuildQuestStandalone(P46L02OutputPath);
    }

    public static void BuildP46M01QuestStandalone()
    {
        BuildQuestStandalone(P46M01OutputPath);
    }

    public static void BuildP46J04QuestStandalone()
    {
        BuildQuestStandalone(P46J04OutputPath);
    }

    public static void BuildP46J04R1QuestStandalone()
    {
        BuildQuestStandalone(P46J04R1OutputPath);
    }

    private static void BuildQuestStandalone(string outputPath)
    {
        string[] scenes =
        {
            "Assets/Scenes/experiment_start_scene.unity",
            "Assets/Scenes/final_scene.unity"
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        BuildPlayerOptions options = new()
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        };

        Debug.Log($"[P45DQuestStandaloneBuild] Building Android APK -> {outputPath}");
        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;
        Debug.Log($"[P45DQuestStandaloneBuild] result={summary.result} output={summary.outputPath} size={summary.totalSize}");
        if (summary.result != BuildResult.Succeeded)
        {
            throw new System.Exception($"P45D Android build failed: {summary.result}");
        }
    }
}
