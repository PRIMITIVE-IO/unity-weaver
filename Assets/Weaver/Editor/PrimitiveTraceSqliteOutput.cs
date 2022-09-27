using System.Collections.Generic;
using System.Data;
using System.IO;
using Mono.Data.Sqlite;

namespace Weaver.Editor
{
    public struct Batch
    {
        public readonly int threadCount;
        public readonly int stackEntryCount;
        public readonly int threadId;
        public readonly long elapsed;
        public readonly List<PrimitiveTracker.PrimitiveStackEntry> entries;
        public readonly int threadState;
        public readonly int threadBatchIncrement;

        public Batch(
            int threadId,
            long elapsed,
            List<PrimitiveTracker.PrimitiveStackEntry> entries,
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
    public class PrimitiveTraceSqliteOutput
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

        public void InsertThread(IEnumerable<Batch> batches, Dictionary<int, string> threadNamesById)
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
                foreach (PrimitiveTracker.PrimitiveStackEntry stackElement in batch.entries)
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
                foreach (PrimitiveTracker.PrimitiveStackEntry stackEntry in batch.entries)
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