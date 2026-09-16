using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Autonomy.Domain
{
    public static class ExperimentCompensatedConditionOrder
    {
        public const string C00 = "C00_robot_off_voice_off";
        public const string C10 = "C10_robot_on_voice_off";
        public const string C11 = "C11_robot_on_voice_on";

        private static readonly string[][] LatinSquare =
        {
            new[] { C11, C00, C10 },
            new[] { C00, C10, C11 },
            new[] { C10, C11, C00 }
        };

        public static IReadOnlyList<string> BuildForParticipant(int participantId)
        {
            int normalized = Math.Abs(participantId);
            int index = normalized % 3;
            return LatinSquare[index].ToArray();
        }

        public static IReadOnlyList<string> BuildForParticipant(string participantId)
        {
            return BuildForParticipant(ParseParticipantNumber(participantId));
        }

        public static int ParseParticipantNumber(string participantId)
        {
            if (string.IsNullOrWhiteSpace(participantId))
            {
                return 1;
            }

            string digits = new string(participantId.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digits))
            {
                return 1;
            }

            if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
            {
                return value;
            }

            long accumulator = 0;
            foreach (char digit in digits)
            {
                accumulator = ((accumulator * 10) + (digit - '0')) % int.MaxValue;
            }

            return accumulator > 0 ? (int)accumulator : 1;
        }

        public static bool IsFinalWithinSubjectOrder(IReadOnlyList<string> conditionIds)
        {
            if (conditionIds == null || conditionIds.Count != 3)
            {
                return false;
            }

            var expected = new HashSet<string>(new[] { C00, C10, C11 }, StringComparer.Ordinal);
            return conditionIds.All(id => expected.Contains(id)) &&
                conditionIds.Distinct(StringComparer.Ordinal).Count() == expected.Count;
        }

        public static string FormatOrder(IReadOnlyList<string> conditionIds)
        {
            if (conditionIds == null || conditionIds.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(" -> ", conditionIds.Select(ToShortCode));
        }

        public static string ToShortCode(string conditionId)
        {
            return conditionId switch
            {
                C00 => "C00",
                C10 => "C10",
                C11 => "C11",
                _ => string.IsNullOrWhiteSpace(conditionId) ? "?" : conditionId
            };
        }
    }

    public static class ExperimentInstructionClipResolver
    {
        public static string Resolve(string conditionId, string visibleTrialLabel)
        {
            string shortCode = ExperimentCompensatedConditionOrder.ToShortCode(conditionId);
            string ordinal = string.IsNullOrWhiteSpace(visibleTrialLabel)
                ? "prueba"
                : visibleTrialLabel.Trim().ToLowerInvariant().Replace(" ", "_");
            string suffix = shortCode switch
            {
                "C00" => "manual",
                "C10" => "robot_autonomous",
                "C11" => "robot_voice",
                _ => string.Empty
            };
            return string.IsNullOrWhiteSpace(suffix) ? string.Empty : $"instruction_{ordinal}_{suffix}";
        }
    }
}
