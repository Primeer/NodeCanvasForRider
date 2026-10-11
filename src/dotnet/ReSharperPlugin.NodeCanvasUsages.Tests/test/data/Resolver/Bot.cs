using UnityEngine;

namespace Demo.Game
{
    [System.Flags]
    public enum Relation
    {
        None = 0,
        Self = 1 << 0,
        Enemy = 1 << 1,
        Ally = 1 << 2
    }

    public class Unit : MonoBehaviour
    {
        public float Health => 100f;
    }

    public class Pawn : MonoBehaviour
    {
        public float armor;
        public void Heal(float amount) { }
        public bool IsAlive() { return true; }
    }

    public class Bot : Pawn
    {
        public class Sensor
        {
            public static bool Ping(Bot owner) { return true; }
        }

        public float speed;

        public bool IsDead { get; private set; }
        public float Stamina => 1f;

        public void Attack(Unit target) { }
        public void MoveTo(Vector3 point) { }
        public void MoveTo(Unit target, Vector3 offset) { }
        public float DistanceTo(Unit target) { return 0f; }
        public float DistanceTo(Vector3 point) { return 0f; }
        public bool CanSee(Unit target, float angle) { return true; }
        private void Secret() { }
        public static Bot Create() { return null; }
    }
}
