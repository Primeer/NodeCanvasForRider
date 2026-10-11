// Minimal stand-ins for UnityEngine and NodeCanvas types used by the test sources.
namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public class ScriptableObject : Object { }
    public struct Vector3 { public float x, y, z; }
}

namespace NodeCanvas.Framework
{
    public abstract class Graph : UnityEngine.ScriptableObject { }
}
