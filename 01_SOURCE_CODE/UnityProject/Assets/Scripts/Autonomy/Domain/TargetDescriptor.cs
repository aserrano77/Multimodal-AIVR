using System.Numerics;

namespace Autonomy.Domain
{
    /// <summary>
    /// Modelo mínimo suficiente para representar un objetivo de tarea.
    /// Este modelo es pragmático para la iteración actual y evita el uso de tipos de Unity.
    /// </summary>
    public record TargetDescriptor(string Id, Vector3 Position);
}
