#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using JetBrains.Annotations;
using Mono.Data.Sqlite;
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

        static PrimitiveTraceSqliteOutput primitiveTraceSqliteOutput;

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
            if (topOfStackString.StartsWith('<')) return;// anonymous method
            MethodName topOfStack = FromFQN(topOfStackString);
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
                        : 5,// waiting thread
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

            primitiveTraceSqliteOutput.InsertThread(copy);
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

        class PrimitiveStackEntry
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

        struct Batch
        {
            public readonly int threadCount;
            public readonly int stackEntryCount;
            public readonly int threadId;
            public readonly long elapsed;
            public readonly List<PrimitiveStackEntry> entries;
            public readonly int threadState;
            public readonly int threadBatchIncrement;

            public Batch(
                int threadId,
                long elapsed,
                List<PrimitiveStackEntry> entries,
                int stackEntryCount,
                int threadCount,
                int threadState,
                int threadBatchIncrement)
            {
                this.threadId = threadId;
                this.elapsed = elapsed;
                this.entries = entries;
                this.stackEntryCount = stackEntryCount;
                this.threadCount = threadCount;
                this.threadState = threadState;
                this.threadBatchIncrement = threadBatchIncrement;
            }
        }

        /// <summary>
        /// Writes the result to an external database.
        /// </summary>
        class PrimitiveTraceSqliteOutput
        {
            readonly SqliteConnection conn;

            public PrimitiveTraceSqliteOutput(string dbPath)
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }

                string cs = $"URI=file:{dbPath}";
                conn = new(cs);
                conn.Open();
                CreateTables();
            }

            void CreateTables()
            {
                using IDbCommand cmd = conn.CreateCommand();
                cmd.CommandText =
                    @"CREATE TABLE threads (
                  id INTEGER PRIMARY KEY ASC,
                  time INTEGER NOT NULL,
                  timestamp INTEGER NOT NULL,
                  thread_status INTEGER,
                  thread_id INTEGER,
                  thread_name TEXT,
                  is_current INTEGER NOT NULL
                )";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "CREATE INDEX index_threads_time ON threads (time);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "CREATE INDEX index_threads_is_current ON threads (is_current);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = @"
            CREATE TABLE stack_frames (
                  id INTEGER PRIMARY KEY ASC,
                  thread_id INTEGER NOT NULL,
                  stack_index INTEGER NOT NULL,
                  loc_class_name TEXT NOT NULL,
                  loc_method_name TEXT NOT NULL,
                  loc_method_type TEXT NOT NULL,
                  loc_code_index INTEGER NOT NULL,
                  loc_line_number INTEGER NOT NULL
                )";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "CREATE INDEX index_stack_frames_thread_id ON stack_frames (thread_id);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "CREATE INDEX index_stack_frames_stack_index ON stack_frames (stack_index);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "CREATE INDEX index_stack_frames_loc_class_name ON stack_frames (loc_class_name);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = @"
            CREATE TABLE method_exit_actions (
                  thread_id INTEGER NOT NULL,
                  loc_class_name TEXT NOT NULL,
                  loc_method_name TEXT NOT NULL,
                  loc_method_type TEXT NOT NULL,
                  object_id INTEGER NOT NULL,
                  timestamp INTEGER NOT NULL,
                  time INTEGER NOT NULL
                )";
                cmd.ExecuteNonQuery();

                cmd.CommandText = @"
            CREATE TABLE objects (
                  id INTEGER PRIMARY KEY ASC,
                  stack_frame_id INTEGER NOT NULL,
                  object_id INTEGER NOT NULL,
                  reference_type TEXT NOT NULL
                )";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "CREATE INDEX index_objects_stack_frame_id ON objects (stack_frame_id);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = @"
            CREATE TABLE repetitive_region (
                    id INTEGER PRIMARY KEY ASC,
                    step_from INTEGER NOT NULL,
                    step_to INTEGER NOT NULL,
                    method TEXT NOT NULL,
                    call_count INTEGER NOT NULL
                )";
                cmd.ExecuteNonQuery();
            }

            public void InsertThread(IEnumerable<Batch> batches)
            {
                using IDbCommand cmd = conn.CreateCommand();
                using IDbTransaction transaction = conn.BeginTransaction();
                cmd.CommandText =
                    @"INSERT INTO threads (
                      time,
                      timestamp,
                      thread_status,
                      thread_id,
                      thread_name,
                      is_current
                    ) VALUES (@Time, @Timestamp, @ThreadStatus, @ThreadId, @ThreadName, @IsCurrent)";

                foreach (Batch batch in batches)
                {
                    IDbDataParameter timeParameter =
                        cmd.CreateParameter();
                    timeParameter.DbType = DbType.Int32;
                    timeParameter.ParameterName = "@Time";
                    timeParameter.Value = batch.threadBatchIncrement;
                    cmd.Parameters.Add(timeParameter);

                    IDbDataParameter timestampParameter =
                        cmd.CreateParameter();
                    timestampParameter.DbType = DbType.Int64;
                    timestampParameter.ParameterName = "@Timestamp";
                    timestampParameter.Value = batch.elapsed;
                    cmd.Parameters.Add(timestampParameter);

                    IDbDataParameter threadStatusParameter =
                        cmd.CreateParameter();
                    threadStatusParameter.DbType = DbType.Int32;
                    threadStatusParameter.ParameterName = "@ThreadStatus";
                    threadStatusParameter.Value = batch.threadState;
                    cmd.Parameters.Add(threadStatusParameter);

                    IDbDataParameter threadIdParameter =
                        cmd.CreateParameter();
                    threadIdParameter.DbType = DbType.Int32;
                    threadIdParameter.ParameterName = "@ThreadId";
                    threadIdParameter.Value = batch.threadId;
                    cmd.Parameters.Add(threadIdParameter);

                    IDbDataParameter threadNameParameter =
                        cmd.CreateParameter();
                    threadNameParameter.DbType = DbType.String;
                    threadNameParameter.ParameterName = "@ThreadName";
                    threadNameParameter.Value = threadNamesById[batch.threadId];
                    cmd.Parameters.Add(threadNameParameter);

                    IDbDataParameter isCurrentParameter =
                        cmd.CreateParameter();
                    isCurrentParameter.DbType = DbType.Int32;
                    isCurrentParameter.ParameterName = "@IsCurrent";
                    isCurrentParameter.Value = batch.threadState == 2 ? 1 : 0;
                    cmd.Parameters.Add(isCurrentParameter);

                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            public void InsertStackFrames(IEnumerable<Batch> batches)
            {
                using IDbCommand cmd = conn.CreateCommand();
                using IDbTransaction transaction = conn.BeginTransaction();

                // TODO threadID is foreign key
                cmd.CommandText =
                    @"
                INSERT INTO stack_frames (
                      thread_id,
                      stack_index,
                      loc_class_name,
                      loc_method_name,
                      loc_method_type,
                      loc_code_index,
                      loc_line_number
                    ) VALUES (@ThreadId, @StackIndex, @LocClassName, @LocMethodName, @LocMethodType, -1, -1)";

                foreach (Batch batch in batches)
                {
                    int stackFrameCount = 0;
                    foreach (PrimitiveStackEntry stackElement in batch.entries)
                    {
                        IDbDataParameter threadIdParameter =
                            cmd.CreateParameter();
                        threadIdParameter.DbType = DbType.Int32;
                        threadIdParameter.ParameterName = "@ThreadId";
                        threadIdParameter.Value = batch.threadCount;
                        cmd.Parameters.Add(threadIdParameter);

                        IDbDataParameter stackIndexParameter =
                            cmd.CreateParameter();
                        stackIndexParameter.DbType = DbType.Int32;
                        stackIndexParameter.ParameterName = "@StackIndex";
                        stackIndexParameter.Value = stackFrameCount;
                        cmd.Parameters.Add(stackIndexParameter);
                        stackFrameCount++;

                        IDbDataParameter classNameParameter =
                            cmd.CreateParameter();
                        classNameParameter.DbType = DbType.String;
                        classNameParameter.ParameterName = "@LocClassName";
                        classNameParameter.Value =
                            ((ClassName)stackElement.MethodName.ContainmentParent).FullyQualifiedName;
                        cmd.Parameters.Add(classNameParameter);

                        IDbDataParameter methodNameParameter =
                            cmd.CreateParameter();
                        methodNameParameter.DbType = DbType.String;
                        methodNameParameter.ParameterName = "@LocMethodName";
                        methodNameParameter.Value = stackElement.MethodName.ShortName;
                        cmd.Parameters.Add(methodNameParameter);

                        IDbDataParameter methodTypeParameter =
                            cmd.CreateParameter();
                        methodTypeParameter.DbType = DbType.String;
                        methodTypeParameter.ParameterName = "@LocMethodType";
                        methodTypeParameter.Value = stackElement.MethodName.ReturnType;
                        cmd.Parameters.Add(methodTypeParameter);

                        cmd.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }

            public void InsertObject(IEnumerable<Batch> batches)
            {
                using IDbCommand cmd = conn.CreateCommand();
                using IDbTransaction transaction = conn.BeginTransaction();

                // TODO threadID is foreign key
                cmd.CommandText =
                    @"
                INSERT INTO objects (
                      stack_frame_id,
                      object_id,
                      reference_type
                    ) VALUES (@StackFrameId, @ObjectId, @ReferenceType)";

                foreach (Batch batch in batches)
                {
                    int count = 0;
                    foreach (PrimitiveStackEntry stackEntry in batch.entries)
                    {
                        if (stackEntry.ObjectId == -1)
                        {
                            count++;
                            continue;
                        }

                        IDbDataParameter stackFrameIdParameter =
                            cmd.CreateParameter();
                        stackFrameIdParameter.DbType = DbType.Int32;
                        stackFrameIdParameter.ParameterName = "@StackFrameId";
                        stackFrameIdParameter.Value = batch.stackEntryCount + count;
                        cmd.Parameters.Add(stackFrameIdParameter);
                        count++;

                        IDbDataParameter objectIdParameter =
                            cmd.CreateParameter();
                        objectIdParameter.DbType = DbType.Int64;
                        objectIdParameter.ParameterName = "@ObjectId";
                        objectIdParameter.Value = stackEntry.ObjectId;
                        cmd.Parameters.Add(objectIdParameter);

                        IDbDataParameter referenceTypeParameter =
                            cmd.CreateParameter();
                        referenceTypeParameter.DbType = DbType.String;
                        referenceTypeParameter.ParameterName = "@ReferenceType";
                        referenceTypeParameter.Value =
                            $"L{((ClassName)stackEntry.MethodName.ContainmentParent).FullyQualifiedName};"; // Java standard
                        cmd.Parameters.Add(referenceTypeParameter);

                        cmd.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
        }
    }
}