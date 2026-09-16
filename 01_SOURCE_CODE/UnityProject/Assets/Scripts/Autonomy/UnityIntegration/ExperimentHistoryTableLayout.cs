using System;
using System.Collections.Generic;

namespace Autonomy.UnityIntegration
{
    public enum ExperimentHistoryColumn
    {
        FullId,
        QuestionnaireCode,
        StartedAt,
        Status
    }

    public readonly struct ExperimentHistoryColumnDefinition
    {
        public ExperimentHistoryColumnDefinition(
            ExperimentHistoryColumn column,
            string header,
            float width,
            int cellFontSize,
            int minimumFontSize,
            bool allowAutoSize)
        {
            Column = column;
            Header = header ?? string.Empty;
            Width = width;
            CellFontSize = cellFontSize;
            MinimumFontSize = minimumFontSize;
            AllowAutoSize = allowAutoSize;
        }

        public ExperimentHistoryColumn Column { get; }
        public string Header { get; }
        public float Width { get; }
        public int CellFontSize { get; }
        public int MinimumFontSize { get; }
        public bool AllowAutoSize { get; }
    }

    /// <summary>
    /// Single geometry authority shared by the history header and every data row.
    /// Widths target the 824 px start-screen content area after the 8 px viewport
    /// inset on each side and the three 8 px inter-column gaps are removed.
    /// </summary>
    public static class ExperimentHistoryTableLayout
    {
        public const float RowHeight = 42f;
        public const float CellHeight = 38f;
        public const float ColumnSpacing = 8f;
        public const int ViewportHorizontalInset = 8;
        public const int HeaderFontSize = 21;
        public const int EntriesPerPage = 7;
        public const float ViewportHeight = 338f;
        public const float PaginationHeight = 48f;
        public const float PaginationSpacing = 10f;
        public const float PaginationTopGap = 4f;
        public const int PaginationFontSize = 24;

        private static readonly ExperimentHistoryColumnDefinition[] Definitions =
        {
            new(ExperimentHistoryColumn.FullId, "ID completo", 218f, 19, 19, false),
            new(ExperimentHistoryColumn.QuestionnaireCode, "Codigo", 104f, 21, 21, false),
            new(ExperimentHistoryColumn.StartedAt, "Inicio", 180f, 18, 18, false),
            new(ExperimentHistoryColumn.Status, "Estado", 282f, 18, 14, true)
        };

        public static IReadOnlyList<ExperimentHistoryColumnDefinition> Columns => Definitions;

        public static float TotalColumnWidth
        {
            get
            {
                float total = 0f;
                for (int i = 0; i < Definitions.Length; i++)
                {
                    total += Definitions[i].Width;
                }

                return total;
            }
        }

        public static ExperimentHistoryColumnDefinition Get(ExperimentHistoryColumn column)
        {
            for (int i = 0; i < Definitions.Length; i++)
            {
                if (Definitions[i].Column == column)
                {
                    return Definitions[i];
                }
            }

            throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown history column.");
        }
    }
}
