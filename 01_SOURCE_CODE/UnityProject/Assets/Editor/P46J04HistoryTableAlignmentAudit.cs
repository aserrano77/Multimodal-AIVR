using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class P46J04HistoryTableAlignmentAudit
{
    private const string Prefix = "[P46J-04][Audit]";
    private const string StartScreenPath = "Assets/Scripts/ExperimentRuntimeStartScreenUI.cs";
    private const string LayoutPath = "Assets/Scripts/Autonomy/UnityIntegration/ExperimentHistoryTableLayout.cs";
    private const string TestPath = "Assets/Scripts/Autonomy/BT/Tests/ExperimentHistoryTableLayoutTests.cs";

    [MenuItem("Tools/Experiment/P46J-04 Audit History Table Alignment")]
    public static void RunFromMenu()
    {
        bool passed = RunAudit(out string report);
        Debug.Log(report);
        EditorUtility.DisplayDialog("P46J-04 History Table", passed ? "PASS" : "FAIL", "Aceptar");
    }

    public static void RunForBatch()
    {
        bool passed = RunAudit(out string report);
        Debug.Log(report);
        if (!passed)
        {
            throw new InvalidOperationException("P46J-04 audit failed. See Editor log for details.");
        }
    }

    public static bool RunAudit(out string report)
    {
        var lines = new List<string>();
        int passed = 0;
        int failed = 0;

        void Check(string id, bool condition, string detail)
        {
            if (condition)
            {
                passed++;
                lines.Add($"PASS {id}: {detail}");
            }
            else
            {
                failed++;
                lines.Add($"FAIL {id}: {detail}");
            }
        }

        string start = Read(StartScreenPath);
        string layout = Read(LayoutPath);
        ExperimentHistoryColumnDefinition[] columns = ExperimentHistoryTableLayout.Columns.ToArray();

        Check("single_authority", Count(start, "ExperimentHistoryTableLayout.Columns") == 1 &&
                                  Count(layout, "ExperimentHistoryColumnDefinition[] Definitions") == 1,
            "header and data rows consume one shared column definition");
        Check("four_columns", columns.Length == 4 &&
                              columns.Select(column => column.Column).SequenceEqual(new[]
                              {
                                  ExperimentHistoryColumn.FullId,
                                  ExperimentHistoryColumn.QuestionnaireCode,
                                  ExperimentHistoryColumn.StartedAt,
                                  ExperimentHistoryColumn.Status
                              }),
            string.Join(", ", columns.Select(column => $"{column.Column}={column.Width:0}px")));
        Check("exact_widths", columns.Select(column => column.Width).SequenceEqual(new[] { 218f, 104f, 180f, 282f }) &&
                              Mathf.Approximately(ExperimentHistoryTableLayout.TotalColumnWidth, 784f),
            "ID=218, Code=104, Start=180, Status=282, total=784 px");
        Check("header_viewport_alignment", start.Contains("ExperimentHistoryTableLayout.ViewportHorizontalInset", StringComparison.Ordinal) &&
                                                start.Contains("CreateHistoryRowRoot(\"HistoryRow\", horizontalPadding: 0)", StringComparison.Ordinal),
            "header compensates the 8 px viewport inset and rows use the viewport content width");
        Check("fixed_width_layout", start.Contains("cellElement.minWidth = definition.Width", StringComparison.Ordinal) &&
                                    start.Contains("cellElement.preferredWidth = definition.Width", StringComparison.Ordinal) &&
                                    start.Contains("cellElement.flexibleWidth = 0f", StringComparison.Ordinal),
            "TMP preferred width cannot resize any column");
        Check("no_wrap", start.Contains("text.textWrappingMode = TextWrappingModes.NoWrap", StringComparison.Ordinal),
            "history cells cannot add a second line");
        Check("constant_row_height", start.Contains("rowElement.minHeight = ExperimentHistoryTableLayout.RowHeight", StringComparison.Ordinal) &&
                                     start.Contains("rowElement.preferredHeight = ExperimentHistoryTableLayout.RowHeight", StringComparison.Ordinal) &&
                                     start.Contains("rowElement.flexibleHeight = 0f", StringComparison.Ordinal),
            $"all rows are fixed at {ExperimentHistoryTableLayout.RowHeight:0}px");
        ExperimentHistoryColumnDefinition status = ExperimentHistoryTableLayout.Get(ExperimentHistoryColumn.Status);
        Check("status_left_aligned", start.Contains("text.alignment = TextAlignmentOptions.MidlineLeft", StringComparison.Ordinal),
            "Estado and all table cells are left aligned");
        Check("long_status_treatment", status.Width == 282f && status.AllowAutoSize && status.MinimumFontSize == 14 &&
                                       start.Contains("TextOverflowModes.Ellipsis", StringComparison.Ordinal),
            "known long statuses get the widest column, bounded 18-14 auto-size, then ellipsis only as fallback");
        float sevenRowsHeight = (ExperimentHistoryTableLayout.EntriesPerPage * ExperimentHistoryTableLayout.RowHeight) +
                                ((ExperimentHistoryTableLayout.EntriesPerPage - 1) * 4f);
        float viewportContentHeight = ExperimentHistoryTableLayout.ViewportHeight -
                                      (2f * ExperimentHistoryTableLayout.ViewportHorizontalInset);
        Check("compact_pagination", ExperimentHistoryTableLayout.PaginationHeight == 48f &&
                                    ExperimentHistoryTableLayout.PaginationTopGap == 4f &&
                                    start.Contains("rowElement.preferredHeight = ExperimentHistoryTableLayout.PaginationHeight", StringComparison.Ordinal) &&
                                    start.Contains("layout.childForceExpandHeight = false", StringComparison.Ordinal),
            "pagination row, buttons and indicator use a fixed 48 px height with a 4 px top gap");
        Check("seven_rows_fit", ExperimentHistoryTableLayout.EntriesPerPage == 7 &&
                                sevenRowsHeight <= viewportContentHeight,
            $"seven rows require {sevenRowsHeight:0}px inside {viewportContentHeight:0}px of usable viewport height");
        Check("header_rows_same_builder", start.Contains("AddHistoryCells(row, null, isHeader: true)", StringComparison.Ordinal) &&
                                          start.Contains("AddHistoryCells(row, entry, isHeader: false)", StringComparison.Ordinal),
            "headers and values use AddHistoryCells with the same definitions");
        Check("header_before_rows", Index(start, "AddHistoryHeader();") < Index(start, "CreateHistoryScrollView(page)"),
            "the fixed header remains above the scrollable data rows");
        Check("empty_history_supported", start.Contains("AddHistoryEmptyMessage", StringComparison.Ordinal) &&
                                         start.Contains("entries.Count == 0", StringComparison.Ordinal),
            "empty history retains its explicit rendering route");
        Check("pagination_roles_preserved", start.Contains("PreviousHistoryPagePressed,\n                ExperimentButtonRole.Secondary", StringComparison.Ordinal) &&
                                            start.Contains("NextHistoryPagePressed,\n                ExperimentButtonRole.Primary", StringComparison.Ordinal),
            "Mas recientes remains Secondary and Mas antiguas remains Primary");
        Check("condition_matrix", ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(new[]
        {
            ExperimentCompensatedConditionOrder.C00,
            ExperimentCompensatedConditionOrder.C10,
            ExperimentCompensatedConditionOrder.C11
        }), "C00/C10/C11 remain present and C01 remains absent");
        Check("missing_scripts", CountMissingScriptsInTargetScenes(out string missingDetail) == 0, missingDetail);
        Check("metas_present", File.Exists(Absolute(LayoutPath + ".meta")) &&
                               File.Exists(Absolute(TestPath + ".meta")) &&
                               File.Exists(Absolute("Assets/Editor/P46J04HistoryTableAlignmentAudit.cs.meta")),
            "all P46J-04 scripts have versioned .meta files");

        lines.Insert(0, $"{Prefix} {(failed == 0 ? "PASS" : "FAIL")} {passed}/{passed + failed}");
        report = string.Join("\n", lines);
        string outputDirectory = Absolute("Logs/P46J04_HistoryTableAlignment");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "P46J04_audit.txt"), report);
        return failed == 0;
    }

    private static int CountMissingScriptsInTargetScenes(out string detail)
    {
        string[] scenePaths =
        {
            "Assets/Scenes/experiment_start_scene.unity",
            "Assets/Scenes/final_scene.unity",
            "Assets/Scenes/autonomous_demo_step22_multimodal_bridge.unity"
        };
        int total = 0;
        foreach (string scenePath in scenePaths)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                {
                    total += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
                }
            }
        }

        detail = $"target scenes missing scripts={total}";
        return total;
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(Absolute(relativePath));
    }

    private static string Absolute(string relativePath)
    {
        return Path.Combine(Directory.GetParent(Application.dataPath).FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static int Index(string source, string value)
    {
        return source.IndexOf(value, StringComparison.Ordinal);
    }
}
