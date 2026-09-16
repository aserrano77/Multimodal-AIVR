using System;

namespace Autonomy.Core
{
    public readonly struct BlackboardKey<T> : IEquatable<BlackboardKey<T>>
    {
        public string Id { get; }

        public BlackboardKey(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Key ID cannot be null or empty.", nameof(id));

            Id = id;
        }

        public bool Equals(BlackboardKey<T> other) => Id == other.Id;
        public override bool Equals(object obj) => obj is BlackboardKey<T> other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
        public override string ToString() => Id;

        public static bool operator ==(BlackboardKey<T> left, BlackboardKey<T> right) => left.Equals(right);
        public static bool operator !=(BlackboardKey<T> left, BlackboardKey<T> right) => !left.Equals(right);
    }
}
