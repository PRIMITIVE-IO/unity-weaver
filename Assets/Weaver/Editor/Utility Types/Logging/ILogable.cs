using UnityEngine;

namespace Weaver.Editor.Utility_Types.Logging
{
    public interface ILogable
    {
        Object context { get; }
        string label { get; }
    }
}
