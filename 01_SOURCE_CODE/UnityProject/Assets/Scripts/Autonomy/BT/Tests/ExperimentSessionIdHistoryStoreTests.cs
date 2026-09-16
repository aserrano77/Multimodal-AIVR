using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentSessionIdHistoryStoreTests
    {
        private static readonly string[] DefaultOrder =
        {
            ExperimentCompensatedConditionOrder.C00,
            ExperimentCompensatedConditionOrder.C10,
            ExperimentCompensatedConditionOrder.C11
        };

        private static readonly object[] PrefixOrderCases =
        {
            new object[] { 'A', new[] { ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C11 } },
            new object[] { 'B', new[] { ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C10 } },
            new object[] { 'C', new[] { ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C11 } },
            new object[] { 'D', new[] { ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C00 } },
            new object[] { 'E', new[] { ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C10 } },
            new object[] { 'F', new[] { ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C00 } }
        };

        private string _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "ExperimentSessionIdHistoryStoreTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            ExperimentSessionIdHistoryStore.ForceSaveFailureForTests(false);
            ExperimentSessionIdHistoryStore.OverrideQuestionnaireCodeGeneratorForTests(null);
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(null);
            ExperimentDataPathResolver.ResetForTests(null);
            if (!string.IsNullOrWhiteSpace(_tempDirectory) && Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void LoadRecentReturnsEmptyHistoryWhenFileDoesNotExist()
        {
            string path = Path.Combine(_tempDirectory, "missing_history.json");
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(path);

            IReadOnlyList<ExperimentSessionIdHistoryEntry> loaded = ExperimentSessionIdHistoryStore.LoadRecent();

            Assert.That(loaded, Is.Empty);
        }

        [TestCaseSource(nameof(PrefixOrderCases))]
        public void QuestionnaireCodePrefixMapsExactSupportedOrder(char prefix, string[] order)
        {
            Assert.That(QuestionnaireCodeCodec.TryGetPrefixForOrder(order, out char actual), Is.True);
            Assert.That(actual, Is.EqualTo(prefix));
        }

        [TestCaseSource(nameof(PrefixOrderCases))]
        public void QuestionnaireCodePrefixDecodesExactSupportedOrder(char prefix, string[] order)
        {
            Assert.That(QuestionnaireCodeCodec.TryGetOrderForPrefix(prefix, out string[] actual), Is.True);
            Assert.That(actual, Is.EqualTo(order));
        }

        [TestCase("A7K3MW", 'A')]
        [TestCase("C7K3M4", 'C')]
        [TestCase("F23452", 'F')]
        [TestCase("BZZZZA", 'B')]
        [TestCase("EABCD8", 'E')]
        public void QuestionnaireCodeContractVectorsValidateAndDecode(string code, char expectedPrefix)
        {
            Assert.That(QuestionnaireCodeCodec.TryValidate(code, out string normalized, out string error), Is.True, error);
            Assert.That(normalized, Is.EqualTo(code));
            Assert.That(
                QuestionnaireCodeCodec.TryDecodeOrder(code, out string[] decoded, out _, out error),
                Is.True,
                error);
            Assert.That(QuestionnaireCodeCodec.TryGetOrderForPrefix(expectedPrefix, out string[] expected), Is.True);
            Assert.That(decoded, Is.EqualTo(expected));
        }

        [Test]
        public void QuestionnaireCodeRequiredExamplesDecodeSpecifiedOrders()
        {
            Assert.That(QuestionnaireCodeCodec.TryDecodeOrder("C7K3M4", out string[] cOrder, out _, out string cError), Is.True, cError);
            Assert.That(cOrder.Select(ExperimentCompensatedConditionOrder.ToShortCode),
                Is.EqualTo(new[] { "C10", "C00", "C11" }));
            Assert.That(QuestionnaireCodeCodec.TryDecodeOrder("A7K3MW", out string[] aOrder, out _, out string aError), Is.True, aError);
            Assert.That(aOrder.Select(ExperimentCompensatedConditionOrder.ToShortCode),
                Is.EqualTo(new[] { "C00", "C10", "C11" }));
        }

        [TestCase(null, QuestionnaireCodeCodec.ErrorNullOrEmpty)]
        [TestCase("", QuestionnaireCodeCodec.ErrorNullOrEmpty)]
        [TestCase("A7K3M", QuestionnaireCodeCodec.ErrorInvalidLength)]
        [TestCase("A7K3MWW", QuestionnaireCodeCodec.ErrorInvalidLength)]
        [TestCase("G7K3MW", QuestionnaireCodeCodec.ErrorInvalidPrefix)]
        [TestCase("A0K3MW", QuestionnaireCodeCodec.ErrorInvalidCharacter)]
        [TestCase("A1K3MW", QuestionnaireCodeCodec.ErrorInvalidCharacter)]
        [TestCase("AIK3MW", QuestionnaireCodeCodec.ErrorInvalidCharacter)]
        [TestCase("ALK3MW", QuestionnaireCodeCodec.ErrorInvalidCharacter)]
        [TestCase("AOK3MW", QuestionnaireCodeCodec.ErrorInvalidCharacter)]
        [TestCase("A*K3MW", QuestionnaireCodeCodec.ErrorInvalidCharacter)]
        [TestCase("A7 K3MW", QuestionnaireCodeCodec.ErrorInvalidLength)]
        [TestCase("A7K3M2", QuestionnaireCodeCodec.ErrorChecksumMismatch)]
        public void QuestionnaireCodeRejectsInvalidInput(string code, string expectedError)
        {
            Assert.That(QuestionnaireCodeCodec.TryValidate(code, out _, out string error), Is.False);
            Assert.That(error, Is.EqualTo(expectedError));
        }

        [Test]
        public void QuestionnaireCodeNormalizesLowercaseAndOuterWhitespace()
        {
            Assert.That(QuestionnaireCodeCodec.TryValidate("  c7k3m4  ", out string normalized, out string error), Is.True, error);
            Assert.That(normalized, Is.EqualTo("C7K3M4"));
        }

        [Test]
        public void QuestionnaireCodeDetectsEverySinglePositionModification()
        {
            string[] validVectors = { "A7K3MW", "C7K3M4", "F23452", "BZZZZA", "EABCD8" };
            foreach (string valid in validVectors)
            {
                for (int position = 0; position < valid.Length; position++)
                {
                    char[] changed = valid.ToCharArray();
                    int currentIndex = QuestionnaireCodeCodec.Alphabet.IndexOf(changed[position]);
                    changed[position] = QuestionnaireCodeCodec.Alphabet[(currentIndex + 1) % QuestionnaireCodeCodec.Alphabet.Length];
                    Assert.That(
                        QuestionnaireCodeCodec.TryValidate(new string(changed), out _, out _),
                        Is.False,
                        valid + " position=" + position);
                }
            }
        }

        [TestCaseSource(nameof(PrefixOrderCases))]
        public void QuestionnaireCodeGenerationHasCorrectPrefixAlphabetChecksumAndOrder(char prefix, string[] order)
        {
            for (int sample = 0; sample < 20; sample++)
            {
                string code = QuestionnaireCodeCodec.GenerateForOrder(order);
                Assert.That(code, Has.Length.EqualTo(QuestionnaireCodeCodec.CodeLength));
                Assert.That(code[0], Is.EqualTo(prefix));
                Assert.That(code.All(ch => QuestionnaireCodeCodec.Alphabet.IndexOf(ch) >= 0), Is.True);
                Assert.That(code[5], Is.EqualTo(QuestionnaireCodeCodec.CalculateChecksum(code.Substring(0, 5))));
                Assert.That(QuestionnaireCodeCodec.TryDecodeOrder(code, out string[] decoded, out _, out string error), Is.True, error);
                Assert.That(decoded, Is.EqualTo(order));
            }
        }

        [Test]
        public void QuestionnaireCodeGenerationRejectsInvalidExperimentalOrder()
        {
            string[] invalid = { ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C11 };
            Assert.That(QuestionnaireCodeCodec.TryGetPrefixForOrder(invalid, out _), Is.False);
            Assert.Throws<ArgumentException>(() => QuestionnaireCodeCodec.GenerateForOrder(invalid));
        }

        [Test]
        public void SaveAndLoadSortsNewestSessionFirst()
        {
            string path = Path.Combine(_tempDirectory, "history.json");
            var entries = new List<ExperimentSessionIdHistoryEntry>
            {
                ExperimentSessionIdHistoryEntry.Create("U20260101_100000", "S20260101_100000", "AAAAAA", "2026-01-01T10:00:00.0000000Z", "started", 1),
                ExperimentSessionIdHistoryEntry.Create("U20260102_100000", "S20260102_100000", "BBBBBB", "2026-01-02T10:00:00.0000000Z", "completed", 1)
            };

            ExperimentSessionIdHistoryStore.Save(path, entries);
            List<ExperimentSessionIdHistoryEntry> loaded = ExperimentSessionIdHistoryStore.Load(path);

            Assert.That(loaded, Has.Count.EqualTo(2));
            Assert.That(loaded[0].session_id, Is.EqualTo("S20260102_100000"));
            Assert.That(loaded[1].session_id, Is.EqualTo("S20260101_100000"));
        }

        [Test]
        public void RegisterSessionStartedPersistsOneEntryWithQuestionnaireCode()
        {
            string path = Path.Combine(_tempDirectory, "history.json");
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(path);

            ExperimentSessionIdHistoryEntry created = ExperimentSessionIdHistoryStore.RegisterSessionStarted(
                "U20260705_101010",
                "S20260705_101010",
                "2026-07-05T10:10:10.0000000Z",
                DefaultOrder);
            List<ExperimentSessionIdHistoryEntry> loaded = ExperimentSessionIdHistoryStore.Load(path);

            Assert.That(File.Exists(path), Is.True);
            Assert.That(loaded, Has.Count.EqualTo(1));
            Assert.That(loaded[0].session_id, Is.EqualTo("S20260705_101010"));
            Assert.That(loaded[0].user_id, Is.EqualTo("U20260705_101010"));
            Assert.That(loaded[0].questionnaire_code, Is.EqualTo(created.questionnaire_code));
            Assert.That(loaded[0].questionnaire_code, Has.Length.EqualTo(QuestionnaireCodeCodec.CodeLength));
            Assert.That(loaded[0].questionnaire_code_scheme, Is.EqualTo(QuestionnaireCodeCodec.Scheme));
            Assert.That(loaded[0].condition_order_ids, Is.EqualTo(DefaultOrder));
            Assert.That(loaded[0].status, Is.EqualTo(ExperimentSessionIdHistoryStore.StartedStatus));
        }

        [Test]
        public void RegisterSessionStartedRegeneratesAfterSimulatedCollision()
        {
            string path = Path.Combine(_tempDirectory, "history.json");
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(path);
            const string collision = "A7K3MW";
            string replacementPayload = "A2345";
            string replacement = replacementPayload + QuestionnaireCodeCodec.CalculateChecksum(replacementPayload);
            ExperimentSessionIdHistoryStore.Save(
                path,
                new List<ExperimentSessionIdHistoryEntry>
                {
                    ExperimentSessionIdHistoryEntry.Create(
                        "U_OLD",
                        "S_OLD",
                        collision,
                        "2026-07-01T10:00:00.0000000Z",
                        ExperimentSessionIdHistoryStore.CompletedStatus,
                        1,
                        QuestionnaireCodeCodec.Scheme,
                        DefaultOrder)
                });
            int generationCount = 0;
            ExperimentSessionIdHistoryStore.OverrideQuestionnaireCodeGeneratorForTests(_ =>
                generationCount++ == 0 ? collision : replacement);

            ExperimentSessionIdHistoryEntry created = ExperimentSessionIdHistoryStore.RegisterSessionStarted(
                "U_NEW",
                "S_NEW",
                "2026-07-02T10:00:00.0000000Z",
                DefaultOrder);

            Assert.That(generationCount, Is.EqualTo(2));
            Assert.That(created.questionnaire_code, Is.EqualTo(replacement));
            Assert.That(ExperimentSessionIdHistoryStore.Load(path).Select(entry => entry.questionnaire_code),
                Is.EquivalentTo(new[] { collision, replacement }));
        }

        [Test]
        public void RegisterSessionStartedStopsAtCollisionRetryLimitWithoutPersistingDuplicate()
        {
            string path = Path.Combine(_tempDirectory, "history.json");
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(path);
            const string collision = "A7K3MW";
            ExperimentSessionIdHistoryStore.Save(
                path,
                new List<ExperimentSessionIdHistoryEntry>
                {
                    ExperimentSessionIdHistoryEntry.Create(
                        "U_OLD",
                        "S_OLD",
                        collision,
                        "2026-07-01T10:00:00.0000000Z",
                        ExperimentSessionIdHistoryStore.CompletedStatus,
                        1,
                        QuestionnaireCodeCodec.Scheme,
                        DefaultOrder)
                });
            ExperimentSessionIdHistoryStore.OverrideQuestionnaireCodeGeneratorForTests(_ => collision);
            LogAssert.Expect(LogType.Error, new Regex("collision_retry_limit_exceeded"));

            Assert.Throws<InvalidOperationException>(() => ExperimentSessionIdHistoryStore.RegisterSessionStarted(
                "U_NEW",
                "S_NEW",
                "2026-07-02T10:00:00.0000000Z",
                DefaultOrder));
            Assert.That(ExperimentSessionIdHistoryStore.Load(path), Has.Count.EqualTo(1));
        }

        [Test]
        public void NewAndLegacyHistoryDocumentsDeserializeWithoutChangingCodes()
        {
            string newPath = Path.Combine(_tempDirectory, "new_history.json");
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(newPath);
            ExperimentSessionIdHistoryEntry created = ExperimentSessionIdHistoryStore.RegisterSessionStarted(
                "U_NEW",
                "S_NEW",
                "2026-07-03T10:00:00.0000000Z",
                DefaultOrder);
            ExperimentSessionIdHistoryEntry reloadedNew = ExperimentSessionIdHistoryStore.Load(newPath).Single();
            Assert.That(reloadedNew.questionnaire_code, Is.EqualTo(created.questionnaire_code));
            Assert.That(reloadedNew.questionnaire_code_scheme, Is.EqualTo(QuestionnaireCodeCodec.Scheme));
            Assert.That(reloadedNew.condition_order_ids, Is.EqualTo(DefaultOrder));
            Assert.That(QuestionnaireCodeCodec.TryDecodeOrder(reloadedNew.questionnaire_code, out string[] decoded, out _, out string error), Is.True, error);
            Assert.That(decoded, Is.EqualTo(reloadedNew.condition_order_ids));

            string legacyPath = Path.Combine(_tempDirectory, "legacy_history.json");
            File.WriteAllText(
                legacyPath,
                "{\"schema_version\":2,\"sessions\":[{\"session_id\":\"S_LEGACY\",\"user_id\":\"U_LEGACY\",\"questionnaire_code\":\"01ILOA\",\"condition_order_ids\":[\"C00_robot_off_voice_off\",\"C10_robot_on_voice_off\",\"C11_robot_on_voice_on\"]}]}" );
            ExperimentSessionIdHistoryEntry legacy = ExperimentSessionIdHistoryStore.Load(legacyPath).Single();
            Assert.That(legacy.questionnaire_code, Is.EqualTo("01ILOA"));
            Assert.That(legacy.questionnaire_code_scheme, Is.Empty);
            Assert.That(legacy.condition_order_ids, Is.EqualTo(DefaultOrder));
        }

        [Test]
        public void UpdateSessionStatusPersistsSavedExit()
        {
            string path = Path.Combine(_tempDirectory, "history.json");
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(path);
            ExperimentSessionIdHistoryStore.RegisterSessionStarted(
                "U20260705_111111",
                "S20260705_111111",
                "2026-07-05T11:11:11.0000000Z",
                DefaultOrder);

            ExperimentSessionIdHistoryStore.UpdateSessionStatus("S20260705_111111", ExperimentSessionIdHistoryStore.SavedExitStatus);
            List<ExperimentSessionIdHistoryEntry> loaded = ExperimentSessionIdHistoryStore.Load(path);

            Assert.That(loaded, Has.Count.EqualTo(1));
            Assert.That(loaded[0].status, Is.EqualTo(ExperimentSessionIdHistoryStore.SavedExitStatus));
            Assert.That(loaded[0].questionnaire_code, Is.Not.Empty);
        }

        [Test]
        public void LaterAttemptAndStatusUpdatesCannotMutateEarlierSessionIdentity()
        {
            string path = Path.Combine(_tempDirectory, "history.json");
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(path);
            string[] orderD =
            {
                ExperimentCompensatedConditionOrder.C10,
                ExperimentCompensatedConditionOrder.C11,
                ExperimentCompensatedConditionOrder.C00
            };
            ExperimentSessionIdHistoryEntry sessionD = ExperimentSessionIdHistoryStore.RegisterSessionStarted(
                "U_D",
                "S_D",
                "2026-07-17T23:56:11.0000000Z",
                orderD);
            Assert.That(ExperimentSessionIdHistoryStore.UpdateSavedExitCheckpoint(
                sessionD.session_id,
                orderD[0],
                1,
                1,
                string.Join(",", orderD),
                "condition_start",
                orderD,
                0), Is.True);
            Assert.That(ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                sessionD.session_id,
                ExperimentSessionIdHistoryStore.SavedExitStatus,
                "test_saved_exit",
                out _), Is.True);

            ExperimentSessionIdHistoryStore.RegisterSessionStarted(
                "U_A",
                "S_A",
                "2026-07-17T23:57:06.0000000Z",
                DefaultOrder);
            Assert.That(ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                sessionD.session_id,
                ExperimentSessionIdHistoryStore.SupersededAfterSavedExitStatus,
                "test_new_attempt",
                out _), Is.True);

            ExperimentSessionIdHistoryEntry persistedD = ExperimentSessionIdHistoryStore.Load(path)
                .Single(entry => entry.session_id == sessionD.session_id);
            Assert.That(persistedD.user_id, Is.EqualTo(sessionD.user_id));
            Assert.That(persistedD.questionnaire_code, Is.EqualTo(sessionD.questionnaire_code));
            Assert.That(persistedD.questionnaire_code_scheme, Is.EqualTo(QuestionnaireCodeCodec.Scheme));
            Assert.That(persistedD.condition_order_ids, Is.EqualTo(orderD));
            Assert.That(persistedD.status, Is.EqualTo(ExperimentSessionIdHistoryStore.SupersededAfterSavedExitStatus));
        }

        [Test]
        public void CheckpointWithDifferentOrderIsRejectedWithoutChangingIdentity()
        {
            string path = Path.Combine(_tempDirectory, "history.json");
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(path);
            string[] orderD =
            {
                ExperimentCompensatedConditionOrder.C10,
                ExperimentCompensatedConditionOrder.C11,
                ExperimentCompensatedConditionOrder.C00
            };
            ExperimentSessionIdHistoryEntry session = ExperimentSessionIdHistoryStore.RegisterSessionStarted(
                "U_D",
                "S_D",
                "2026-07-17T23:56:11.0000000Z",
                orderD);
            LogAssert.Expect(LogType.Error, new Regex("condition_order_identity_mismatch"));

            bool updated = ExperimentSessionIdHistoryStore.UpdateSavedExitCheckpoint(
                session.session_id,
                DefaultOrder[0],
                1,
                1,
                string.Join(",", DefaultOrder),
                "condition_start",
                DefaultOrder,
                0);

            Assert.That(updated, Is.False);
            ExperimentSessionIdHistoryEntry persisted = ExperimentSessionIdHistoryStore.Load(path).Single();
            Assert.That(persisted.questionnaire_code, Is.EqualTo(session.questionnaire_code));
            Assert.That(persisted.questionnaire_code_scheme, Is.EqualTo(session.questionnaire_code_scheme));
            Assert.That(persisted.condition_order_ids, Is.EqualTo(orderD));
            Assert.That(persisted.saved_exit_checkpoint_schema_version, Is.Zero);
        }

        [Test]
        public void CompletedStatusIsTerminalAndCannotBecomeSavedOrSuperseded()
        {
            string path = Path.Combine(_tempDirectory, "history.json");
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(path);
            ExperimentSessionIdHistoryEntry session = ExperimentSessionIdHistoryStore.RegisterSessionStarted(
                "U_COMPLETED",
                "S_COMPLETED",
                "2026-07-17T23:56:11.0000000Z",
                DefaultOrder);
            Assert.That(ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                session.session_id,
                ExperimentSessionIdHistoryStore.CompletedStatus,
                "test_complete",
                out _), Is.True);
            LogAssert.Expect(LogType.Error, new Regex("current_status_is_terminal"));
            Assert.That(ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                session.session_id,
                ExperimentSessionIdHistoryStore.SavedExitStatus,
                "test_invalid_save",
                out _), Is.False);
            LogAssert.Expect(LogType.Error, new Regex("current_status_is_terminal"));
            Assert.That(ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                session.session_id,
                ExperimentSessionIdHistoryStore.SupersededAfterSavedExitStatus,
                "test_invalid_supersede",
                out _), Is.False);

            ExperimentSessionIdHistoryEntry persisted = ExperimentSessionIdHistoryStore.Load(path).Single();
            Assert.That(persisted.status, Is.EqualTo(ExperimentSessionIdHistoryStore.CompletedStatus));
            Assert.That(persisted.completed_at_iso, Is.Not.Empty);
            Assert.That(ExperimentSavedExitResumePromptState.TryFindPendingSession(new[] { persisted }, out _), Is.False);
        }

        [Test]
        public void SupersededStatusRequiresSavedExit()
        {
            Assert.That(ExperimentSessionIdHistoryStore.TryTransitionSessionStatus(
                ExperimentSessionIdHistoryStore.ActiveStatus,
                ExperimentSessionIdHistoryStore.SupersededAfterSavedExitStatus,
                "test",
                out _,
                out string error), Is.False);
            Assert.That(error, Is.EqualTo("transition_not_allowed"));
            Assert.That(ExperimentSessionIdHistoryStore.TryTransitionSessionStatus(
                ExperimentSessionIdHistoryStore.SavedExitStatus,
                ExperimentSessionIdHistoryStore.SupersededAfterSavedExitStatus,
                "test",
                out string result,
                out error), Is.True, error);
            Assert.That(result, Is.EqualTo(ExperimentSessionIdHistoryStore.SupersededAfterSavedExitStatus));
        }

        [Test]
        public void LoadReturnsEmptyHistoryForCorruptJson()
        {
            string path = Path.Combine(_tempDirectory, "history.json");
            File.WriteAllText(path, "{ not valid json");

            List<ExperimentSessionIdHistoryEntry> loaded = ExperimentSessionIdHistoryStore.Load(path);

            Assert.That(loaded, Is.Empty);
        }

        [Test]
        public void ConfigureRuntimeButtonReplacesExistingRuntimeListenerWithOneFunctionalListener()
        {
            var target = new GameObject("Button_HistorialSesiones");
            try
            {
                Image image = target.AddComponent<Image>();
                Button button = target.AddComponent<Button>();
                int invokeCount = 0;

                int firstCount = ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, () => invokeCount++, ExperimentButtonRole.Secondary, true);
                int secondCount = ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, () => invokeCount++, ExperimentButtonRole.Secondary, true);
                button.onClick.Invoke();

                Assert.That(firstCount, Is.EqualTo(1));
                Assert.That(secondCount, Is.EqualTo(1));
                Assert.That(invokeCount, Is.EqualTo(1));
                Assert.That(button.interactable, Is.True);
                Assert.That(image.raycastTarget, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void ConfigureRuntimeButtonSupportsHistoryBackButtonWithoutDuplicateCallbacks()
        {
            var target = new GameObject("Button_VolverHistorial");
            try
            {
                Image image = target.AddComponent<Image>();
                Button button = target.AddComponent<Button>();
                int invokeCount = 0;

                ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, () => invokeCount += 10, ExperimentButtonRole.Secondary, true);
                int listenerCount = ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, () => invokeCount++, ExperimentButtonRole.Secondary, true);
                button.onClick.Invoke();

                Assert.That(listenerCount, Is.EqualTo(1));
                Assert.That(invokeCount, Is.EqualTo(1));
                Assert.That(button.interactable, Is.True);
                Assert.That(image.raycastTarget, Is.True);
                Assert.That(target.name, Is.EqualTo("Button_VolverHistorial"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void SemanticButtonPaletteBuildsApprovedColorBlocksForEveryRole()
        {
            AssertRolePalette(
                ExperimentButtonRole.Primary,
                new Color32(0x00, 0x6E, 0xA6, 0xFF),
                new Color32(0x00, 0x77, 0xB6, 0xFF),
                new Color32(0x00, 0x5B, 0x8C, 0xFF));
            AssertRolePalette(
                ExperimentButtonRole.Secondary,
                new Color32(0x4B, 0x55, 0x63, 0xFF),
                new Color32(0x5B, 0x64, 0x70, 0xFF),
                new Color32(0x37, 0x41, 0x51, 0xFF));
            AssertRolePalette(
                ExperimentButtonRole.Warning,
                new Color32(0xA8, 0x57, 0x00, 0xFF),
                new Color32(0xB8, 0x5C, 0x00, 0xFF),
                new Color32(0x7D, 0x42, 0x00, 0xFF));
            AssertRolePalette(
                ExperimentButtonRole.Destructive,
                new Color32(0xB3, 0x26, 0x4A, 0xFF),
                new Color32(0xC1, 0x2C, 0x51, 0xFF),
                new Color32(0x8F, 0x1E, 0x3C, 0xFF));
        }

        [Test]
        public void SemanticRoleCallSitesUsePrimaryForForwardHistoryAndConfirmedSafeExit()
        {
            string start = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "ExperimentRuntimeStartScreenUI.cs"));
            string pause = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "ExperimentPauseMenuController.cs"));

            StringAssert.IsMatch(
                @"PreviousHistoryPagePressed,\s*ExperimentButtonRole\.Secondary,\s*page\.HasPreviousPage",
                start);
            StringAssert.IsMatch(
                @"NextHistoryPagePressed,\s*ExperimentButtonRole\.Primary,\s*page\.HasNextPage",
                start);
            StringAssert.IsMatch(
                @"ConfirmSaveAndExit,\s*ExperimentButtonRole\.Primary,\s*true",
                pause);
            StringAssert.IsMatch(
                @"ConfirmRestart,\s*ExperimentButtonRole\.Warning,\s*true",
                pause);
            StringAssert.IsMatch(
                @"ConfirmExitWithoutCompletion,\s*ExperimentButtonRole\.Destructive,\s*true",
                pause);
            Assert.That(Regex.Matches(pause, @"CancelConfirmation\(\);[\s\S]{0,500}?ExperimentButtonRole\.Secondary").Count,
                Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void DisabledForwardPaginationKeepsPrimaryRoleButUsesDisabledState()
        {
            var target = new GameObject("Button_HistorialSiguiente");
            try
            {
                Image image = target.AddComponent<Image>();
                Button button = target.AddComponent<Button>();

                ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(
                    button,
                    image,
                    () => { },
                    ExperimentButtonRole.Primary,
                    false);

                Assert.That(button.interactable, Is.False);
                Assert.That((Color32)button.colors.normalColor, Is.EqualTo(new Color32(0x00, 0x6E, 0xA6, 0xFF)));
                Assert.That((Color32)button.colors.disabledColor, Is.EqualTo(new Color32(0x3F, 0x44, 0x4B, 0xA6)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void ConfigureRuntimeButtonUsesWhiteBaseImageAndPreservesNavigationAndMaterial()
        {
            var target = new GameObject("Button_SemanticPalette");
            try
            {
                Image image = target.AddComponent<Image>();
                image.color = new Color(0.25f, 0.40f, 0.75f, 1f);
                Material originalMaterial = image.material;
                Button button = target.AddComponent<Button>();
                Navigation originalNavigation = new Navigation { mode = Navigation.Mode.Explicit };
                button.navigation = originalNavigation;

                ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(
                    button,
                    image,
                    () => { },
                    ExperimentButtonRole.Warning,
                    false);

                Assert.That((Color32)image.color, Is.EqualTo(new Color32(0xFF, 0xFF, 0xFF, 0xFF)));
                Assert.That(image.material, Is.SameAs(originalMaterial));
                Assert.That(button.targetGraphic, Is.SameAs(image));
                Assert.That(button.transition, Is.EqualTo(Selectable.Transition.ColorTint));
                Assert.That(button.navigation.mode, Is.EqualTo(Navigation.Mode.Explicit));
                Assert.That(button.interactable, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static void AssertRolePalette(
            ExperimentButtonRole role,
            Color32 expectedNormal,
            Color32 expectedHighlighted,
            Color32 expectedPressed)
        {
            ColorBlock colors = ExperimentRuntimeButtonUtility.BuildColorBlock(role);
            Assert.That((Color32)colors.normalColor, Is.EqualTo(expectedNormal), $"{role} normal");
            Assert.That((Color32)colors.highlightedColor, Is.EqualTo(expectedHighlighted), $"{role} highlighted");
            Assert.That((Color32)colors.pressedColor, Is.EqualTo(expectedPressed), $"{role} pressed");
            Assert.That((Color32)colors.selectedColor, Is.EqualTo(expectedHighlighted), $"{role} selected");
            Assert.That((Color32)colors.disabledColor, Is.EqualTo(new Color32(0x3F, 0x44, 0x4B, 0xA6)), $"{role} disabled");
            Assert.That(colors.colorMultiplier, Is.EqualTo(1f));
            Assert.That(colors.fadeDuration, Is.EqualTo(0.10f).Within(0.0001f));
        }

        [Test]
        public void ConfigureSessionStopsWhenHistorySaveFailsInsteadOfUsingUnpersistedCode()
        {
            string dataRoot = Path.Combine(_tempDirectory, "ExperimentData");
            ExperimentDataPathResolver.ResetForTests(dataRoot);
            ExperimentSessionIdHistoryStore.ForceSaveFailureForTests(true);
            LogAssert.Expect(LogType.Error, new Regex("configure_session_history_register_failed"));

            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                string.Empty,
                string.Empty,
                new[] { "C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on" },
                1);

            Assert.That(context.SessionRoot, Is.Null.Or.Empty);
            Assert.That(context.QuestionnaireCode, Is.Null.Or.Empty);
            Assert.That(ExperimentDataPathResolver.HasActiveSession, Is.False);
            Assert.That(File.Exists(ExperimentSessionIdHistoryStore.DefaultPath), Is.False);
        }

        [TestCase(0, 0, 0, 0, 0, false, false)]
        [TestCase(1, 0, 1, 0, 1, false, false)]
        [TestCase(6, 0, 1, 0, 6, false, false)]
        [TestCase(7, 0, 2, 0, 6, false, true)]
        [TestCase(7, 1, 2, 6, 7, true, false)]
        [TestCase(50, 8, 9, 48, 50, true, false)]
        public void ResolvePageCalculatesExpectedHistoryRanges(
            int totalEntries,
            int requestedPageIndex,
            int expectedPageCount,
            int expectedStartIndex,
            int expectedEndExclusive,
            bool expectedPrevious,
            bool expectedNext)
        {
            ExperimentSessionHistoryPage page = ExperimentSessionIdHistoryStore.ResolvePage(totalEntries, requestedPageIndex, 6);

            Assert.That(page.TotalEntries, Is.EqualTo(totalEntries));
            Assert.That(page.PageCount, Is.EqualTo(expectedPageCount));
            Assert.That(page.StartIndex, Is.EqualTo(expectedStartIndex));
            Assert.That(page.EndExclusive, Is.EqualTo(expectedEndExclusive));
            Assert.That(page.HasPreviousPage, Is.EqualTo(expectedPrevious));
            Assert.That(page.HasNextPage, Is.EqualTo(expectedNext));
        }

        [Test]
        public void ResolvePageClampsOutsideHistoryRange()
        {
            ExperimentSessionHistoryPage beforeFirst = ExperimentSessionIdHistoryStore.ResolvePage(7, -5, 6);
            ExperimentSessionHistoryPage afterLast = ExperimentSessionIdHistoryStore.ResolvePage(7, 50, 6);

            Assert.That(beforeFirst.PageIndex, Is.EqualTo(0));
            Assert.That(beforeFirst.StartIndex, Is.EqualTo(0));
            Assert.That(beforeFirst.EndExclusive, Is.EqualTo(6));
            Assert.That(afterLast.PageIndex, Is.EqualTo(1));
            Assert.That(afterLast.StartIndex, Is.EqualTo(6));
            Assert.That(afterLast.EndExclusive, Is.EqualTo(7));
        }
    }
}
