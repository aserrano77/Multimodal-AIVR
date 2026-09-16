using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Autonomy.Domain
{
    public sealed class VoiceConfirmationResponseParser
    {
        private static readonly HashSet<string> Affirmatives = new(StringComparer.Ordinal)
        {
            "si",
            "confirma",
            "confirmo",
            "adelante",
            "ejecuta",
            "correcto"
        };

        private static readonly HashSet<string> Negatives = new(StringComparer.Ordinal)
        {
            "no",
            "cancelar",
            "cancela",
            "olvida",
            "anula"
        };

        public VoiceConfirmationResponse Parse(string transcript)
        {
            string cleaned = Clean(transcript);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return VoiceConfirmationResponse.None;
            }

            return Affirmatives.Contains(cleaned)
                ? VoiceConfirmationResponse.Affirmative
                : Negatives.Contains(cleaned)
                    ? VoiceConfirmationResponse.Negative
                    : VoiceConfirmationResponse.None;
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string lower = RemoveDiacritics(text.Trim().ToLowerInvariant());
            StringBuilder builder = new();
            foreach (char ch in lower)
            {
                builder.Append(char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' ');
            }

            return string.Join(" ", builder.ToString().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string RemoveDiacritics(string text)
        {
            string normalized = text.Normalize(NormalizationForm.FormD);
            StringBuilder builder = new();
            foreach (char ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
