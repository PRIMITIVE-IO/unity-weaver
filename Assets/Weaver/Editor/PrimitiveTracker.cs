#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using JetBrains.Annotations;
using UnityEngine;
using Weaver.Editor.Settings;
using Debug = UnityEngine.Debug;

namespace Weaver.Editor
{
    public static class PrimitiveTracker
    {
        /// <summary>
        /// Allows us to generate a unique ID for each instance of every class that we see.
        /// </summary>
        static ObjectIDGenerator objectIDGenerator;

        public static PrimitiveTraceSqliteOutput primitiveTraceSqliteOutput;

        /// <summary>
        /// All threads are tracked here. Since multiple threads write to this dictionary, it must be concurrent.
        /// Same for the stack traces that are tracked.
        /// </summary>
        static ConcurrentDictionary<int, ConcurrentStack<PrimitiveStackEntry>> callStacksByThreadId;

        /// <summary>
        /// Unity sends messages on the main thread, which are outside the current call stack on the main thread
        /// This list tracks the hash codes of the base methods of these "imposter" threads
        /// </summary>
        static List<int> imposterThreads;

        /// <summary>
        /// The current base function of the main thread is tracked here.
        /// When the main thread returns, this is set to "" 
        /// </summary>
        static string baseOfMainThread;

        /// <summary>
        /// Threads sometimes have names. Either the name, or the number as a string are stored in this dictionary
        /// </summary>
        static Dictionary<int, string> threadNamesById;

        static Stopwatch sw;
        static ActiveRepetitions activeRepetitions;

        static bool verbose;

        static volatile bool first = true;

        /// <summary>
        /// This queue accumulates thread events and writes to SQLite at periodic intervals
        /// </summary>
        static ConcurrentQueue<Batch> accumulatedEntries;

        static int stackEntryIncrementor = 1;
        static int threadIncrementor = 1;
        static int threadBatchIncrementor = 1;
        const int BatchSize = 2000;

        static string DbDefaultPath
        {
            get
            {
                if (WeaverSettings.Instance != null && !string.IsNullOrEmpty(WeaverSettings.Instance.m_PathToOutput))
                {
                    return WeaverSettings.Instance.m_PathToOutput;
                }

                string codeBase = Assembly.GetExecutingAssembly().CodeBase;
                UriBuilder uri = new UriBuilder(codeBase);
                string path = Uri.UnescapeDataString(uri.Path);
                return Path.Combine(Path.GetDirectoryName(path), $"{DateTime.Now.ToString("yyyyMMddHHmmssfff")}.db");
            }
        }

        static void Initialize()
        {
            objectIDGenerator = new();

            callStacksByThreadId = new();
            imposterThreads = new();
            baseOfMainThread = "";
            threadNamesById = new();

            primitiveTraceSqliteOutput = new(DbDefaultPath);
            verbose = WeaverSettings.Instance != null && WeaverSettings.Instance.m_Verbose;
            accumulatedEntries = new ConcurrentQueue<Batch>();

            sw = Stopwatch.StartNew();
            activeRepetitions = new ActiveRepetitions(10000, 50, 200);
        }

        static bool CheckPlayingAndInitialize()
        {
            try
            {
                if (!Application.isPlaying) return false;
            }
            catch (UnityException e)
            {
                // do nothing - trying to check from another thread or from MonoBehaviour constructor
            }

            if (first)
            {
                Initialize();
                first = false;
            }

            return true;
        }

        [PublicAPI]
        public static void OnInstanceEntry(object traceObject)
        {
            if (!CheckPlayingAndInitialize()) return;

            long objectInstance = objectIDGenerator.GetId(traceObject, out bool firstTime);

            StackTrace stackTrace = new StackTrace(false);
            StackSort(stackTrace, objectInstance);
        }

        [PublicAPI]
        public static void OnStaticEntry()
        {
            if (!CheckPlayingAndInitialize()) return;

            StackTrace stackTrace = new StackTrace(false);
            StackSort(stackTrace);
        }

        [PublicAPI]
        public static void OnInstanceExit(object traceObject)
        {
            if (!CheckPlayingAndInitialize()) return;

            long objectInstance = objectIDGenerator.GetId(traceObject, out bool firstTime);
            StackTrace stackTrace = new StackTrace(false);
            StackPop(stackTrace, objectInstance);
        }

        [PublicAPI]
        public static void OnStaticExit()
        {
            if (!CheckPlayingAndInitialize()) return;

            StackTrace stackTrace = new StackTrace(false);
            StackPop(stackTrace);
        }

        static void StackSort(StackTrace stackTrace, long objectId = -1)
        {
            int threadId = Environment.CurrentManagedThreadId;
            if (!threadNamesById.ContainsKey(threadId))
            {
                string threadName = Thread.CurrentThread.Name ?? threadId.ToString();
                threadNamesById.Add(threadId, threadName);
            }

            List<string> stackMethods = StackMethods(stackTrace);
            string topOfStackString = stackMethods.First();
            if (topOfStackString.StartsWith('<')) return; // anonymous method
            MethodName topOfStack = FromFQN(topOfStackString);

            activeRepetitions.OnMethodEnter(topOfStack, threadIncrementor);
            if (activeRepetitions.IsExecutingRepetitiveMethod) return;

            string bottomOfStack = stackMethods.Last();

            if (threadId == 1 && string.IsNullOrEmpty(baseOfMainThread))
            {
                // set the base method for thread 1
                baseOfMainThread = bottomOfStack;
            }
            else if (threadId == 1 && baseOfMainThread != bottomOfStack)
            {
                // imposter
                threadId = Math.Abs(bottomOfStack.GetHashCode()); // thread ids need to be positive
                imposterThreads.Add(threadId);
                if (!threadNamesById.ContainsKey(threadId))
                {
                    threadNamesById.Add(threadId, threadId.ToString());
                }
            }

            PrimitiveStackEntry primitiveStackEntry = new(topOfStack, objectId);

            if (verbose)
            {
                Log(topOfStack.FullyQualifiedName,
                    objectId,
                    threadId,
                    true);
            }

            PushStackAndWrite(primitiveStackEntry, threadId);
        }

        static List<string> StackMethods(StackTrace stackTrace)
        {
            bool first = true;
            List<string> stackMethods = new();
            foreach (StackFrame? stackFrame in stackTrace.GetFrames())
            {
                if (first)
                {
                    // skip the tracer method (this)
                    first = false;
                    continue;
                }

                if (stackFrame != null)
                {
                    string traceMethodName = stackFrame.GetMethod().Name;
                    string traceMethodDeclType = stackFrame.GetMethod().DeclaringType.FullName;
                    if (traceMethodName == ".ctor")
                    {
                        traceMethodName = stackFrame.GetMethod().DeclaringType.Name;
                    }

                    stackMethods.Add($"{traceMethodDeclType}.{traceMethodName}");
                }
            }

            return stackMethods;
        }

        static void PushStackAndWrite(PrimitiveStackEntry primitiveStackEntry, int threadId)
        {
            callStacksByThreadId.TryGetValue(threadId, out ConcurrentStack<PrimitiveStackEntry>? stack);
            if (stack == null)
            {
                stack = new ConcurrentStack<PrimitiveStackEntry>();
                bool success = callStacksByThreadId.TryAdd(threadId, stack);
                if (!success)
                {
                    return;
                }
            }

            stack.Push(primitiveStackEntry);
            long elapsed = sw.ElapsedMilliseconds;

            // write EVERY thread group, but mark this new stack with the active status 2
            foreach (KeyValuePair<int, ConcurrentStack<PrimitiveStackEntry>> keyValuePair in callStacksByThreadId)
            {
                List<PrimitiveStackEntry> entriesToInsert = keyValuePair.Value.ToList();
                Batch newBatch = new(
                    keyValuePair.Key,
                    elapsed,
                    entriesToInsert,
                    stackEntryIncrementor,
                    threadIncrementor,
                    threadId == keyValuePair.Key
                        // these values are from the JVM standard
                        ? 2 // active thread 
                        : 5, // waiting thread
                    threadBatchIncrementor);

                stackEntryIncrementor += entriesToInsert.Count;
                accumulatedEntries.Enqueue(newBatch);
                threadIncrementor++;
            }

            threadBatchIncrementor++;

            if ((threadId == 1 || imposterThreads.Contains(threadId)) && accumulatedEntries.Count > BatchSize)
            {
                // only write from the main thread
                List<Batch> copy = new();
                for (int i = 0; i < BatchSize; i++)
                {
                    bool success = accumulatedEntries.TryDequeue(out Batch toAdd);
                    if (success)
                    {
                        copy.Add(toAdd);
                    }
                }

                WriteAll(copy);
            }
        }

        static void WriteAll(IReadOnlyCollection<Batch> copy)
        {
            Debug.Log($"Writing {copy.Count} entries...");

            primitiveTraceSqliteOutput.InsertThread(copy, threadNamesById);
            primitiveTraceSqliteOutput.InsertStackFrames(copy);
            primitiveTraceSqliteOutput.InsertObject(copy);

            Debug.Log($"Wrote {copy.Count} entries");
        }

        static void StackPop(StackTrace stackTrace, long instanceId = -1)
        {
            int threadId = Environment.CurrentManagedThreadId;
            List<string> stackMethods = StackMethods(stackTrace);
            string topOfStackString = stackMethods.First();
            if (topOfStackString.StartsWith('<')) return; // anonymous method
            MethodName topOfStack = FromFQN(topOfStackString);
            string bottomOfStack = stackMethods.Last();

            if (threadId == 1 && bottomOfStack != baseOfMainThread)
            {
                // imposter
                threadId = Math.Abs(bottomOfStack.GetHashCode());
            }

            PrimitiveStackEntry primitiveStackEntry = new(topOfStack, instanceId);

            if (verbose)
            {
                Log(topOfStack.FullyQualifiedName,
                    instanceId,
                    threadId,
                    false);
            }
            
            activeRepetitions.OnMethodExit(topOfStack, threadIncrementor);
            callStacksByThreadId.TryGetValue(threadId, out ConcurrentStack<PrimitiveStackEntry>? stack);
            if (stack != null)
            {
                int removeHashCode = primitiveStackEntry.GetHashCode();
                int topHashCode = -1;
                while (stack.Count > 0 && topHashCode != removeHashCode)
                {
                    bool success = stack.TryPop(out PrimitiveStackEntry top);
                    if (success)
                    {
                        topHashCode = top.GetHashCode();
                    }
                }

                if (!stack.Any())
                {
                    callStacksByThreadId.Remove(threadId, out ConcurrentStack<PrimitiveStackEntry> _);
                    if (threadId == 1)
                    {
                        baseOfMainThread = "";
                        // clear imposters
                        foreach (int imposterThread in imposterThreads)
                        {
                            callStacksByThreadId.Remove(imposterThread, out ConcurrentStack<PrimitiveStackEntry> _);
                        }

                        imposterThreads.Clear();
                    }
                }
            }
        }

        static void Log(string methodName, long objectId, int threadId, bool isEntering)
        {
            string entering = isEntering ? "Entering" : "Exiting";
            Debug.Log($"{entering} Method {methodName} on instance {objectId} on thread {threadId}");
        }

        static MethodName FromFQN(string methodFqn)
        {
            string prefix = methodFqn;
            if (prefix.Contains('<'))
            {
                prefix = prefix[..prefix.IndexOf('<')];
            }
            else if (prefix.Contains('('))
            {
                prefix = prefix[..prefix.IndexOf('(')];
            }

            prefix = prefix.TrimEnd(new char[1] { '.' });
            prefix = prefix.Replace('+', '$'); // inner class separator

            string methodNameString = prefix[(prefix.LastIndexOf('.') + 1)..];

            string classFqn = prefix[..prefix.LastIndexOf('.')];
            string namespaceName = "";
            if (classFqn.Contains('.'))
            {
                namespaceName = classFqn[..classFqn.LastIndexOf('.')];
                classFqn = classFqn[(classFqn.LastIndexOf('.') + 1)..];
            }

            string returnType = "Void"; // TODO
            ClassName parentClass = new ClassName(
                new FileName(""),
                new PackageName(namespaceName),
                classFqn);
            MethodName methodName = new MethodName(
                parentClass,
                methodNameString,
                $"()L{returnType};", // TODO: Java format
                new List<Argument>());

            return methodName;
        }

        public class PrimitiveStackEntry
        {
            public readonly MethodName MethodName;
            public readonly long ObjectId;

            public PrimitiveStackEntry(MethodName methodName, long objectId)
            {
                MethodName = methodName;
                ObjectId = objectId;
            }

            public override int GetHashCode()
            {
                return MethodName.GetHashCode() + ObjectId.GetHashCode();
            }
        }
    }
}