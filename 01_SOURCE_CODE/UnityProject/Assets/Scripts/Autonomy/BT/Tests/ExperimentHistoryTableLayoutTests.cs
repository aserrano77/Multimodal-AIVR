using System.IO;
using System.Linq;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentHistoryTableLayoutTests
    {
        [Test]
        public void HeaderAndRowsUseOneFourColumnDefinition()
        {
            ExperimentHistoryColumnDefinition[] columns = ExperimentHistoryTableLayout.Columns.ToArray();

            Assert.That(columns, Has.Length.EqualTo(4));
            Assert.That(columns.Select(column => column.Column), Is.EqualTo(new[]
            {
                ExperimentHistoryColumn.FullId,
                ExperimentHistoryColumn.QuestionnaireCode,
                ExperimentHistoryColumn.StartedAt,
                ExperimentHistoryColumn.Status
            }));
            Assert.That(columns.Select(column => column.Width), Is.EqualTo(new[] { 218f, 104f, 180f, 282f }));
            Assert.That(ExperimentHistoryTableLayout.TotalColumnWidth, Is.EqualTo(784f));
        }

        [Test]
        public void FixedColumnsExactlyFillHeaderAndViewportRowGeometry()
        {
            const float startScreenContentWidth = 920f - (2f * 48f);
            float headerCellSpace = startScreenContentWidth -
                                    (2f * ExperimentHistoryTableLayout.ViewportHorizontalInset) -
                                    (3f * ExperimentHistoryTableLayout.ColumnSpacing);
            float viewportRowCellSpace = (startScreenContentWidth -
                                          (2f * ExperimentHistoryTableLayout.ViewportHorizontalInset)) -
                                         (3f * ExperimentHistoryTableLayout.ColumnSpacing);

            Assert.That(headerCellSpace, Is.EqualTo(ExperimentHistoryTableLayout.TotalColumnWidth));
            Assert.That(viewportRowCellSpace, Is.EqualTo(ExperimentHistoryTableLayout.TotalColumnWidth));
        }

        [Test]
        public void CompactPaginationFundsSevenRowsWithoutIncreasingVerticalBudget()
        {
            float rowsHeight = (ExperimentHistoryTableLayout.EntriesPerPage * ExperimentHistoryTableLayout.RowHeight) +
                               ((ExperimentHistoryTableLayout.EntriesPerPage - 1) * 4f);
            float viewportContentHeight = ExperimentHistoryTableLayout.ViewportHeight -
                                          (2f * ExperimentHistoryTableLayout.ViewportHorizontalInset);
            float heightRecovered = (60f - ExperimentHistoryTableLayout.PaginationHeight) +
                                    (10f - ExperimentHistoryTableLayout.PaginationTopGap);
            float viewportGrowth = ExperimentHistoryTableLayout.ViewportHeight - 320f;

            Assert.That(ExperimentHistoryTableLayout.EntriesPerPage, Is.EqualTo(7));
            Assert.That(ExperimentHistoryTableLayout.PaginationHeight, Is.EqualTo(48f));
            Assert.That(ExperimentHistoryTableLayout.PaginationHeight, Is.GreaterThanOrEqualTo(44f));
            Assert.That(rowsHeight, Is.LessThanOrEqualTo(viewportContentHeight));
            Assert.That(viewportGrowth, Is.LessThanOrEqualTo(heightRecovered));
        }

        [TestCase(ExperimentSessionIdHistoryStore.ClosedWithoutCompletionStatus)]
        [TestCase(ExperimentSessionIdHistoryStore.SupersededAfterSavedExitStatus)]
        public void LongStatusUsesDedicatedWidestColumnWithoutChangingGrid(string status)
        {
            ExperimentHistoryColumnDefinition state = ExperimentHistoryTableLayout.Get(ExperimentHistoryColumn.Status);
            float maximumOtherWidth = ExperimentHistoryTableLayout.Columns
                .Where(column => column.Column != ExperimentHistoryColumn.Status)
                .Max(column => column.Width);

            Assert.That(status, Is.Not.Empty);
            Assert.That(state.Width, Is.GreaterThan(maximumOtherWidth));
            Assert.That(state.AllowAutoSize, Is.True);
            Assert.That(state.MinimumFontSize, Is.EqualTo(14));
            Assert.That(ExperimentHistoryTableLayout.TotalColumnWidth, Is.EqualTo(784f));
        }

        [Test]
        public void RuntimeTableDisablesWrappingAndContentDrivenWidths()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "ExperimentRuntimeStartScreenUI.cs"));

            StringAssert.Contains("AddHistoryCells(row, null, isHeader: true)", source);
            StringAssert.Contains("AddHistoryCells(row, entry, isHeader: false)", source);
            StringAssert.Contains("text.textWrappingMode = TextWrappingModes.NoWrap", source);
            StringAssert.Contains("text.alignment = TextAlignmentOptions.MidlineLeft", source);
            StringAssert.Contains("cellElement.preferredWidth = definition.Width", source);
            StringAssert.Contains("cellElement.flexibleWidth = 0f", source);
            StringAssert.Contains("rowElement.preferredHeight = ExperimentHistoryTableLayout.RowHeight", source);
            StringAssert.Contains("rowElement.preferredHeight = ExperimentHistoryTableLayout.PaginationHeight", source);
            StringAssert.Contains("buttonElement.preferredHeight = ExperimentHistoryTableLayout.PaginationHeight", source);
            StringAssert.Contains("indicatorElement.preferredHeight = ExperimentHistoryTableLayout.PaginationHeight", source);
            StringAssert.Contains("layout.childForceExpandHeight = false", source);
            StringAssert.DoesNotMatch(@"AddHistoryCell\([^\n]+,\s*[0-9.]+f\)", source);
        }

        [Test]
        public void HeaderIsCreatedBeforeScrollableDataRows()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "ExperimentRuntimeStartScreenUI.cs"));

            StringAssert.IsMatch(
                @"AddHistoryHeader\(\);\s*RectTransform scrollContent = CreateHistoryScrollView\(page\);",
                source);
        }

        [Test]
        public void PaginationRolesRemainP46J03R2Semantics()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "ExperimentRuntimeStartScreenUI.cs"));

            StringAssert.IsMatch(
                @"PreviousHistoryPagePressed,\s*ExperimentButtonRole\.Secondary,\s*page\.HasPreviousPage",
                source);
            StringAssert.IsMatch(
                @"NextHistoryPagePressed,\s*ExperimentButtonRole\.Primary,\s*page\.HasNextPage",
                source);
        }
    }
}
