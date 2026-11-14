using System;
using System.Collections.Generic;
using System.Data.SqlClient;

namespace SnisDrivingSchool
{
    // Instructor leave (holiday, sick leave, training ...). The manager or reception records it.
    // An instructor on leave can't be chosen for a lesson on those days, and lessons they already
    // had on those days are offered to another free instructor (see Replacement.cs).
    public static class InstructorLeave
    {
        public class Period
        {
            public int LeaveId { get; set; }
            public int InstructorId { get; set; }
            public DateTime From { get; set; }
            public DateTime To { get; set; }
            public string Note { get; set; }
            public string Text { get { return From == To ? From.ToString("d MMM") : From.ToString("d MMM") + " - " + To.ToString("d MMM"); } }
        }

        private static bool _checked;

        public static void EnsureTable(SqlConnection con)
        {
            if (_checked) return;
            using (var cmd = new SqlCommand(
                @"IF OBJECT_ID('dbo.InstructorLeave', 'U') IS NULL
                  CREATE TABLE dbo.InstructorLeave(
                      LeaveID       INT IDENTITY(1,1) PRIMARY KEY,
                      InstructorID  INT           NOT NULL,
                      FromDate      DATE          NOT NULL,
                      ToDate        DATE          NOT NULL,
                      Note          NVARCHAR(200) NULL,
                      CreatedOn     DATETIME      NOT NULL DEFAULT GETDATE()
                  );", con))
            {
                cmd.ExecuteNonQuery();
            }
            _checked = true;
        }

        // SQL condition: instructor "{0}" is on leave on any day from @d to @e (use inside WHERE)
        public const string OnLeaveSql =
            "EXISTS (SELECT 1 FROM dbo.InstructorLeave lv WHERE lv.InstructorID = {0} AND lv.FromDate <= @e AND lv.ToDate >= @d)";

        public static bool IsOnLeave(SqlConnection con, SqlTransaction tx, int instructorId, DateTime date)
        {
            return IsOnLeave(con, tx, instructorId, date, date);
        }

        // On leave on any day from "from" to "to"?
        public static bool IsOnLeave(SqlConnection con, SqlTransaction tx, int instructorId, DateTime from, DateTime to)
        {
            EnsureTable(con);
            using (var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.InstructorLeave WHERE InstructorID = @i AND FromDate <= @e AND ToDate >= @d", con, tx))
            {
                cmd.Parameters.AddWithValue("@i", instructorId);
                cmd.Parameters.AddWithValue("@d", from.Date);
                cmd.Parameters.AddWithValue("@e", to.Date);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        // Leave that is still current or coming up, for every instructor
        public static Dictionary<int, List<Period>> Upcoming(SqlConnection con)
        {
            EnsureTable(con);
            var map = new Dictionary<int, List<Period>>();
            using (var cmd = new SqlCommand(
                "SELECT LeaveID, InstructorID, FromDate, ToDate, Note FROM dbo.InstructorLeave WHERE ToDate >= CAST(GETDATE() AS date) ORDER BY FromDate", con))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var p = new Period
                    {
                        LeaveId = Convert.ToInt32(r["LeaveID"]),
                        InstructorId = Convert.ToInt32(r["InstructorID"]),
                        From = Convert.ToDateTime(r["FromDate"]),
                        To = Convert.ToDateTime(r["ToDate"]),
                        Note = r["Note"] == DBNull.Value ? "" : Convert.ToString(r["Note"])
                    };
                    if (!map.ContainsKey(p.InstructorId)) map[p.InstructorId] = new List<Period>();
                    map[p.InstructorId].Add(p);
                }
            }
            return map;
        }

        // Adds "(on leave 3 Nov - 14 Nov)" to instructor names in drop-down lists
        public static void MarkInList(SqlConnection con, System.Collections.Generic.List<SnisDrivingSchool.Models.SelectItem> items)
        {
            var leave = Upcoming(con);
            foreach (var it in items)
            {
                List<Period> list;
                if (leave.TryGetValue(it.Id, out list) && list.Count > 0)
                    it.Text += " (on leave " + string.Join(", ", list.ConvertAll(p => p.Text)) + ")";
            }
        }

        // Records leave. Lessons the instructor already has on those days are handed to another free instructor
        // (the student is asked to accept). Returns an error, or null; "done" says what happened.
        public static string Add(int instructorId, DateTime from, DateTime to, string note, out string done)
        {
            done = null;
            note = (note ?? "").Trim();
            if (to < from) return "The last day of leave can't be before the first day.";
            if (from < DateTime.Today) return "Leave can't start in the past.";
            if ((to - from).TotalDays > 180) return "Leave can be at most 6 months at a time.";
            if (note.Length > 200) return "The note is too long (200 characters at most).";

            var affected = new List<int>();
            string name;
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureTable(con);
                LessonPlan.EnsureColumns(con);
                using (var cmd = new SqlCommand("SELECT Name + ' ' + Surname FROM dbo.INSTRUCTORNEW WHERE InstructorID = @i", con))
                {
                    cmd.Parameters.AddWithValue("@i", instructorId);
                    object o = cmd.ExecuteScalar();
                    if (o == null) return "That instructor could not be found.";
                    name = Convert.ToString(o).Trim();
                }
                using (var cmd = new SqlCommand(
                    "INSERT INTO dbo.InstructorLeave (InstructorID, FromDate, ToDate, Note) VALUES (@i, @f, @t, @n)", con))
                {
                    cmd.Parameters.AddWithValue("@i", instructorId);
                    cmd.Parameters.AddWithValue("@f", from.Date);
                    cmd.Parameters.AddWithValue("@t", to.Date);
                    cmd.Parameters.AddWithValue("@n", note == "" ? (object)DBNull.Value : note);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = new SqlCommand(
                    @"SELECT BookingID FROM dbo.BOOKING11
                      WHERE InstructorID = @i AND BookingDate < DATEADD(DAY, 1, @t) AND ISNULL(EndDate, CAST(BookingDate AS date)) >= @f
                        AND ISNULL(BookingStatus, '') NOT IN ('Cancelled', 'Completed')", con))
                {
                    cmd.Parameters.AddWithValue("@i", instructorId);
                    cmd.Parameters.AddWithValue("@f", from.Date);
                    cmd.Parameters.AddWithValue("@t", to.Date);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) affected.Add(Convert.ToInt32(r[0]));
                }
            }

            int offered = 0;
            var stuck = new List<int>();
            foreach (int bookingId in affected)
            {
                string result;
                if (Replacement.Offer(bookingId, "Your instructor " + name + " is on leave on that day.", out result)) offered++;
                else stuck.Add(bookingId);
            }

            done = "Leave saved for " + name + " (" + (from == to ? from.ToString("d MMM yyyy") : from.ToString("d MMM") + " - " + to.ToString("d MMM yyyy")) + ").";
            if (offered > 0) done += " " + offered + " lesson" + (offered == 1 ? " was" : "s were") + " offered to another instructor; the students will be asked to accept.";
            if (stuck.Count > 0) done += " No other instructor is free for booking" + (stuck.Count == 1 ? " #" : "s #") + string.Join(", #", stuck) + ": please contact " + (stuck.Count == 1 ? "that student." : "those students.");
            return null;
        }

        public static string Remove(int leaveId)
        {
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureTable(con);
                using (var cmd = new SqlCommand("DELETE FROM dbo.InstructorLeave WHERE LeaveID = @l", con))
                {
                    cmd.Parameters.AddWithValue("@l", leaveId);
                    return cmd.ExecuteNonQuery() == 0 ? "That leave could not be found." : null;
                }
            }
        }
    }
}
