using System;
using System.Collections.Generic;

namespace Autonomy.Core
{
    public class RobotBlackboard : IRobotBlackboard
    {
        private readonly Dictionary<string, object> _data = new(StringComparer.Ordinal);

        public RobotMode CurrentMode { get; private set; } = RobotMode.Idle;

        public void SetMode(RobotMode newMode)
        {
            CurrentMode = newMode;
        }

        public void Set<T>(BlackboardKey<T> key, T value)
        {
            _data[key.Id] = value;
        }

        public bool TryGet<T>(BlackboardKey<T> key, out T value)
        {
            // Enfoque semi-tipado: la integridad de los datos depende de la disciplina al definir IDs de claves únicos.
            // Si dos claves comparten ID pero difieren en tipo T, TryGet fallará silenciosamente retornando false.
            if (_data.TryGetValue(key.Id, out var obj) && obj is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        public bool Remove<T>(BlackboardKey<T> key)
        {
            return _data.Remove(key.Id);
        }
    }
}
