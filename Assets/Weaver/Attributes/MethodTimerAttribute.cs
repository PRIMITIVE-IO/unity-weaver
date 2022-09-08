using System;

namespace Weaver.Attributes
{
    /// <summary>
    /// Based off of https://github.com/Fody/MethodTimer
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class MethodTimerAttribute : Attribute
    {
    }
}
