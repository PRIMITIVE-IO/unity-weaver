﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using JetBrains.Annotations;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Weaver.Editor.Utility_Types.Logging
{
    [Serializable]
    public class Log
    {
        [Serializable]
        public struct Entry
        {
            public static int selectedID;
            public int id;
            public int lineNumber;
            public string fileName;
            public string message;
            public MessageType type;
        }

        ILogable m_Context;
        [SerializeField]
        [UsedImplicitly]
        List<Entry> m_Entries = new();

        public List<Entry> entries => m_Entries;

        public ILogable context
        {
            get => m_Context;
            set => m_Context = value;
        }
        /// <summary>
        /// Creates a new instance of a log.
        /// </summary>
        public Log(ILogable context)
        {
            m_Context = context;
            m_Entries = new List<Entry>();
        }

        public void Clear()
        {
            m_Entries.Clear();
        }

        /// <summary>
        /// Logs a message to the weaver settings log with an
        /// option to write to the Unity console. 
        /// </summary>
        /// <param name="message">The message you want to write</param>
        /// <param name="logToConsole">If true will also log to the Unity console</param>
        public void Info(string context, string message, bool logToConsole, int stackFrameDiscard = 2)
        {
            AddEntry(context, message, MessageType.Info, stackFrameDiscard);
            if (logToConsole)
            {
                Debug.Log(message);
            }
        }

        /// <summary>
        /// Logs a warning to the weaver settings log with an
        /// option to write to the Unity console. 
        /// </summary>
        /// <param name="warning">The message you want to write</param>
        /// <param name="logToConsole">If true will also log to the Unity console</param>
        public void Warning(string context, string warning, bool logToConsole, int stackFrameDiscard = 2)
        {
            AddEntry(context, warning, MessageType.Warning, stackFrameDiscard);
            if (logToConsole)
            {
                Debug.LogWarning(warning);
            }
        }

        /// <summary>
        /// Logs a error to the weaver settings log with an
        /// option to write to the Unity console. 
        /// </summary>
        /// <param name="message">The message you want to write</param>
        /// <param name="logToConsole">If true will also log to the Unity console</param>
        public void Error(string context, string error, bool logToConsole, int stackFrameDiscard = 2)
        {
            AddEntry(context, error, MessageType.Error, stackFrameDiscard);
            if (logToConsole)
            {
                Debug.LogError(error);
            }
        }

        /// <summary>
        /// Adds the label to the front of the console log.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        string FormatLabel(string message, string fileName, int lineNumber, MessageType logType)
        {
            switch (logType)
            {
                case MessageType.Warning:
                    return $"<color=yellow>[{fileName}:{lineNumber}]: {message}</color>";
                case MessageType.Error:
                    return $"<color=red>[{fileName}:{lineNumber}]: {message}</color>";
            }
            return $"[{fileName}:{lineNumber}]: {message}";
        }

        void AddEntry(string context, string message, MessageType logType, int stackFrameDiscard)
        {
            // Get our stack frame
            StackFrame frame = new(stackFrameDiscard, true);
            // Create our entry
            if (string.IsNullOrEmpty(context))
            {
                context = System.IO.Path.GetFileNameWithoutExtension(frame.GetFileName());
            }
            int lineNumber = frame.GetFileLineNumber();
            message = FormatLabel(message, context, frame.GetFileLineNumber(), logType);
            Entry entry = new()
            {
                fileName = frame.GetFileName(),
                lineNumber = lineNumber,
                message = message,
                type = logType,
                id = m_Entries.Count + 1
            };
            m_Entries.Add(entry);
        }
    }
}
