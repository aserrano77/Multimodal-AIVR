using System;
using System.Collections.Generic;
using System.Globalization;
using Autonomy.Domain;

namespace Autonomy.UnityIntegration
{
    public sealed class ExperimentGlobalInstructionPage
    {
        public ExperimentGlobalInstructionPage(string title, params string[] lines)
        {
            Title = title ?? string.Empty;
            Lines = Array.AsReadOnly(lines ?? Array.Empty<string>());
        }

        public string Title { get; }
        public IReadOnlyList<string> Lines { get; }
        public string Body => string.Join("\n", Lines);
    }

    public sealed class ExperimentGlobalInstructionsState
    {
        private readonly IReadOnlyList<ExperimentGlobalInstructionPage> _pages;

        public ExperimentGlobalInstructionsState(IReadOnlyList<ExperimentGlobalInstructionPage> pages)
        {
            _pages = pages ?? Array.Empty<ExperimentGlobalInstructionPage>();
            PageIndex = 0;
        }

        public int PageIndex { get; private set; }
        public int PageCount => _pages.Count;
        public bool HasPrevious => PageIndex > 0;
        public bool HasNext => PageIndex + 1 < PageCount;
        public bool CanComplete => PageCount > 0 && PageIndex == PageCount - 1;
        public ExperimentGlobalInstructionPage CurrentPage => PageCount == 0 ? null : _pages[PageIndex];

        public static ExperimentGlobalInstructionsState CreateDefault()
        {
            return new ExperimentGlobalInstructionsState(CreateDefaultPages());
        }

        public static IReadOnlyList<ExperimentGlobalInstructionPage> CreateDefaultPages()
        {
            return Array.AsReadOnly(new[]
            {
                new ExperimentGlobalInstructionPage(
                    "Estructura de la sesión",
                    "La sesión tiene 3 pruebas.",
                    "Cada prueba tiene 2 rondas.",
                    "Antes de cada prueba aparecerán instrucciones específicas.",
                    "Para empezar cada prueba se debe pulsar \"Comenzar la prueba\"."),
                new ExperimentGlobalInstructionPage(
                    "Controles básicos",
                    "Gatillo: pulsar botones de menús y paneles.",
                    "Grip: agarrar cajas.",
                    "Stick analógico: desplazarse por el entorno.",
                    "Botón \"Y\" izquierdo: abrir el menú de pausa."),
                new ExperimentGlobalInstructionPage(
                    "Panel de control de la pared",
                    "En la pared a su izquierda hay un panel de control del experimento.",
                    "Sirve para avanzar cuando corresponda.",
                    "También permite cerrar una ronda por incidencia si ocurre un problema.",
                    "Sigue siempre las indicaciones de ese panel."),
                new ExperimentGlobalInstructionPage(
                    "Pausa y salidas",
                    "El botón Y abre el menú de pausa.",
                    "Continuar: vuelve al experimento.",
                    "Reiniciar sesión experimental: empieza un nuevo intento desde el principio.",
                    "Guardar y volver al inicio: guarda un punto recuperable para continuar después.",
                    "Salir al inicio sin guardar: vuelve al inicio sin guardar progreso recuperable."),
                new ExperimentGlobalInstructionPage(
                    "Cuestionario final",
                    "Al terminar las 3 pruebas aparecerá un código de cuestionario de 6 letras y números.",
                    "Ese código deberá introducirse en el formulario final de Google Forms.",
                    "No cierres la aplicación antes de anotar el código.",
                    "El código puede recuperarse más tarde desde Historial de IDs."),
                new ExperimentGlobalInstructionPage(
                    "Normas durante la prueba",
                    "Evita bloquear al robot.",
                    "No empujes al robot ni los palés.",
                    "No interfieras con cajas que esté manipulando el robot.",
                    "Si ocurre un problema, usa el panel de pared o el menú de pausa, no cierres manualmente la aplicación.")
            });
        }

        public int MoveNext()
        {
            if (HasNext)
            {
                PageIndex++;
            }

            return PageIndex;
        }

        public int MovePrevious()
        {
            if (HasPrevious)
            {
                PageIndex--;
            }

            return PageIndex;
        }

        public IReadOnlyList<string> ResolveWizardButtonLabels()
        {
            var labels = new List<string>();
            if (HasPrevious)
            {
                labels.Add("Anterior");
            }

            labels.Add(CanComplete ? "Entendido / Comenzar" : "Siguiente");
            return labels;
        }
    }

    public sealed class ExperimentFinalQuestionnaireCompletionModel
    {
        public ExperimentFinalQuestionnaireCompletionModel(
            string sessionId,
            string questionnaireCode,
            int visiblePrueba,
            int roundIndex,
            string status)
        {
            SessionId = sessionId ?? string.Empty;
            QuestionnaireCode = questionnaireCode ?? string.Empty;
            VisiblePrueba = Math.Max(0, visiblePrueba);
            RoundIndex = Math.Max(0, roundIndex);
            Status = string.IsNullOrWhiteSpace(status) ? ExperimentSessionIdHistoryStore.CompletedStatus : status.Trim();
        }

        public string SessionId { get; }
        public string QuestionnaireCode { get; }
        public int VisiblePrueba { get; }
        public int RoundIndex { get; }
        public string Status { get; }
        public string Title => "Experimento completado";
        public string PrimaryInstruction => "Introduce este codigo en el cuestionario final.";
        public string AssociationExplanation => "Este codigo permite asociar tus respuestas del cuestionario con la sesion experimental.";
        public string HistoryNote => "Tambien podras recuperarlo desde Historial de IDs en la pantalla inicial.";
        public bool HasValidQuestionnaireCode => IsSixCharacterQuestionnaireCode(QuestionnaireCode);

        public static bool IsSixCharacterQuestionnaireCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Trim().Length != QuestionnaireCodeCodec.CodeLength)
            {
                return false;
            }

            string trimmed = code.Trim();
            for (int i = 0; i < trimmed.Length; i++)
            {
                char ch = trimmed[i];
                if (!char.IsLetterOrDigit(ch))
                {
                    return false;
                }
            }

            return true;
        }

        public Dictionary<string, object> ToEventPayload(string eventName)
        {
            return new Dictionary<string, object>
            {
                ["event_name"] = eventName ?? string.Empty,
                ["session_id"] = SessionId,
                ["questionnaire_code"] = QuestionnaireCode,
                ["visible_prueba"] = VisiblePrueba,
                ["round_index"] = RoundIndex,
                ["status"] = Status,
                ["timestamp"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
        }
    }

    public enum ExperimentStartScreenRenderMode
    {
        Welcome,
        GlobalInstructions,
        SavedExitPrompt,
        History
    }

    public static class ExperimentStartScreenFlowPriority
    {
        public static ExperimentStartScreenRenderMode Resolve(
            bool showingHistory,
            bool hasPendingSavedExit,
            bool showingGlobalInstructions)
        {
            if (showingHistory)
            {
                return ExperimentStartScreenRenderMode.History;
            }

            if (hasPendingSavedExit)
            {
                return ExperimentStartScreenRenderMode.SavedExitPrompt;
            }

            return showingGlobalInstructions
                ? ExperimentStartScreenRenderMode.GlobalInstructions
                : ExperimentStartScreenRenderMode.Welcome;
        }
    }
}
