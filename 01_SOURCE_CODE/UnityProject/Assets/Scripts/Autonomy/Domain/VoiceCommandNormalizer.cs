using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Autonomy.Domain
{
    public sealed class VoiceCommandNormalizer : IVoiceCommandNormalizer
    {
        private readonly VoiceCommandVocabulary _vocabulary;

        public VoiceCommandNormalizer()
            : this(VoiceCommandVocabulary.SpanishLogistics)
        {
        }

        public VoiceCommandNormalizer(VoiceCommandVocabulary vocabulary)
        {
            _vocabulary = vocabulary ?? VoiceCommandVocabulary.SpanishLogistics;
        }

        public VoiceCommandNormalizationResult Normalize(string rawTranscript)
        {
            string raw = rawTranscript ?? string.Empty;
            string cleaned = Clean(raw);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return Build(raw, cleaned, string.Empty, VoiceCommandRecognitionStatus.Unrecognized, 0f, string.Empty, VoiceCommandActionToken.Unknown, ObjectToken.Unknown, string.Empty, string.Empty, Array.Empty<string>(), "empty transcript");
            }

            List<string> corrections = new();
            List<string> tokens = cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            ApplyCompactPickupReconstruction(tokens, corrections);
            ApplyObservedBaseModelActionAliases(tokens, corrections);
            NormalizeCommonBoxAsrAliases(tokens, corrections);
            string joined = string.Join(" ", tokens);

            if (IsShortEspedaStop(tokens))
            {
                corrections.Add("phonetic stop alias: 'espeda' -> 'espera'");
                return Build(raw, cleaned, "espera", VoiceCommandRecognitionStatus.Recognized, 0.9f, "espera", VoiceCommandActionToken.Stop, ObjectToken.Unknown, string.Empty, string.Empty, corrections, string.Empty);
            }

            if (joined == "de tente")
            {
                corrections.Add("phonetic stop alias: 'de tente' -> 'detente'");
                return Build(raw, cleaned, "detente", VoiceCommandRecognitionStatus.Recognized, 0.84f, "detente", VoiceCommandActionToken.Stop, ObjectToken.Unknown, string.Empty, string.Empty, corrections, string.Empty);
            }

            if (_vocabulary.StopPhrases.Contains(joined))
            {
                return Build(raw, cleaned, joined, VoiceCommandRecognitionStatus.Recognized, 0.98f, joined, VoiceCommandActionToken.Stop, ObjectToken.Unknown, string.Empty, string.Empty, corrections, string.Empty);
            }

            if (joined == "para")
            {
                return Build(raw, cleaned, joined, VoiceCommandRecognitionStatus.Recognized, 0.92f, "para", VoiceCommandActionToken.Stop, ObjectToken.Unknown, string.Empty, string.Empty, corrections, string.Empty);
            }

            if (IsResumePhrase(joined))
            {
                return Build(raw, cleaned, joined, VoiceCommandRecognitionStatus.Recognized, 0.94f, "reanuda la tarea", VoiceCommandActionToken.Resume, ObjectToken.Unknown, string.Empty, string.Empty, corrections, string.Empty);
            }

            if (IsNearestAmbiguityTokens(tokens))
            {
                string nearestText = BuildNearestAmbiguityNormalizedText(tokens);
                return Build(raw, cleaned, nearestText, VoiceCommandRecognitionStatus.Recognized, 0.9f, nearestText, VoiceCommandActionToken.Unknown, ObjectToken.Box, string.Empty, string.Empty, corrections, "nearest box reference requires clarification");
            }

            VoiceCommandActionToken action = VoiceCommandActionToken.Unknown;
            string canonicalAction = string.Empty;
            int actionIndex = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (_vocabulary.PickActions.TryGetValue(tokens[i], out string pickAction))
                {
                    action = VoiceCommandActionToken.Pick;
                    canonicalAction = pickAction;
                    actionIndex = i;
                    AddCorrection(corrections, tokens[i], pickAction, "action alias");
                    tokens[i] = pickAction;
                    break;
                }

                if (_vocabulary.TransportActions.TryGetValue(tokens[i], out string transportAction))
                {
                    action = VoiceCommandActionToken.Transport;
                    canonicalAction = transportAction;
                    actionIndex = i;
                    tokens[i] = transportAction;
                    break;
                }

                if (tokens[i] == "debo" && HasTransportContext(tokens))
                {
                    action = VoiceCommandActionToken.Transport;
                    canonicalAction = "lleva";
                    actionIndex = i;
                    corrections.Add("contextual action alias: 'debo' -> 'lleva'");
                    tokens[i] = "lleva";
                    break;
                }
            }

            ObjectToken objectToken = ObjectToken.Unknown;
            int objectIndex = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (_vocabulary.ObjectAliases.TryGetValue(tokens[i], out string canonicalObject))
                {
                    objectToken = ObjectToken.Box;
                    objectIndex = i;
                    AddCorrection(corrections, tokens[i], canonicalObject, "object alias");
                    tokens[i] = canonicalObject;
                    break;
                }

                if (tokens[i] == "cafe" && action == VoiceCommandActionToken.Pick)
                {
                    objectToken = ObjectToken.Box;
                    objectIndex = i;
                    corrections.Add("contextual object alias: 'cafe' -> 'caja A'");
                    tokens[i] = "caja";
                    if (!tokens.Skip(i + 1).Any(token => _vocabulary.Labels.Contains(token)))
                    {
                        tokens.Insert(i + 1, "a");
                    }

                    break;
                }
            }

            if (objectToken == ObjectToken.Unknown && action == VoiceCommandActionToken.Transport)
            {
                for (int i = Math.Max(0, actionIndex + 1); i < tokens.Count; i++)
                {
                    if (IsNumericObjectLabel(tokens, i, out _, out _) ||
                        TryReadSpokenAlphanumericObjectLabel(tokens, i, AllowExperimentalFeToC1Fallback(tokens), out _, out _))
                    {
                        objectToken = ObjectToken.Box;
                        objectIndex = actionIndex;
                        corrections.Add("inferred box object from explicit spoken label");
                        break;
                    }
                }
            }

            string objectLabel = FindObjectLabel(tokens, objectIndex, objectToken, corrections);
            string destinationLabel = FindDestinationLabel(tokens, objectIndex, corrections);
            if (action == VoiceCommandActionToken.Transport &&
                string.IsNullOrWhiteSpace(destinationLabel) &&
                (IsAliasWithDigit(objectLabel) || IsDigits(objectLabel)))
            {
                destinationLabel = "SELF";
                corrections.Add("inferred destination SELF from explicit box label");
            }
            NormalizeDestinationAliases(tokens, corrections);

            List<string> normalizedTokens = RemoveNoise(tokens, objectLabel, destinationLabel, corrections);
            string normalizedText = BuildNormalizedText(action, canonicalAction, objectToken, objectLabel, destinationLabel, normalizedTokens);
            string canonicalPhrase = BuildCanonicalPhrase(action, canonicalAction, objectToken, objectLabel, destinationLabel);
            ScoreAndStatus(action, objectToken, objectLabel, destinationLabel, corrections, out VoiceCommandRecognitionStatus status, out float score, out string ambiguityReason);

            return Build(raw, cleaned, normalizedText, status, score, canonicalPhrase, action, objectToken, objectLabel, destinationLabel, corrections, ambiguityReason);
        }

        private static void ApplyCompactPickupReconstruction(List<string> tokens, List<string> corrections)
        {
            if (tokens.Count == 0)
            {
                return;
            }

            string first = tokens[0];
            bool hasBoxFragment = first.Contains("caza") || first.Contains("caja") || first.Contains("casa");
            bool hasPickupFragment = first.StartsWith("co", StringComparison.Ordinal) || first.Contains("coge");
            if (tokens.Count <= 2 && hasPickupFragment && hasBoxFragment)
            {
                string label = tokens.Count > 1 && IsTrailingSpokenA(tokens[tokens.Count - 1]) ? "a" : string.Empty;
                corrections.Add($"strong reconstruction: '{first}' -> 'coge la caja'");
                tokens.Clear();
                tokens.Add("coge");
                tokens.Add("la");
                tokens.Add("caja");
                if (!string.IsNullOrWhiteSpace(label))
                {
                    tokens.Add(label);
                }
            }
        }

        private static bool IsResumePhrase(string text)
        {
            switch (text)
            {
                case "reanuda":
                case "reanuda la tarea":
                case "reanuda la tarea actual":
                case "retoma":
                case "retoma la tarea":
                case "retoma la tarea actual":
                case "continua":
                case "continua la tarea":
                case "continua con la tarea":
                case "sigue":
                case "sigue con la tarea":
                    return true;
                default:
                    return false;
            }
        }

        private static void ApplyObservedBaseModelActionAliases(List<string> tokens, List<string> corrections)
        {
            if (tokens == null || tokens.Count == 0)
            {
                return;
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                if (IsObservedLlevaAlias(tokens[i]) && HasStrongTransportBoxAliasContext(tokens))
                {
                    corrections.Add($"observed transport action alias: '{tokens[i]}' -> 'lleva'");
                    tokens[i] = "lleva";
                    continue;
                }

                if (tokens[i] == "debosita" && HasBoxContext(tokens))
                {
                    corrections.Add("observed deposit action alias: 'debosita' -> 'deposita'");
                    tokens[i] = "deposita";
                }
            }

            for (int i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i] == "de" && tokens[i + 1] == "posita" && HasBoxContext(tokens))
                {
                    corrections.Add("observed deposit action alias: 'de posita' -> 'deposita'");
                    tokens[i] = "deposita";
                    tokens.RemoveAt(i + 1);
                    return;
                }
            }
        }

        private static void NormalizeCommonBoxAsrAliases(List<string> tokens, List<string> corrections)
        {
            if (tokens == null)
            {
                return;
            }

            bool hasTransportContext = tokens.Any(token =>
                token == "lleva" ||
                token == "mueve" ||
                token == "deposita" ||
                token == "deja" ||
                token == "transporta");
            if (!hasTransportContext)
            {
                return;
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] == "casa" || tokens[i] == "caza")
                {
                    corrections.Add($"object alias: '{tokens[i]}' -> 'caja'");
                    tokens[i] = "caja";
                }
            }
        }

        private static bool IsObservedLlevaAlias(string token)
        {
            return token == "gieba" ||
                token == "gieva" ||
                token == "gueva" ||
                token == "gueba";
        }

        private static bool HasStrongTransportBoxAliasContext(List<string> tokens)
        {
            return HasBoxContext(tokens) &&
                tokens.Any(token => token == "zona") &&
                tokens.Any(token => token == "a" || token == "ah" || token == "b" || token == "be" || token == "ve" || token == "c" || token == "ce" || IsAliasWithDigit(token));
        }

        private static bool HasBoxContext(List<string> tokens)
        {
            return tokens != null && tokens.Any(token => token == "caja" || token == "caza" || token == "casa");
        }

        private static bool IsShortEspedaStop(List<string> tokens)
        {
            return tokens != null && tokens.Count == 1 && tokens[0] == "espeda";
        }

        private static bool IsNearestAmbiguityTokens(List<string> tokens)
        {
            if (tokens == null || tokens.Count < 3)
            {
                return false;
            }

            int nearestIndex = tokens.IndexOf("mas");
            if (nearestIndex < 0 || nearestIndex + 1 >= tokens.Count || tokens[nearestIndex + 1] != "cercana")
            {
                return false;
            }

            bool hasBoxTerm = tokens.Any(token => token == "caja" || token == "caza" || token == "casa");
            if (!hasBoxTerm)
            {
                return false;
            }

            bool hasOptionalPickAction = tokens[0] == "coge" || tokens[0] == "recoge" || tokens[0] == "coce" || tokens[0] == "cose";
            bool startsWithBoxPhrase = tokens.Count >= 4 && tokens[0] == "la" && (tokens[1] == "caja" || tokens[1] == "caza" || tokens[1] == "casa");
            return hasOptionalPickAction || startsWithBoxPhrase;
        }

        private static string BuildNearestAmbiguityNormalizedText(List<string> tokens)
        {
            bool hasPickAction = tokens != null && tokens.Count > 0 &&
                (tokens[0] == "coge" || tokens[0] == "recoge" || tokens[0] == "coce" || tokens[0] == "cose");
            return hasPickAction ? "coge la caja mas cercana" : "la caja mas cercana";
        }

        private bool HasTransportContext(List<string> tokens)
        {
            bool hasObject = tokens.Any(token => _vocabulary.ObjectAliases.ContainsKey(token) || token == "cafe");
            bool hasDestination = tokens.Any(token => token == "zona" || _vocabulary.DestinationAliases.ContainsKey(token));
            return hasObject && hasDestination;
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
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                {
                    builder.Append(ch);
                }
                else
                {
                    builder.Append(' ');
                }
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

        private void NormalizeDestinationAliases(List<string> tokens, List<string> corrections)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!_vocabulary.DestinationAliases.TryGetValue(tokens[i], out string canonical))
                {
                    continue;
                }

                AddCorrection(corrections, tokens[i], canonical, "destination alias");
                tokens[i] = canonical == "deposito" ? "zona" : canonical;
            }
        }

        private string FindObjectLabel(List<string> tokens, int objectIndex, ObjectToken objectToken, List<string> corrections)
        {
            if (objectToken == ObjectToken.Unknown)
            {
                return string.Empty;
            }

            for (int i = Math.Max(0, objectIndex + 1); i < tokens.Count; i++)
            {
                if (IsNumericObjectLabel(tokens, i, out string numericLabel, out int consumed))
                {
                    if (consumed > 1)
                    {
                        corrections.Add($"normalized numeric box label '{string.Join(" ", tokens.Skip(i).Take(consumed))}' -> '{numericLabel}'");
                    }

                    return numericLabel;
                }

                if (TryReadSpokenAlphanumericObjectLabel(tokens, i, AllowExperimentalFeToC1Fallback(tokens), out string spokenAliasLabel, out int spokenAliasConsumed))
                {
                    string spoken = string.Join(" ", tokens.Skip(i).Take(spokenAliasConsumed));
                    corrections.Add(spoken == "fe uno"
                        ? $"experimental observed ASR fallback: '{spoken}' -> '{spokenAliasLabel}' in deposit-box context"
                        : $"normalized spoken box alias '{spoken}' -> '{spokenAliasLabel}'");
                    return spokenAliasLabel;
                }

                if (_vocabulary.Labels.Contains(tokens[i]))
                {
                    if (i + 1 < tokens.Count && IsDigits(tokens[i + 1]))
                    {
                        return $"{tokens[i]}{tokens[i + 1]}".ToUpperInvariant();
                    }

                    return tokens[i].ToUpperInvariant();
                }
            }

            if (tokens.Count > 0 && IsTrailingSpokenA(tokens[tokens.Count - 1]))
            {
                corrections.Add($"inferred object label A from trailing '{tokens[tokens.Count - 1]}'");
                return "A";
            }

            return string.Empty;
        }

        private string FindDestinationLabel(List<string> tokens, int objectIndex, List<string> corrections)
        {
            int destinationIndex = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                bool isDestinationAlias = _vocabulary.DestinationAliases.ContainsKey(tokens[i]);
                if (tokens[i] == "zona" || isDestinationAlias)
                {
                    destinationIndex = i;
                }
            }

            if (destinationIndex < 0)
            {
                return string.Empty;
            }

            if (IsSelfDestinationPhrase(tokens, destinationIndex))
            {
                corrections.Add("resolved destination SELF from self-zone phrase");
                return "SELF";
            }

            for (int i = destinationIndex + 1; i < tokens.Count; i++)
            {
                if (_vocabulary.Labels.Contains(tokens[i]))
                {
                    return tokens[i].ToUpperInvariant();
                }
            }

            if (tokens.Count > 0 && IsTrailingSpokenA(tokens[tokens.Count - 1]))
            {
                corrections.Add($"inferred destination label A from trailing '{tokens[tokens.Count - 1]}'");
                return "A";
            }

            if (objectIndex >= 0)
            {
                for (int i = objectIndex + 1; i < destinationIndex; i++)
                {
                    if (IsNumericObjectLabel(tokens, i, out _, out int consumed))
                    {
                        i += Math.Max(0, consumed - 1);
                        continue;
                    }

                    if (_vocabulary.Labels.Contains(tokens[i]))
                    {
                        return tokens[i].ToUpperInvariant();
                    }
                }
            }

            return string.Empty;
        }

        private static bool IsSelfDestinationPhrase(List<string> tokens, int destinationIndex)
        {
            if (tokens == null || destinationIndex < 0 || destinationIndex >= tokens.Count)
            {
                return false;
            }

            if (destinationIndex > 0 && tokens[destinationIndex - 1] == "su")
            {
                return true;
            }

            int selfIndex = tokens.LastIndexOf("su", destinationIndex);
            if (selfIndex >= 0 && selfIndex < destinationIndex)
            {
                bool hasDestinationTerm = false;
                for (int i = selfIndex + 1; i <= destinationIndex; i++)
                {
                    if (tokens[i] == "zona" || tokens[i] == "deposito")
                    {
                        hasDestinationTerm = true;
                        continue;
                    }

                    if (tokens[i] == "de" || tokens[i] == "del" || tokens[i] == "la" || tokens[i] == "el")
                    {
                        continue;
                    }

                    return false;
                }

                if (hasDestinationTerm)
                {
                    return true;
                }
            }

            for (int i = destinationIndex + 1; i < tokens.Count; i++)
            {
                if (tokens[i] == "correspondiente")
                {
                    return true;
                }

                if (_selfDestinationTrailingNoise.Contains(tokens[i]))
                {
                    continue;
                }

                break;
            }

            return false;
        }

        private static readonly HashSet<string> _selfDestinationTrailingNoise = new(StringComparer.Ordinal)
        {
            "de",
            "deposito",
            "depósito",
            "la",
            "el",
            "por",
            "favor"
        };

        private List<string> RemoveNoise(List<string> tokens, string objectLabel, string destinationLabel, List<string> corrections)
        {
            List<string> filtered = new();
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];
                bool isSemanticLabel = (_vocabulary.Labels.Contains(token) || IsDigits(token)) &&
                                       (objectLabel.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        destinationLabel.Equals(token, StringComparison.OrdinalIgnoreCase));
                if (!isSemanticLabel && _vocabulary.NoiseTerms.Contains(token))
                {
                    corrections.Add($"removed noise '{token}'");
                    continue;
                }

                filtered.Add(token);
            }

            return filtered;
        }

        private static string BuildNormalizedText(
            VoiceCommandActionToken action,
            string canonicalAction,
            ObjectToken objectToken,
            string objectLabel,
            string destinationLabel,
            List<string> fallbackTokens)
        {
            string canonical = BuildCanonicalPhrase(action, canonicalAction, objectToken, objectLabel, destinationLabel);
            return string.IsNullOrWhiteSpace(canonical) ? string.Join(" ", fallbackTokens) : canonical;
        }

        private static string BuildCanonicalPhrase(
            VoiceCommandActionToken action,
            string canonicalAction,
            ObjectToken objectToken,
            string objectLabel,
            string destinationLabel)
        {
            if (action == VoiceCommandActionToken.Stop)
            {
                return string.IsNullOrWhiteSpace(canonicalAction) ? "para robot" : canonicalAction;
            }

            if (action == VoiceCommandActionToken.Unknown)
            {
                return string.Empty;
            }

            string actionText = string.IsNullOrWhiteSpace(canonicalAction)
                ? (action == VoiceCommandActionToken.Pick ? "coge" : "lleva")
                : canonicalAction;

            List<string> parts = new() { actionText };
            if (objectToken == ObjectToken.Box)
            {
                parts.Add("la");
                parts.Add("caja");
                if (!string.IsNullOrWhiteSpace(objectLabel) &&
                    (action == VoiceCommandActionToken.Pick || IsAliasWithDigit(objectLabel) || IsDigits(objectLabel)))
                {
                    parts.Add(objectLabel);
                }
            }

            if (!string.IsNullOrWhiteSpace(destinationLabel))
            {
                parts.Add("a");
                if (destinationLabel.Equals("SELF", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add("su");
                    parts.Add("zona");
                }
                else
                {
                    parts.Add("zona");
                    parts.Add(destinationLabel);
                }
            }

            return string.Join(" ", parts);
        }

        private static void ScoreAndStatus(
            VoiceCommandActionToken action,
            ObjectToken objectToken,
            string objectLabel,
            string destinationLabel,
            IReadOnlyCollection<string> corrections,
            out VoiceCommandRecognitionStatus status,
            out float score,
            out string ambiguityReason)
        {
            score = 0f;
            ambiguityReason = string.Empty;
            bool contextualAction = ContainsCorrection(corrections, "contextual action alias");
            bool strongReconstruction = ContainsCorrection(corrections, "strong reconstruction");
            bool contextualObject = ContainsCorrection(corrections, "contextual object alias");
            bool dangerousObjectAlias = ContainsCorrection(corrections, "object alias: 'casa' -> 'caja'");
            bool inferredLabel = ContainsCorrection(corrections, "inferred ");
            int correctionPenaltyCount = corrections == null ? 0 : corrections.Count(correction => correction.Contains("alias") || correction.Contains("reconstruction") || correction.Contains("inferred"));

            if (action == VoiceCommandActionToken.Stop)
            {
                status = VoiceCommandRecognitionStatus.Recognized;
                score = 0.95f;
                return;
            }

            if (action == VoiceCommandActionToken.Unknown)
            {
                status = VoiceCommandRecognitionStatus.Unrecognized;
                ambiguityReason = "no canonical action token was recognized";
                return;
            }

            score = 0.45f;
            if (objectToken == ObjectToken.Box)
            {
                score += 0.25f;
            }

            if (!string.IsNullOrWhiteSpace(objectLabel))
            {
                score += 0.15f;
            }

            if (action == VoiceCommandActionToken.Transport && !string.IsNullOrWhiteSpace(destinationLabel))
            {
                score += 0.2f;
            }

            if (action == VoiceCommandActionToken.Pick && objectToken == ObjectToken.Box)
            {
                score += 0.05f;
            }

            score = Math.Min(1f, score);
            score -= Math.Min(0.25f, correctionPenaltyCount * 0.05f);
            if (contextualAction)
            {
                score -= 0.18f;
            }

            if (strongReconstruction)
            {
                score -= 0.12f;
            }

            if (contextualObject)
            {
                score -= 0.08f;
            }

            if (dangerousObjectAlias)
            {
                score -= 0.05f;
            }

            if (inferredLabel)
            {
                score -= 0.04f;
            }

            score = Math.Max(0f, Math.Min(1f, score));

            if (action == VoiceCommandActionToken.Pick && objectToken == ObjectToken.Box && !string.IsNullOrWhiteSpace(objectLabel))
            {
                status = VoiceCommandRecognitionStatus.Recognized;
                return;
            }

            if (action == VoiceCommandActionToken.Transport && objectToken == ObjectToken.Box && !string.IsNullOrWhiteSpace(destinationLabel))
            {
                if (contextualAction)
                {
                    status = VoiceCommandRecognitionStatus.Ambiguous;
                    ambiguityReason = "transport action was inferred from weak contextual ASR evidence";
                    return;
                }

                status = VoiceCommandRecognitionStatus.Recognized;
                return;
            }

            status = objectToken == ObjectToken.Box ? VoiceCommandRecognitionStatus.Ambiguous : VoiceCommandRecognitionStatus.Unrecognized;
            ambiguityReason = objectToken == ObjectToken.Box
                ? "recognized action and object but missing required label or destination"
                : "recognized action but no logistics object was recognized";
        }

        private static bool ContainsCorrection(IEnumerable<string> corrections, string marker)
        {
            return corrections != null && corrections.Any(correction => correction.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsTrailingSpokenA(string token)
        {
            return token == "a" || token == "ah" || token == "eh";
        }

        private static bool IsNumericObjectLabel(List<string> tokens, int index, out string label, out int consumed)
        {
            label = string.Empty;
            consumed = 0;
            if (tokens == null || index < 0 || index >= tokens.Count)
            {
                return false;
            }

            string token = tokens[index];
            if (IsAliasWithDigit(token))
            {
                label = token.ToUpperInvariant();
                consumed = 1;
                return true;
            }

            if (IsDigits(token))
            {
                label = token;
                consumed = 1;
                return true;
            }

            if ((token == "numero" || token == "n") && index + 1 < tokens.Count && IsDigits(tokens[index + 1]))
            {
                label = tokens[index + 1];
                consumed = 2;
                return true;
            }

            return false;
        }

        private static bool TryReadSpokenAlphanumericObjectLabel(List<string> tokens, int index, bool allowExperimentalFeToC1Fallback, out string label, out int consumed)
        {
            label = string.Empty;
            consumed = 0;
            if (tokens == null || index < 0 || index + 1 >= tokens.Count)
            {
                return false;
            }

            if (!TryReadSpokenLetter(tokens[index], allowExperimentalFeToC1Fallback, out string letter) ||
                !TryReadSpokenDigit(tokens[index + 1], out string digit))
            {
                return false;
            }

            label = letter + digit;
            consumed = 2;
            return true;
        }

        private static bool TryReadSpokenLetter(string token, bool allowExperimentalFeToC1Fallback, out string letter)
        {
            letter = string.Empty;
            switch (token)
            {
                case "a":
                case "ah":
                    letter = "A";
                    return true;
                case "b":
                case "be":
                case "ve":
                case "veo":
                case "de":
                    letter = "B";
                    return true;
                case "c":
                case "ce":
                case "see":
                    letter = "C";
                    return true;
                case "fe":
                    if (allowExperimentalFeToC1Fallback)
                    {
                        letter = "C";
                        return true;
                    }

                    return false;
                default:
                    return false;
            }
        }

        private static bool AllowExperimentalFeToC1Fallback(List<string> tokens)
        {
            return tokens != null &&
                tokens.Any(token => token == "deposita" || token == "deja") &&
                tokens.Any(token => token == "caja" || token == "caza" || token == "casa");
        }

        private static bool TryReadSpokenDigit(string token, out string digit)
        {
            digit = string.Empty;
            switch (token)
            {
                case "1":
                case "uno":
                    digit = "1";
                    return true;
                case "2":
                case "dos":
                    digit = "2";
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsAliasWithDigit(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length < 2)
            {
                return false;
            }

            bool hasLetter = false;
            bool hasDigit = false;
            foreach (char ch in token)
            {
                if (char.IsLetter(ch))
                {
                    hasLetter = true;
                }
                else if (char.IsDigit(ch))
                {
                    hasDigit = true;
                }
                else
                {
                    return false;
                }
            }

            return hasLetter && hasDigit;
        }

        private static bool IsDigits(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && token.All(char.IsDigit);
        }

        private static void AddCorrection(List<string> corrections, string original, string canonical, string reason)
        {
            if (string.Equals(original, canonical, StringComparison.Ordinal))
            {
                return;
            }

            corrections.Add($"{reason}: '{original}' -> '{canonical}'");
        }

        private static VoiceCommandNormalizationResult Build(
            string raw,
            string cleaned,
            string normalized,
            VoiceCommandRecognitionStatus status,
            float score,
            string canonicalPhrase,
            VoiceCommandActionToken action,
            ObjectToken objectToken,
            string objectLabel,
            string destinationLabel,
            IEnumerable<string> corrections,
            string ambiguityReason)
        {
            return new VoiceCommandNormalizationResult(
                raw,
                cleaned,
                normalized,
                status,
                score,
                canonicalPhrase,
                action,
                objectToken,
                objectLabel,
                destinationLabel,
                corrections,
                ambiguityReason);
        }
    }
}
