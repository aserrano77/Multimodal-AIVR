using System.Collections.Generic;

namespace Autonomy.Domain
{
    public sealed class VoiceCommandVocabulary
    {
        public static VoiceCommandVocabulary SpanishLogistics { get; } = new VoiceCommandVocabulary();

        private VoiceCommandVocabulary()
        {
            PickActions = new Dictionary<string, string>
            {
                ["coge"] = "coge",
                ["recoge"] = "recoge",
                ["toma"] = "toma",
                ["coce"] = "coge",
                ["cose"] = "coge"
            };

            TransportActions = new Dictionary<string, string>
            {
                ["lleva"] = "lleva",
                ["mueve"] = "mueve",
                ["deposita"] = "deposita",
                ["deja"] = "deja"
            };

            StopPhrases = new HashSet<string>
            {
                "espera",
                "detente",
                "alto",
                "alto robot",
                "para robot",
                "para la tarea",
                "parar",
                "parar robot",
                "stop",
                "stop robot",
                "cancela la tarea",
                "pausa seguridad"
            };

            ObjectAliases = new Dictionary<string, string>
            {
                ["caja"] = "caja",
                ["paquete"] = "caja",
                ["caza"] = "caja",
                ["casa"] = "caja"
            };

            DestinationAliases = new Dictionary<string, string>
            {
                ["zona"] = "zona",
                ["deposito"] = "zona",
                ["bosito"] = "deposito"
            };

            Labels = new HashSet<string> { "a", "b", "c" };
            NoiseTerms = new HashSet<string> { "eh", "ah", "por", "favor" };
        }

        public IReadOnlyDictionary<string, string> PickActions { get; }
        public IReadOnlyDictionary<string, string> TransportActions { get; }
        public IReadOnlyCollection<string> StopPhrases { get; }
        public IReadOnlyDictionary<string, string> ObjectAliases { get; }
        public IReadOnlyDictionary<string, string> DestinationAliases { get; }
        public IReadOnlyCollection<string> Labels { get; }
        public IReadOnlyCollection<string> NoiseTerms { get; }
    }
}
