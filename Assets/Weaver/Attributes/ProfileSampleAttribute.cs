using System;

namespace Weaver.Attributes
{
    /// <summary>
    /// When put above a method and Weaver has this option
    /// turned on this will inject <see cref="UnityEngine.Profiling.Profiler.BeginSample(string)"/>
    /// at the start of the function and <see cref="UnityEngine.Profiling.Profiler.EndSample()"/> and the 
    /// end. This will then output the result.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public class ProfileSampleAttribute : Attribute
    {
    }
}
