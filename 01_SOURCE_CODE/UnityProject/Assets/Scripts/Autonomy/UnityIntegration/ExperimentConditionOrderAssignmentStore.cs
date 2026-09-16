using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autonomy.Domain;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    [Serializable]
    public sealed class ExperimentConditionOrderAssignment
    {
        public string participation_id = string.Empty;
        public string sequence_prefix = string.Empty;
        public List<string> condition_order_ids = new List<string>();
        public int block_index;
        public int block_position;
        public bool consumed_block_position;
        public string assignment_source = string.Empty;
        public string assigned_at_iso = string.Empty;
    }

    public static class ExperimentConditionOrderAssignmentStore
    {
        public const int CurrentSchemaVersion = 1;
        public const string Algorithm = "randomized_complete_blocks_v1";

        private const string FolderName = "ExperimentData";
        private const string FileName = "condition_order_assignments.json";
        private static readonly object Sync = new object();
        private static readonly string[] Prefixes = { "A", "B", "C", "D", "E", "F" };
        private static string s_defaultPathOverride;
#if UNITY_INCLUDE_TESTS || UNITY_EDITOR
        private static int? s_baseSeedOverride;
#endif

        public static string DefaultPath => !string.IsNullOrWhiteSpace(s_defaultPathOverride)
            ? s_defaultPathOverride
            : Path.Combine(Application.persistentDataPath, FolderName, FileName);

#if UNITY_INCLUDE_TESTS || UNITY_EDITOR
        public static void OverrideDefaultPathForTests(string path)
        {
            s_defaultPathOverride = path;
        }

        public static void OverrideBaseSeedForTests(int? baseSeed)
        {
            s_baseSeedOverride = baseSeed;
        }
#endif

        public static ExperimentConditionOrderAssignment GetOrCreate(string participationId)
        {
            string normalizedParticipationId = participationId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedParticipationId))
            {
                throw new ArgumentException("A participation id is required for condition-order assignment.", nameof(participationId));
            }

            lock (Sync)
            {
                string path = DefaultPath;
                LoadResult loaded = LoadOrCreate(path);
                ConditionOrderAssignmentDocument document = loaded.Document;
                ExperimentConditionOrderAssignment existing = document.assignments.Find(item =>
                    item != null &&
                    string.Equals(item.participation_id, normalizedParticipationId, StringComparison.Ordinal));
                if (existing != null)
                {
                    return Clone(existing);
                }

                if (document.cursor >= Prefixes.Length)
                {
                    document.block_index++;
                    document.current_block = CreateShuffledBlock(document.base_seed, document.block_index);
                    document.cursor = 0;
                }

                int blockPosition = document.cursor;
                string prefix = document.current_block[blockPosition];
                if (!QuestionnaireCodeCodec.TryGetOrderForPrefix(prefix[0], out string[] order))
                {
                    throw new InvalidDataException($"Unsupported condition-order prefix '{prefix}' in assignment state.");
                }

                var assignment = new ExperimentConditionOrderAssignment
                {
                    participation_id = normalizedParticipationId,
                    sequence_prefix = prefix,
                    condition_order_ids = order.ToList(),
                    block_index = document.block_index,
                    block_position = blockPosition,
                    consumed_block_position = true,
                    assignment_source = "new_participation",
                    assigned_at_iso = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };
                document.assignments.Add(assignment);
                document.cursor++;
                Save(path, document, backupExistingPrimary: loaded.PrimaryWasValid);
                return Clone(assignment);
            }
        }

        public static bool TryGetExisting(
            string participationId,
            out ExperimentConditionOrderAssignment assignment)
        {
            assignment = null;
            string normalizedParticipationId = participationId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedParticipationId))
            {
                return false;
            }

            lock (Sync)
            {
                string path = DefaultPath;
                if (!File.Exists(path) && !File.Exists(path + ".bak"))
                {
                    return false;
                }

                LoadResult loaded = LoadOrCreate(path);
                ExperimentConditionOrderAssignment existing = loaded.Document.assignments.Find(item =>
                    item != null &&
                    string.Equals(item.participation_id, normalizedParticipationId, StringComparison.Ordinal));
                if (existing == null)
                {
                    return false;
                }

                assignment = Clone(existing);
                return true;
            }
        }

        private static LoadResult LoadOrCreate(string path)
        {
            if (TryLoadValid(path, out ConditionOrderAssignmentDocument primary, out string primaryError))
            {
                return new LoadResult(primary, primaryWasValid: true);
            }

            string backupPath = path + ".bak";
            if (TryLoadValid(backupPath, out ConditionOrderAssignmentDocument backup, out string backupError))
            {
                return new LoadResult(backup, primaryWasValid: false);
            }

            if (!File.Exists(path) && !File.Exists(backupPath))
            {
                int baseSeed = CreateBaseSeed();
                return new LoadResult(
                    new ConditionOrderAssignmentDocument
                    {
                        schema_version = CurrentSchemaVersion,
                        algorithm = Algorithm,
                        base_seed = baseSeed,
                        block_index = 0,
                        cursor = 0,
                        current_block = CreateShuffledBlock(baseSeed, 0),
                        assignments = new List<ExperimentConditionOrderAssignment>()
                    },
                    primaryWasValid: false);
            }

            throw new InvalidDataException(
                $"Condition-order assignment state is unreadable. primary={primaryError}; backup={backupError}");
        }

        private static bool TryLoadValid(
            string path,
            out ConditionOrderAssignmentDocument document,
            out string error)
        {
            document = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "file_missing";
                return false;
            }

            try
            {
                document = JsonUtility.FromJson<ConditionOrderAssignmentDocument>(File.ReadAllText(path, Encoding.UTF8));
                return Validate(document, out error);
            }
            catch (Exception ex)
            {
                error = "read_failed:" + ex.GetType().Name;
                document = null;
                return false;
            }
        }

        private static bool Validate(ConditionOrderAssignmentDocument document, out string error)
        {
            if (document == null)
            {
                error = "document_missing";
                return false;
            }

            if (document.schema_version != CurrentSchemaVersion ||
                !string.Equals(document.algorithm, Algorithm, StringComparison.Ordinal))
            {
                error = "schema_or_algorithm_mismatch";
                return false;
            }

            if (document.base_seed <= 0 || document.block_index < 0 ||
                document.cursor < 0 || document.cursor > Prefixes.Length ||
                document.current_block == null || document.current_block.Count != Prefixes.Length ||
                !document.current_block.OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(Prefixes, StringComparer.Ordinal))
            {
                error = "block_state_invalid";
                return false;
            }

            document.assignments ??= new List<ExperimentConditionOrderAssignment>();
            var participationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ExperimentConditionOrderAssignment assignment in document.assignments)
            {
                if (assignment == null || string.IsNullOrWhiteSpace(assignment.participation_id) ||
                    !participationIds.Add(assignment.participation_id) ||
                    string.IsNullOrWhiteSpace(assignment.sequence_prefix) || assignment.sequence_prefix.Length != 1 ||
                    assignment.block_index < 0 || assignment.block_index > document.block_index ||
                    assignment.block_position < 0 || assignment.block_position >= Prefixes.Length ||
                    !assignment.consumed_block_position ||
                    !QuestionnaireCodeCodec.TryGetOrderForPrefix(assignment.sequence_prefix[0], out string[] expectedOrder) ||
                    assignment.condition_order_ids == null ||
                    !assignment.condition_order_ids.SequenceEqual(expectedOrder, StringComparer.Ordinal))
                {
                    error = "assignment_invalid";
                    return false;
                }
            }

            error = "valid";
            return true;
        }

        private static void Save(
            string path,
            ConditionOrderAssignmentDocument document,
            bool backupExistingPrimary)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = path + ".tmp";
            string backupPath = path + ".bak";
            string temporaryBackupPath = backupPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonUtility.ToJson(document, prettyPrint: true) + Environment.NewLine,
                Encoding.UTF8);
            try
            {
                if (backupExistingPrimary && File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
                        return;
                    }
                    catch (PlatformNotSupportedException)
                    {
                    }
                    catch (IOException)
                    {
                    }

                    File.Copy(path, temporaryBackupPath, overwrite: true);
                    MoveIntoPlaceAtomically(temporaryBackupPath, backupPath);
                }

                MoveIntoPlaceAtomically(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                if (File.Exists(temporaryBackupPath))
                {
                    File.Delete(temporaryBackupPath);
                }
            }
        }

        private static void MoveIntoPlaceAtomically(string sourcePath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                File.Replace(sourcePath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }

            File.Move(sourcePath, destinationPath);
        }

        private static List<string> CreateShuffledBlock(int baseSeed, int blockIndex)
        {
            var block = new List<string>(Prefixes);
            var random = new System.Random(unchecked(baseSeed + blockIndex));
            for (int i = block.Count - 1; i > 0; i--)
            {
                int swapIndex = random.Next(i + 1);
                (block[i], block[swapIndex]) = (block[swapIndex], block[i]);
            }

            return block;
        }

        private static int CreateBaseSeed()
        {
#if UNITY_INCLUDE_TESTS || UNITY_EDITOR
            if (s_baseSeedOverride.HasValue)
            {
                return Math.Max(1, s_baseSeedOverride.Value);
            }
#endif
            var bytes = new byte[4];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }

            int seed = BitConverter.ToInt32(bytes, 0) & int.MaxValue;
            return Math.Max(1, seed);
        }

        private static ExperimentConditionOrderAssignment Clone(ExperimentConditionOrderAssignment source)
        {
            return new ExperimentConditionOrderAssignment
            {
                participation_id = source.participation_id,
                sequence_prefix = source.sequence_prefix,
                condition_order_ids = source.condition_order_ids?.ToList() ?? new List<string>(),
                block_index = source.block_index,
                block_position = source.block_position,
                consumed_block_position = source.consumed_block_position,
                assignment_source = source.assignment_source,
                assigned_at_iso = source.assigned_at_iso
            };
        }

        [Serializable]
        private sealed class ConditionOrderAssignmentDocument
        {
            public int schema_version;
            public string algorithm = string.Empty;
            public int base_seed;
            public int block_index;
            public int cursor;
            public List<string> current_block = new List<string>();
            public List<ExperimentConditionOrderAssignment> assignments = new List<ExperimentConditionOrderAssignment>();
        }

        private readonly struct LoadResult
        {
            public LoadResult(ConditionOrderAssignmentDocument document, bool primaryWasValid)
            {
                Document = document;
                PrimaryWasValid = primaryWasValid;
            }

            public ConditionOrderAssignmentDocument Document { get; }
            public bool PrimaryWasValid { get; }
        }
    }
}
