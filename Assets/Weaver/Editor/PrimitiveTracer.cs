using UnityEngine;

namespace Weaver.Editor
{
    public static class PrimitiveTracer
    {
        public static void Trace(object traceObject, string methodName)
        {
            Debug.Log(methodName);
            if (traceObject is Object traceObjectObject)
            {
                Debug.Log(traceObjectObject.GetInstanceID());
            }
            if (traceObject is Component traceObjectComponent)
            {
                Debug.Log(traceObjectComponent.gameObject.GetInstanceID());
            }
        }
    }
}