using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Diagnostics;

namespace SnisDrivingSchool
{
    // Shared database helpers used by every controller.
    public static class Db
    {
        // The "SnisDb" connection string from Web.config
        public static string ConnStr
        {
            get { return ConfigurationManager.ConnectionStrings["SnisDb"].ConnectionString; }
        }

        // Inserts a row and returns its new ID.
        // Works whether the ID column is an IDENTITY column or not: if it is, SQL Server picks the ID;
        // if not, the next ID is MAX(id) + 1, locked so two users can't get the same number.
        // table / idCol / columns / values are always fixed text from our own code, never user input.
        public static int InsertRow(SqlConnection con, SqlTransaction tx, string table, string idCol,
                                    string columns, string values, Action<SqlCommand> addParams)
        {
            bool identity;
            using (var cmd = new SqlCommand("SELECT COLUMNPROPERTY(OBJECT_ID(@t), @c, 'IsIdentity')", con, tx))
            {
                cmd.Parameters.AddWithValue("@t", "dbo." + table);
                cmd.Parameters.AddWithValue("@c", idCol);
                object o = cmd.ExecuteScalar();
                identity = o != null && o != DBNull.Value && Convert.ToInt32(o) == 1;
            }

            string sql = identity
                ? "INSERT INTO dbo." + table + " (" + columns + ") VALUES (" + values + "); "
                  + "SELECT CAST(SCOPE_IDENTITY() AS int);"
                : "DECLARE @newId int; "
                  + "SELECT @newId = ISNULL(MAX(" + idCol + "), 0) + 1 FROM dbo." + table + " WITH (UPDLOCK, HOLDLOCK); "
                  + "INSERT INTO dbo." + table + " (" + idCol + ", " + columns + ") VALUES (@newId, " + values + "); "
                  + "SELECT @newId;";

            using (var cmd = new SqlCommand(sql, con, tx))
            {
                addParams(cmd);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        // Writes the real database error to the Visual Studio Output window (Debug)
        // and returns a friendly message to show the user instead.
        public static string Friendly(string userMessage, Exception ex)
        {
            Trace.TraceError(userMessage + " " + ex);
            return userMessage + " Please try again, or contact the school if it keeps happening.";
        }
    }
}
