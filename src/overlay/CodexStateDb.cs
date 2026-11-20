using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexInputUsage
{
    internal sealed class StateThreadRecord
    {
        internal string Id;
        internal string RolloutPath;
        internal string Title;
        internal long TokensUsed;
        internal long RecencyAtMs;
    }

    internal static class CodexStateDb
    {
        private const int SqliteOpenReadOnly = 0x00000001;
        private const int SqliteOk = 0;
        private const int SqliteRow = 100;

        private const string ThreadColumns = "id, rollout_path, title, tokens_used, recency_at_ms";

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_open_v2(IntPtr filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_close_v2(IntPtr db);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_prepare_v2(IntPtr db, IntPtr sql, int byteCount, out IntPtr stmt, IntPtr tail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_bind_text(IntPtr stmt, int index, IntPtr value, int byteCount, IntPtr destructor);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int col);

        internal static string ResolveDatabasePath()
        {
            return Path.Combine(CodexThemeSource.GetCodexHome(), "state_5.sqlite");
        }

        internal static StateThreadRecord TryReadThread(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return null;
            return QuerySingle(
                ResolveDatabasePath(),
                "SELECT " + ThreadColumns + " FROM threads WHERE id = ?1 LIMIT 1",
                threadId);
        }

        internal static string FindRolloutFileByThreadId(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return null;
            StateThreadRecord rec = TryReadThread(threadId);
            if (rec != null && !string.IsNullOrWhiteSpace(rec.RolloutPath) && File.Exists(rec.RolloutPath))
                return rec.RolloutPath;

            string sessionsRoot = Path.Combine(CodexThemeSource.GetCodexHome(), "sessions");
            if (!Directory.Exists(sessionsRoot)) return null;
            try
            {
                string pattern = "*" + threadId + ".jsonl";
                string[] hits = Directory.GetFiles(sessionsRoot, pattern, SearchOption.AllDirectories);
                if (hits.Length > 0) return hits[hits.Length - 1];
            }
            catch { }
            return null;
        }

        private static StateThreadRecord QuerySingle(string dbPath, string sql, string parameter)
        {
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return null;

            IntPtr db = IntPtr.Zero;
            IntPtr stmt = IntPtr.Zero;
            IntPtr dbPtr = IntPtr.Zero;
            IntPtr sqlPtr = IntPtr.Zero;
            IntPtr paramPtr = IntPtr.Zero;

            try
            {
                dbPtr = AllocUtf8(dbPath);
                if (sqlite3_open_v2(dbPtr, out db, SqliteOpenReadOnly, IntPtr.Zero) != SqliteOk) return null;

                sqlPtr = AllocUtf8(sql);
                if (sqlite3_prepare_v2(db, sqlPtr, -1, out stmt, IntPtr.Zero) != SqliteOk) return null;

                if (parameter != null)
                {
                    paramPtr = AllocUtf8(parameter);
                    if (sqlite3_bind_text(stmt, 1, paramPtr, -1, IntPtr.Zero) != SqliteOk) return null;
                }

                if (sqlite3_step(stmt) != SqliteRow) return null;

                return new StateThreadRecord
                {
                    Id = ReadUtf8(stmt, 0) ?? string.Empty,
                    RolloutPath = ReadUtf8(stmt, 1),
                    Title = ReadUtf8(stmt, 2),
                    TokensUsed = sqlite3_column_int64(stmt, 3),
                    RecencyAtMs = sqlite3_column_int64(stmt, 4)
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                if (stmt != IntPtr.Zero) { try { sqlite3_finalize(stmt); } catch { } }
                if (db != IntPtr.Zero) { try { sqlite3_close_v2(db); } catch { } }
                if (dbPtr != IntPtr.Zero) Marshal.FreeHGlobal(dbPtr);
                if (sqlPtr != IntPtr.Zero) Marshal.FreeHGlobal(sqlPtr);
                if (paramPtr != IntPtr.Zero) Marshal.FreeHGlobal(paramPtr);
            }
        }

        private static IntPtr AllocUtf8(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0);
            return ptr;
        }

        private static string ReadUtf8(IntPtr stmt, int col)
        {
            IntPtr ptr = sqlite3_column_text(stmt, col);
            if (ptr == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return string.Empty;
            byte[] buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }
    }
}
