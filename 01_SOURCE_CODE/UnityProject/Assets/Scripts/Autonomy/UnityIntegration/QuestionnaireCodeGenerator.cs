using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Autonomy.Domain
{
    /// <summary>
    /// Pure codec for the interoperable six-character questionnaire code scheme.
    /// Only the four middle characters are non-deterministic.
    /// </summary>
    public static class QuestionnaireCodeCodec
    {
        public const string Scheme = "order_checksum_v1";
        public const int CodeLength = 6;
        public const int RandomCharacterCount = 4;
        public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

        public const string ErrorNone = "none";
        public const string ErrorNullOrEmpty = "null_or_empty";
        public const string ErrorInvalidLength = "invalid_length";
        public const string ErrorInvalidCharacter = "invalid_character";
        public const string ErrorInvalidPrefix = "invalid_prefix";
        public const string ErrorChecksumMismatch = "checksum_mismatch";
        public const string ErrorPrefixOrderUnavailable = "prefix_order_unavailable";

        private static readonly int[] ChecksumWeights = { 3, 5, 7, 11, 13 };

        private static readonly string[][] Orders =
        {
            new[] { ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C11 },
            new[] { ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C10 },
            new[] { ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C11 },
            new[] { ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C00 },
            new[] { ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C10 },
            new[] { ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C00 }
        };

        public static bool TryGetPrefixForOrder(IReadOnlyList<string> conditionOrderIds, out char prefix)
        {
            prefix = '\0';
            if (conditionOrderIds == null || conditionOrderIds.Count != 3)
            {
                return false;
            }

            for (int orderIndex = 0; orderIndex < Orders.Length; orderIndex++)
            {
                if (!OrderMatches(conditionOrderIds, Orders[orderIndex]))
                {
                    continue;
                }

                prefix = (char)('A' + orderIndex);
                return true;
            }

            return false;
        }

        public static bool TryGetOrderForPrefix(char prefix, out string[] conditionOrderIds)
        {
            char normalizedPrefix = char.ToUpperInvariant(prefix);
            int orderIndex = normalizedPrefix - 'A';
            if (orderIndex < 0 || orderIndex >= Orders.Length)
            {
                conditionOrderIds = Array.Empty<string>();
                return false;
            }

            conditionOrderIds = (string[])Orders[orderIndex].Clone();
            return true;
        }

        public static string GenerateForOrder(IReadOnlyList<string> conditionOrderIds)
        {
            if (!TryGetPrefixForOrder(conditionOrderIds, out char prefix))
            {
                throw new ArgumentException(
                    "Condition order must exactly match one of the six supported permutations.",
                    nameof(conditionOrderIds));
            }

            var firstFive = new char[CodeLength - 1];
            firstFive[0] = prefix;
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                var buffer = new byte[4];
                for (int i = 1; i < firstFive.Length; i++)
                {
                    firstFive[i] = Alphabet[NextUniformIndex(generator, buffer, Alphabet.Length)];
                }
            }

            string payload = new string(firstFive);
            return payload + CalculateChecksum(payload);
        }

        public static char CalculateChecksum(string firstFiveCharacters)
        {
            if (firstFiveCharacters == null)
            {
                throw new ArgumentNullException(nameof(firstFiveCharacters));
            }

            if (firstFiveCharacters.Length != CodeLength - 1)
            {
                throw new ArgumentException("Checksum input must contain exactly five characters.", nameof(firstFiveCharacters));
            }

            int checksumIndex = 17;
            for (int i = 0; i < firstFiveCharacters.Length; i++)
            {
                int alphabetIndex = Alphabet.IndexOf(firstFiveCharacters[i]);
                if (alphabetIndex < 0)
                {
                    throw new ArgumentException("Checksum input contains a character outside the questionnaire alphabet.", nameof(firstFiveCharacters));
                }

                checksumIndex += ChecksumWeights[i] * alphabetIndex;
            }

            return Alphabet[checksumIndex % Alphabet.Length];
        }

        public static bool TryValidate(string code, out string normalizedCode, out string error)
        {
            normalizedCode = string.Empty;
            if (string.IsNullOrEmpty(code))
            {
                error = ErrorNullOrEmpty;
                return false;
            }

            normalizedCode = code.Trim().ToUpperInvariant();
            if (normalizedCode.Length != CodeLength)
            {
                error = ErrorInvalidLength;
                return false;
            }

            for (int i = 0; i < normalizedCode.Length; i++)
            {
                if (Alphabet.IndexOf(normalizedCode[i]) < 0)
                {
                    error = ErrorInvalidCharacter;
                    return false;
                }
            }

            char prefix = normalizedCode[0];
            if (prefix < 'A' || prefix > 'F')
            {
                error = ErrorInvalidPrefix;
                return false;
            }

            if (normalizedCode[CodeLength - 1] != CalculateChecksum(normalizedCode.Substring(0, CodeLength - 1)))
            {
                error = ErrorChecksumMismatch;
                return false;
            }

            if (!TryGetOrderForPrefix(prefix, out _))
            {
                error = ErrorPrefixOrderUnavailable;
                return false;
            }

            error = ErrorNone;
            return true;
        }

        public static bool TryDecodeOrder(
            string code,
            out string[] conditionOrderIds,
            out string normalizedCode,
            out string error)
        {
            conditionOrderIds = Array.Empty<string>();
            if (!TryValidate(code, out normalizedCode, out error))
            {
                return false;
            }

            if (TryGetOrderForPrefix(normalizedCode[0], out conditionOrderIds))
            {
                return true;
            }

            error = ErrorPrefixOrderUnavailable;
            return false;
        }

        private static bool OrderMatches(IReadOnlyList<string> candidate, IReadOnlyList<string> expected)
        {
            if (candidate == null || expected == null || candidate.Count != expected.Count)
            {
                return false;
            }

            for (int i = 0; i < expected.Count; i++)
            {
                if (!string.Equals(candidate[i], expected[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static int NextUniformIndex(RandomNumberGenerator generator, byte[] buffer, int maxExclusive)
        {
            if (generator == null)
            {
                throw new ArgumentNullException(nameof(generator));
            }

            uint bound = (uint)maxExclusive;
            uint limit = uint.MaxValue - (uint.MaxValue % bound);
            uint value;
            do
            {
                generator.GetBytes(buffer);
                value = BitConverter.ToUInt32(buffer, 0);
            }
            while (value >= limit);

            return (int)(value % bound);
        }
    }
}
