using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace Weaver.Editor
{
    [InitializeOnLoad]
    public static class AssemblyPosprocessor
    {
        static List<FileSystemWatcher> m_AssemblyWatcheers;

        static AssemblyPosprocessor()
        {
 
        }
    }
}
