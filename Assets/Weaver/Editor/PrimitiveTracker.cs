#nullable enable
using System;
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
using Object = UnityEngine.Object;

namespace Weaver.Editor
{
    public static class PrimitiveTracker
    {
        /// <summary>
        /// Allows us to generate a unique ID for each instance of every class that we see.
        /// </summary>
        static ObjectIDGenerator objectIDGenerator;

        static PrimitiveTraceSqliteOutput primitiveTraceSqliteOutput;

        static Dictionary<int, Stack<PrimitiveStackEntry>?> callStacksByThreadId;
        static Dictionary<int, string> threadNamesById;

        static Stopwatch sw;

        static bool verbose;
        
        static bool first = true;
        
        static readonly List<Batch> accumulatedEntries = new();
        
        static int lastStackEntryCount = 1;
        static int lastThreadCount = 1;

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
            threadNamesById = new();

            primitiveTraceSqliteOutput = new(DbDefaultPath);
            verbose = WeaverSettings.Instance != null && WeaverSettings.Instance.m_Verbose;
            
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
        public static void OnInstanceEntry(object traceObject, string methodDefinition)
        {
            if (!CheckPlayingAndInitialize()) return;

            int threadId = Environment.CurrentManagedThreadId;
            string[] split = methodDefinition.Split('|');
            threadId = CheckMessageThreadId(traceObject, split[1], threadId);
            PrimitiveStackEntry primitiveStackEntry = EntryFromInfos(traceObject, methodDefinition, threadId);

            //StackTrace stackTrace = new StackTrace(false);
            //List<string> stackMethods = StackMethods(stackTrace);

            if (verbose)
            {
                Log(primitiveStackEntry.MethodName.FullyQualifiedName,
                    primitiveStackEntry.ObjectId,
                    threadId,
                    true);
            }

            PushStackAndWrite(primitiveStackEntry, threadId);
        }

        static int CheckMessageThreadId(object traceObject, string methodShortName, int threadId)
        {
            if (traceObject is not Object traceObjectObject) return threadId;
            
            if (!UnityMessages.Contains(methodShortName)) return threadId;
            
            callStacksByThreadId.TryGetValue(threadId, out Stack<PrimitiveStackEntry>? stack);
            
            if (stack == null || !stack.Any()) return threadId;
            if (verbose)
            {
                Debug.LogWarning($"Unity Message invocation interrupting current stack: {methodShortName}.\n This should not happen, because the previous stack should have returned on this thread before the next Unity Message was called");
            }

            return Math.Abs(traceObjectObject.GetInstanceID());
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
                    stackMethods.Add( $"{traceMethodDeclType}.{traceMethodName}");
                }
            }

            return stackMethods;
        }
        
        [PublicAPI]
        public static void OnStaticEntry(string methodDefinition)
        {
            if (!CheckPlayingAndInitialize()) return;
            
            int threadId = Environment.CurrentManagedThreadId;
            PrimitiveStackEntry primitiveStackEntry = EntryFromInfos(null, methodDefinition, threadId);
            if (verbose)
            {
                Log(primitiveStackEntry.MethodName.FullyQualifiedName,
                    primitiveStackEntry.ObjectId,
                    threadId,
                    true);
            }

            PushStackAndWrite(primitiveStackEntry, threadId);
        }

        static void PushStackAndWrite(PrimitiveStackEntry primitiveStackEntry, int threadId)
        {
            callStacksByThreadId.TryGetValue(threadId, out Stack<PrimitiveStackEntry>? stack);
            if (stack == null)
            {
                stack = new Stack<PrimitiveStackEntry>();
                callStacksByThreadId.Add(threadId, stack);
            }

            stack.Push(primitiveStackEntry);
            IEnumerable<MethodName> stackMethods = stack.Select(x => x.MethodName);

            Batch newBatch = new(
                threadId, 
                sw.ElapsedMilliseconds, 
                stackMethods.ToList(), 
                stack.ToList(), 
                lastStackEntryCount, 
                lastThreadCount);
            
            lastStackEntryCount += stack.Count;
            lastThreadCount++;
            accumulatedEntries.Add(newBatch);
            
            if (threadId == 1 && accumulatedEntries.Count > 2000)
            {
                // only write from the main thread
                WriteAll();
            }
        }

        static void WriteAll()
        {
            Debug.Log($"Writing {accumulatedEntries.Count} entries...");

            IEnumerable<Tuple<int, long, int>> threadIdsAndTimes =
                accumulatedEntries.Select(x => new Tuple<int, long, int>(x.threadId, x.elapsed, x.threadCount));
            primitiveTraceSqliteOutput.InsertThread(threadIdsAndTimes.ToList());
                
            IEnumerable<Tuple<int, MethodName[]>> stacksToInsert = accumulatedEntries
                .Select(x => new Tuple<int, MethodName[]>(x.threadCount, x.stacks));
            primitiveTraceSqliteOutput.InsertStackFrames(stacksToInsert.ToList());

            IEnumerable<Tuple<int, PrimitiveStackEntry[]>> objectsToInsert =
                accumulatedEntries.Select(x =>
                    new Tuple<int, PrimitiveStackEntry[]>(x.stackEntryCount, x.entries));
            primitiveTraceSqliteOutput.InsertObject(objectsToInsert.ToList());
            
            Debug.Log($"Wrote {accumulatedEntries.Count} entries");
            
            accumulatedEntries.Clear();
        }

        [PublicAPI]
        public static void OnInstanceExit(object traceObject, string methodDefinition)
        {
            if (!CheckPlayingAndInitialize()) return;
            
            int threadId = Environment.CurrentManagedThreadId;
            string[] split = methodDefinition.Split('|');
            threadId = CheckMessageThreadId(traceObject, split[1], threadId);
            
            PrimitiveStackEntry primitiveStackEntry = EntryFromInfos(traceObject, methodDefinition, threadId);

            if (verbose)
            {
                Log(primitiveStackEntry.MethodName.FullyQualifiedName,
                    primitiveStackEntry.ObjectId,
                    threadId,
                    false);
            }

            PopStack(primitiveStackEntry, threadId);
        }
        
        [PublicAPI]
        public static void OnStaticExit(string methodDefinition)
        {
            if (!CheckPlayingAndInitialize()) return;
            
            int threadId = Environment.CurrentManagedThreadId;
            PrimitiveStackEntry primitiveStackEntry = EntryFromInfos(null, methodDefinition, threadId);
            if (verbose)
            {
                if (verbose)
                {
                    Log(primitiveStackEntry.MethodName.FullyQualifiedName,
                        primitiveStackEntry.ObjectId,
                        threadId,
                        false);
                }
            }

            PopStack(primitiveStackEntry, threadId);
        }

        static void PopStack(PrimitiveStackEntry primitiveStackEntry, int threadId)
        {
            callStacksByThreadId.TryGetValue(threadId, out Stack<PrimitiveStackEntry>? stack);
            if (stack == null) return;
            int removeHashCode = primitiveStackEntry.GetHashCode();
            int topHashCode = -1;
            while (stack.Count > 0 && topHashCode != removeHashCode)
            {
                topHashCode = stack.Pop().GetHashCode();
            }
        }
        
        static PrimitiveStackEntry EntryFromInfos(object? traceObject, string methodDefinition, int threadId)
        {
            MethodName methodName = FromFQN(methodDefinition);
            long classInstanceId = -1;
            if (traceObject != null)
            {
                classInstanceId = InstanceIdFrom(traceObject, threadId);
            }

            if (!threadNamesById.ContainsKey(threadId))
            {
                string threadName = Thread.CurrentThread.Name ?? threadId.ToString();
                threadNamesById.Add(threadId, threadName);
            }

            PrimitiveStackEntry primitiveStackEntry = new(methodName, classInstanceId);
            return primitiveStackEntry;
        }

        static long InstanceIdFrom(object traceObject, int threadId)
        {
            bool isNotUnityManaged = true;
            long classInstanceId = -1;
            
            if (!threadNamesById.ContainsKey(threadId))
            {
                string threadName = Thread.CurrentThread.Name ?? threadId.ToString();
                threadNamesById.Add(threadId, threadName);
            }

            if (traceObject is Object traceObjectObject)
            {
                //isNotUnityManaged = false;
                //classInstanceId = traceObjectObject.GetInstanceID();
            }

            if (traceObject is Component traceObjectComponent)
            {
                //isNotUnityManaged = false;
                // gameobject
                //classInstanceId = traceObjectComponent.gameObject.GetInstanceID();
            }

            if (isNotUnityManaged)
            {
                classInstanceId = objectIDGenerator.GetId(traceObject, out bool firstTime);
            }

            return classInstanceId;
        }

        static void Log(string methodName, long objectId, int threadId, bool isEntering)
        {
            string entering = isEntering ? "Entering" : "Exiting";
            Debug.Log($"{entering} Method {methodName} on instance {objectId} on thread {threadId}");
        }

        static MethodName FromFQN(string methodFqn)
        {
            string[] split = methodFqn.Split('|');
            string classFqn = split[0];
            string namespaceName = "";
            if (classFqn.Contains('.'))
            {
                namespaceName = classFqn[..classFqn.LastIndexOf('.')];
                classFqn = classFqn[(classFqn.LastIndexOf('.') + 1)..];
            }

            ClassName parentClass = new ClassName(
                new FileName(""),
                new PackageName(namespaceName),
                classFqn);
            MethodName methodName = new MethodName(
                parentClass,
                split[1],
                split[2],
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
            public readonly MethodName[] stacks;
            public readonly PrimitiveStackEntry[] entries;

            public Batch(int threadId, long elapsed, List<MethodName> stacks, List<PrimitiveStackEntry> entries, int stackEntryCount, int threadCount)
            {
                this.threadId = threadId;
                this.elapsed = elapsed;
                this.stacks = stacks.ToArray();
                this.entries = entries.ToArray();
                this.stackEntryCount = stackEntryCount;
                this.threadCount = threadCount;
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

            public void InsertThread(IEnumerable<Tuple<int, long, int>> threadIdsAndTimes)
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
                    ) VALUES (@Time, @Timestamp, 2, @ThreadId, @ThreadName, 1)";

                foreach (Tuple<int, long, int> threadIdAndTime in threadIdsAndTimes)
                {
                    IDbDataParameter timeParameter =
                        cmd.CreateParameter();
                    timeParameter.DbType = DbType.Int32;
                    timeParameter.ParameterName = "@Time";
                    timeParameter.Value = threadIdAndTime.Item3;
                    cmd.Parameters.Add(timeParameter);

                    IDbDataParameter timestampParameter =
                        cmd.CreateParameter();
                    timestampParameter.DbType = DbType.Int64;
                    timestampParameter.ParameterName = "@Timestamp";
                    timestampParameter.Value = threadIdAndTime.Item2;
                    cmd.Parameters.Add(timestampParameter);

                    IDbDataParameter threadIdParameter =
                        cmd.CreateParameter();
                    threadIdParameter.DbType = DbType.Int32;
                    threadIdParameter.ParameterName = "@ThreadId";
                    threadIdParameter.Value = threadIdAndTime.Item1;
                    cmd.Parameters.Add(threadIdParameter);

                    IDbDataParameter threadNameParameter =
                        cmd.CreateParameter();
                    threadNameParameter.DbType = DbType.String;
                    threadNameParameter.ParameterName = "@ThreadName";
                    threadNameParameter.Value = threadNamesById[threadIdAndTime.Item1];
                    cmd.Parameters.Add(threadNameParameter);

                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            public void InsertStackFrames(IEnumerable<Tuple<int, MethodName[]>> stacks)
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

                foreach (Tuple<int, MethodName[]> tuple in stacks)
                {
                    int stackFrameCount = 0;
                    foreach (MethodName stackElement in tuple.Item2)
                    {
                        IDbDataParameter threadIdParameter =
                            cmd.CreateParameter();
                        threadIdParameter.DbType = DbType.Int32;
                        threadIdParameter.ParameterName = "@ThreadId";
                        threadIdParameter.Value = tuple.Item1;
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
                        classNameParameter.Value = ((ClassName)stackElement.ContainmentParent).FullyQualifiedName;
                        cmd.Parameters.Add(classNameParameter);

                        IDbDataParameter methodNameParameter =
                            cmd.CreateParameter();
                        methodNameParameter.DbType = DbType.String;
                        methodNameParameter.ParameterName = "@LocMethodName";
                        methodNameParameter.Value = stackElement.ShortName;
                        cmd.Parameters.Add(methodNameParameter);

                        IDbDataParameter methodTypeParameter =
                            cmd.CreateParameter();
                        methodTypeParameter.DbType = DbType.String;
                        methodTypeParameter.ParameterName = "@LocMethodType";
                        methodTypeParameter.Value = stackElement.ReturnType;
                        cmd.Parameters.Add(methodTypeParameter);

                        cmd.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }

            public void InsertObject(IEnumerable<Tuple<int, PrimitiveStackEntry[]>> stacks)
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

                foreach (Tuple<int, PrimitiveStackEntry[]> stack in stacks)
                {
                    int count = 0;
                    foreach (PrimitiveStackEntry stackEntry in stack.Item2)
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
                        stackFrameIdParameter.Value = stack.Item1 + count;
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
                            $"L{((ClassName)stackEntry.MethodName.ContainmentParent).FullyQualifiedName};";
                        cmd.Parameters.Add(referenceTypeParameter);

                        cmd.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
        }

        // from: https://docs.unity3d.com/ScriptReference/MonoBehaviour.html
        static HashSet<string> UnityMessages = new()
        {
            "Awake",
            "FixedUpdate",
            "LateUpdate",
            "OnAnimatorIK",
            "OnAnimatorMove",
            "OnApplicationFocus",
            "OnApplicationPause",
            "OnApplicationQuit",
            "OnAudioFilterRead",
            "OnBecameInvisible",
            "OnBecameVisible",
            "OnCollisionEnter",
            "OnCollisionEnter2D",
            "OnCollisionExit",
            "OnCollisionExit2D",
            "OnCollisionStay",
            "OnCollisionStay2D",
            "OnConnectedToServer",
            "OnControllerColliderHit",
            "OnDestroy",
            "OnDisable",
            "OnDisconnectedFromServer",
            "OnDrawGizmos",
            "OnDrawGizmosSelected",
            "OnEnable",
            "OnFailedToConnect",
            "OnFailedToConnectToMasterServer",
            "OnGUI",
            "OnJointBreak",
            "OnJointBreak2D",
            "OnMasterServerEvent",
            "OnMouseDown",
            "OnMouseDrag",
            "OnMouseEnter",
            "OnMouseExit",
            "OnMouseOver",
            "OnMouseUp",
            "OnMouseUpAsButton",
            "OnNetworkInstantiate",
            "OnParticleCollision",
            "OnParticleSystemStopped",
            "OnParticleTrigger",
            "OnParticleUpdateJobScheduled",
            "OnPlayerConnected",
            "OnPlayerDisconnected",
            "OnPostRender",
            "OnPreCull",
            "OnPreRender",
            "OnRenderImage",
            "OnRenderObject",
            "OnSerializeNetworkView",
            "OnServerInitialized",
            "OnTransformChildrenChanged",
            "OnTransformParentChanged",
            "OnTriggerEnter",
            "OnTriggerEnter2D",
            "OnTriggerExit",
            "OnTriggerExit2D",
            "OnTriggerStay",
            "OnTriggerStay2D",
            "OnValidate",
            "OnWillRenderObject",
            "Reset",
            "Start",
            "Update"
        };
    }
}