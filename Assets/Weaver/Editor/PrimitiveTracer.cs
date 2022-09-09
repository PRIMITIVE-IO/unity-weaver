using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using JetBrains.Annotations;
using Mono.Data.Sqlite;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Weaver.Editor
{
    public static class PrimitiveTracker
    {
        /// <summary>
        /// Allows us to generate a unique ID for each instance of every class that we see.
        /// </summary>
        static readonly ObjectIDGenerator objectIDGenerator = new();

        static readonly PrimitiveTraceSqliteOutput primitiveTraceSqliteOutput = new(DbDefaultPath);

        static readonly Dictionary<int, List<PrimitiveStackEntry>?> callStacksByThreadId = new();

        static Stopwatch sw = Stopwatch.StartNew();

        static string DbDefaultPath
        {
            get
            {
                string codeBase = Assembly.GetExecutingAssembly().CodeBase;
                UriBuilder uri = new UriBuilder(codeBase);
                string path = Uri.UnescapeDataString(uri.Path);
                return Path.Combine(Path.GetDirectoryName(path), $"{DateTime.Now.ToString("yyyyMMddHHmmssfff")}.db");
            }
        }

        [PublicAPI]
        public static void OnEntry(object traceObject, string methodNameString)
        {
            MethodName methodName = MethodNameFromString(methodNameString);
            long classInstanceId = InstanceIdFrom(traceObject);
            int threadId = Environment.CurrentManagedThreadId;

            Console.WriteLine($"Entering Method {methodName.ShortName} on {classInstanceId} on thread {threadId}");
            PrimitiveStackEntry primitiveStackEntry = new(methodName, classInstanceId);

            callStacksByThreadId.TryGetValue(threadId, out List<PrimitiveStackEntry>? stack);
            if (stack == null)
            {
                stack = new List<PrimitiveStackEntry>();
                callStacksByThreadId.Add(threadId, stack);
            }

            stack.Add(primitiveStackEntry);
            IEnumerable<MethodName> stackMethods = stack.Select(x => x.MethodName);

            primitiveTraceSqliteOutput.InsertThread(threadId, sw.ElapsedMilliseconds);
            primitiveTraceSqliteOutput.InsertStackFrames(stackMethods.Reverse().ToList());
            primitiveTraceSqliteOutput.InsertObject(stack.ToList());
        }

        [PublicAPI]
        public static void OnExit(object traceObject, string methodNameString)
        {
            MethodName methodName = MethodNameFromString(methodNameString);
            long classInstanceId = InstanceIdFrom(traceObject);

            int threadId = Environment.CurrentManagedThreadId;

            PrimitiveStackEntry primitiveStackEntry = new(methodName, classInstanceId);
            callStacksByThreadId.TryGetValue(threadId, out List<PrimitiveStackEntry>? stack);
            if (stack == null) return;
            int idToRemove = -1;
            int removeHashCode = primitiveStackEntry.GetHashCode();
            int count = 0;
            foreach (PrimitiveStackEntry entry in stack)
            {
                if (entry.GetHashCode() == removeHashCode)
                {
                    idToRemove = count;
                    break;
                }

                count++;
            }

            if (idToRemove > -1)
            {
                stack.RemoveAt(idToRemove);
            }
        }

        static MethodName MethodNameFromString(string methodNameString)
        {
            string classFqn = methodNameString[..methodNameString.LastIndexOf('.')];
            string classNameShort = classFqn;
            string namespaceName = "";
            if (classFqn.Contains('.'))
            {
                classNameShort = classFqn[(classFqn.IndexOf('.') + 1)..];
                namespaceName = classFqn[..classFqn.IndexOf('.')];
            }

            ClassName parentClass = new ClassName(
                new FileName(""),
                new PackageName(namespaceName),
                classNameShort);
            MethodName methodName = new MethodName(
                parentClass,
                methodNameString,
                "()L;",
                new List<Argument>());
            return methodName;
        }

        static long InstanceIdFrom(object traceObject)
        {
            bool isNotUnityManaged = true;
            long classInstanceId = -1;
            if (traceObject is Object traceObjectObject)
            {
                isNotUnityManaged = false;
                classInstanceId = traceObjectObject.GetInstanceID();
            }

            if (traceObject is Component traceObjectComponent)
            {
                isNotUnityManaged = false;
                // gameobject
                //classInstanceId = traceObjectComponent.gameObject.GetInstanceID();
            }

            if (isNotUnityManaged)
            {
                classInstanceId = objectIDGenerator.GetId(traceObject, out bool firstTime);
            }

            return classInstanceId;
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

        /// <summary>
        /// Writes the result to an external database.
        /// </summary>
        class PrimitiveTraceSqliteOutput
        {
            readonly SqliteConnection conn;
            int currentThreadEntry = 1;
            int currentStackEntry = 1;

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

            public void InsertThread(int threadId, long timeStamp)
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
                    ) VALUES (@Time, @Timestamp, 2, 1, @ThreadName, 1)";


                IDbDataParameter timeParameter =
                    cmd.CreateParameter();
                timeParameter.DbType = DbType.Int32;
                timeParameter.ParameterName = "@Time";
                timeParameter.Value = currentThreadEntry;
                cmd.Parameters.Add(timeParameter);

                IDbDataParameter timestampParameter =
                    cmd.CreateParameter();
                timestampParameter.DbType = DbType.Int64;
                timestampParameter.ParameterName = "@Timestamp";
                timestampParameter.Value = timeStamp;
                cmd.Parameters.Add(timestampParameter);

                IDbDataParameter threadNameParameter =
                    cmd.CreateParameter();
                threadNameParameter.DbType = DbType.String;
                threadNameParameter.ParameterName = "@ThreadName";
                threadNameParameter.Value = threadId.ToString();
                cmd.Parameters.Add(threadNameParameter);

                cmd.ExecuteNonQuery();
                transaction.Commit();
            }

            public void InsertStackFrames(List<MethodName> stack)
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

                int stackFrameCount = 0;
                foreach (MethodName stackElement in stack)
                {
                    IDbDataParameter threadIdParameter =
                        cmd.CreateParameter();
                    threadIdParameter.DbType = DbType.Int32;
                    threadIdParameter.ParameterName = "@ThreadId";
                    threadIdParameter.Value = currentThreadEntry;
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
                    classNameParameter.Value = stackElement.ContainmentParent.ShortName;
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

                currentThreadEntry++;

                transaction.Commit();
            }

            public void InsertObject(List<PrimitiveStackEntry> stack)
            {
                stack.Reverse();
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

                int count = 0;
                foreach (PrimitiveStackEntry stackEntry in stack)
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
                    stackFrameIdParameter.Value = currentStackEntry + count;
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
                    referenceTypeParameter.Value = $"L{stackEntry.MethodName.ContainmentParent.ShortName};";
                    cmd.Parameters.Add(referenceTypeParameter);

                    cmd.ExecuteNonQuery();
                }

                currentStackEntry += stack.Count;

                transaction.Commit();
            }
        }
    }
}