using System;
using System.Runtime.InteropServices;

namespace SimuLean.Data
{
    /// <summary>
    /// Minimal dual-provider SQLite P/Invoke layer. Tries the canonical
    /// "sqlite3" native library first (drop a sqlite3.dll into Assets/Plugins to
    /// use a specific build); when absent it falls back to "winsqlite3.dll",
    /// which ships with Windows 10/11 — so no download or extra native plugin
    /// is required on Windows. Works under any Unity API compatibility level
    /// (no Mono.Data.Sqlite / System.Data dependency).
    /// UTF-16 (*16) SQLite entry points are used throughout so .NET strings
    /// marshal without manual UTF-8 handling.
    /// </summary>
    internal static class SqliteInterop
    {
        internal const int SQLITE_OK   = 0;
        internal const int SQLITE_ROW  = 100;
        internal const int SQLITE_DONE = 101;

        internal const int SQLITE_INTEGER = 1;
        internal const int SQLITE_FLOAT   = 2;
        internal const int SQLITE_TEXT    = 3;
        internal const int SQLITE_BLOB    = 4;
        internal const int SQLITE_NULL    = 5;

        /// <summary>SQLITE_TRANSIENT — SQLite copies bound text immediately.</summary>
        internal static readonly IntPtr Transient = new IntPtr(-1);

        private static class Native
        {
            private const string LIB = "sqlite3";

            [DllImport(LIB)] internal static extern int    sqlite3_open16([MarshalAs(UnmanagedType.LPWStr)] string filename, out IntPtr db);
            [DllImport(LIB)] internal static extern int    sqlite3_close(IntPtr db);
            [DllImport(LIB)] internal static extern int    sqlite3_prepare16_v2(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string sql, int numBytes, out IntPtr stmt, IntPtr pzTail);
            [DllImport(LIB)] internal static extern int    sqlite3_step(IntPtr stmt);
            [DllImport(LIB)] internal static extern int    sqlite3_finalize(IntPtr stmt);
            [DllImport(LIB)] internal static extern int    sqlite3_column_count(IntPtr stmt);
            [DllImport(LIB)] internal static extern IntPtr sqlite3_column_name16(IntPtr stmt, int index);
            [DllImport(LIB)] internal static extern int    sqlite3_column_type(IntPtr stmt, int index);
            [DllImport(LIB)] internal static extern long   sqlite3_column_int64(IntPtr stmt, int index);
            [DllImport(LIB)] internal static extern double sqlite3_column_double(IntPtr stmt, int index);
            [DllImport(LIB)] internal static extern IntPtr sqlite3_column_text16(IntPtr stmt, int index);
            [DllImport(LIB)] internal static extern IntPtr sqlite3_errmsg16(IntPtr db);
            [DllImport(LIB)] internal static extern int    sqlite3_bind_text16(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPWStr)] string value, int numBytes, IntPtr destructor);
            [DllImport(LIB)] internal static extern int    sqlite3_bind_int64(IntPtr stmt, int index, long value);
            [DllImport(LIB)] internal static extern int    sqlite3_bind_double(IntPtr stmt, int index, double value);
            [DllImport(LIB)] internal static extern IntPtr sqlite3_libversion();
            [DllImport(LIB)] internal static extern int    sqlite3_reset(IntPtr stmt);
            [DllImport(LIB)] internal static extern int    sqlite3_clear_bindings(IntPtr stmt);
        }

        private static class WinNative
        {
            private const string LIB = "winsqlite3.dll";

            [DllImport(LIB)] internal static extern int    sqlite3_open16([MarshalAs(UnmanagedType.LPWStr)] string filename, out IntPtr db);
            [DllImport(LIB)] internal static extern int    sqlite3_close(IntPtr db);
            [DllImport(LIB)] internal static extern int    sqlite3_prepare16_v2(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string sql, int numBytes, out IntPtr stmt, IntPtr pzTail);
            [DllImport(LIB)] internal static extern int    sqlite3_step(IntPtr stmt);
            [DllImport(LIB)] internal static extern int    sqlite3_finalize(IntPtr stmt);
            [DllImport(LIB)] internal static extern int    sqlite3_column_count(IntPtr stmt);
            [DllImport(LIB)] internal static extern IntPtr sqlite3_column_name16(IntPtr stmt, int index);
            [DllImport(LIB)] internal static extern int    sqlite3_column_type(IntPtr stmt, int index);
            [DllImport(LIB)] internal static extern long   sqlite3_column_int64(IntPtr stmt, int index);
            [DllImport(LIB)] internal static extern double sqlite3_column_double(IntPtr stmt, int index);
            [DllImport(LIB)] internal static extern IntPtr sqlite3_column_text16(IntPtr stmt, int index);
            [DllImport(LIB)] internal static extern IntPtr sqlite3_errmsg16(IntPtr db);
            [DllImport(LIB)] internal static extern int    sqlite3_bind_text16(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPWStr)] string value, int numBytes, IntPtr destructor);
            [DllImport(LIB)] internal static extern int    sqlite3_bind_int64(IntPtr stmt, int index, long value);
            [DllImport(LIB)] internal static extern int    sqlite3_bind_double(IntPtr stmt, int index, double value);
            [DllImport(LIB)] internal static extern IntPtr sqlite3_libversion();
            [DllImport(LIB)] internal static extern int    sqlite3_reset(IntPtr stmt);
            [DllImport(LIB)] internal static extern int    sqlite3_clear_bindings(IntPtr stmt);
        }

        private static int provider; // 0 = undetermined, 1 = sqlite3, 2 = winsqlite3
        private static readonly object providerLock = new object();

        /// <summary>Selects the first native provider that loads; throws when neither is available.</summary>
        private static int Provider
        {
            get
            {
                if (provider != 0) return provider;
                lock (providerLock)
                {
                    if (provider != 0) return provider;
                    try { Native.sqlite3_libversion(); provider = 1; }
                    catch (DllNotFoundException)
                    {
                        try { WinNative.sqlite3_libversion(); provider = 2; }
                        catch (DllNotFoundException)
                        {
                            throw new NotSupportedException(
                                "SimuLean.Data: no SQLite native library found. Place a sqlite3.dll " +
                                "in Assets/Plugins (or run on Windows 10/11 where winsqlite3.dll is built in).");
                        }
                    }
                    return provider;
                }
            }
        }

        // ── dispatching wrappers ─────────────────────────────────────────────

        internal static int    Open(string filename, out IntPtr db)                    => Provider == 1 ? Native.sqlite3_open16(filename, out db)                    : WinNative.sqlite3_open16(filename, out db);
        internal static int    Close(IntPtr db)                                        => Provider == 1 ? Native.sqlite3_close(db)                                    : WinNative.sqlite3_close(db);
        internal static int    Prepare(IntPtr db, string sql, out IntPtr stmt)         => Provider == 1 ? Native.sqlite3_prepare16_v2(db, sql, -1, out stmt, IntPtr.Zero) : WinNative.sqlite3_prepare16_v2(db, sql, -1, out stmt, IntPtr.Zero);
        internal static int    Step(IntPtr stmt)                                       => Provider == 1 ? Native.sqlite3_step(stmt)                                   : WinNative.sqlite3_step(stmt);
        internal static int    Finalize(IntPtr stmt)                                   => Provider == 1 ? Native.sqlite3_finalize(stmt)                               : WinNative.sqlite3_finalize(stmt);
        internal static int    ColumnCount(IntPtr stmt)                                => Provider == 1 ? Native.sqlite3_column_count(stmt)                           : WinNative.sqlite3_column_count(stmt);
        internal static string ColumnName(IntPtr stmt, int i)                          => Marshal.PtrToStringUni(Provider == 1 ? Native.sqlite3_column_name16(stmt, i) : WinNative.sqlite3_column_name16(stmt, i));
        internal static int    ColumnType(IntPtr stmt, int i)                          => Provider == 1 ? Native.sqlite3_column_type(stmt, i)                         : WinNative.sqlite3_column_type(stmt, i);
        internal static long   ColumnInt64(IntPtr stmt, int i)                         => Provider == 1 ? Native.sqlite3_column_int64(stmt, i)                        : WinNative.sqlite3_column_int64(stmt, i);
        internal static double ColumnDouble(IntPtr stmt, int i)                        => Provider == 1 ? Native.sqlite3_column_double(stmt, i)                       : WinNative.sqlite3_column_double(stmt, i);
        internal static string ColumnText(IntPtr stmt, int i)                          => Marshal.PtrToStringUni(Provider == 1 ? Native.sqlite3_column_text16(stmt, i) : WinNative.sqlite3_column_text16(stmt, i));
        internal static string ErrMsg(IntPtr db)                                       => Marshal.PtrToStringUni(Provider == 1 ? Native.sqlite3_errmsg16(db)           : WinNative.sqlite3_errmsg16(db));
        internal static int    BindText(IntPtr stmt, int index, string value)          => Provider == 1 ? Native.sqlite3_bind_text16(stmt, index, value, -1, Transient) : WinNative.sqlite3_bind_text16(stmt, index, value, -1, Transient);
        internal static int    BindInt64(IntPtr stmt, int index, long value)           => Provider == 1 ? Native.sqlite3_bind_int64(stmt, index, value)               : WinNative.sqlite3_bind_int64(stmt, index, value);
        internal static int    BindDouble(IntPtr stmt, int index, double value)        => Provider == 1 ? Native.sqlite3_bind_double(stmt, index, value)              : WinNative.sqlite3_bind_double(stmt, index, value);
        internal static int    Reset(IntPtr stmt)                                      => Provider == 1 ? Native.sqlite3_reset(stmt)                                  : WinNative.sqlite3_reset(stmt);
        internal static int    ClearBindings(IntPtr stmt)                              => Provider == 1 ? Native.sqlite3_clear_bindings(stmt)                         : WinNative.sqlite3_clear_bindings(stmt);
    }
}
