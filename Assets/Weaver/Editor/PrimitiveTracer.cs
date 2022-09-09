using System.Runtime.Serialization;
using UnityEngine;

namespace Weaver.Editor
{
    public static class PrimitiveTracer
    {
        static readonly ObjectIDGenerator objectIDGenerator = new();
        
        public static void Trace(object traceObject, string methodName)
        {
            string output = $"Method Name: {methodName}";
            bool isNotUnityManaged = true;
            if (traceObject is Object traceObjectObject)
            {
                isNotUnityManaged = false;
                output += $" Object ID: {traceObjectObject.GetInstanceID()}";
            }
            
            if (traceObject is Component traceObjectComponent)
            {
                isNotUnityManaged = false;
                output += $" GameObject ID: {traceObjectComponent.gameObject.GetInstanceID()}";
            }

            if (isNotUnityManaged)
            {
                long classInstanceId = objectIDGenerator.GetId(traceObject, out bool firstTime);
                output += $" Instance ID {classInstanceId}";
            }
            
            Debug.Log(output);
        }
    }
}