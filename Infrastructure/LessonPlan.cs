using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Text.RegularExpressions;

namespace SnisDrivingSchool
{
    // A package has a number of lessons (dbo.PACKAGEN.NumberOfLessons). One booking covers that many lessons:
    // one lesson a day at the same time slot, starting on the booking date and skipping Sundays.
    // Example: 5 lessons from Thursday 5 November -> 5, 6, 7, 9 and 10 November (8 November is a Sunday).
    // The last lesson day is saved in dbo.BOOKING11.EndDate, so clash and leave checks cover the whole period.
    public static class LessonPlan
    {
        private static bool _checked;

        public static void EnsureColumns(SqlConnection con)
        {
            if (_checked) return;
            using (var cmd = new SqlCommand(
                @"IF COL_LENGTH('dbo.PACKAGEN', 'NumberOfLessons') IS NULL ALTER TABLE dbo.PACKAGEN ADD NumberOfLessons INT NULL;
                  IF COL_LENGTH('dbo.BOOKING11', 'EndDate') IS NULL ALTER TABLE dbo.BOOKING11 ADD EndDate DATE NULL;", con))
            {
                cmd.ExecuteNonQuery();
            }
            _checked = true;
        }

        // Number of lessons: NumberOfLessons when it is filled in, otherwise a number in the package name
        // ("5 Lesson Package", "Beginner - 10 lessons"), otherwise the minimum.
        // Every booking is at least MinLessons lessons.
        public const int MinLessons = 3;

        public static int Count(int? numberOfLessons, string packageName)
        {
            if (numberOfLessons.HasValue && numberOfLessons.Value > 0) return Math.Max(MinLessons, Math.Min(numberOfLessons.Value, 40));
            var m = Regex.Match(packageName ?? "", @"(\d{1,2})\s*-?\s*(lesson|les\b|x\b)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value);
                if (n >= 1 && n <= 40) return Math.Max(MinLessons, n);
            }
            return MinLessons;
        }

        // Lesson count for every package (for the booking page)
        public static Dictionary<int, int> PackageCounts(SqlConnection con)
        {
            EnsureColumns(con);
            var map = new Dictionary<int, int>();
            using (var cmd = new SqlCommand("SELECT PackageID, PackageName, NumberOfLessons FROM dbo.PACKAGEN", con))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    map[Convert.ToInt32(r["PackageID"])] = Count(r["NumberOfLessons"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["NumberOfLessons"]),
                                                                 Convert.ToString(r["PackageName"]));
            }
            return map;
        }

        public static int CountFor(SqlConnection con, SqlTransaction tx, int packageId)
        {
            EnsureColumns(con);
            using (var cmd = new SqlCommand("SELECT PackageName, NumberOfLessons FROM dbo.PACKAGEN WHERE PackageID = @p", con, tx))
            {
                cmd.Parameters.AddWithValue("@p", packageId);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return MinLessons;
                    return Count(r["NumberOfLessons"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["NumberOfLessons"]), Convert.ToString(r["PackageName"]));
                }
            }
        }

        // The lesson days: one a day from the start date, skipping Sundays
        public static List<DateTime> Days(DateTime start, int count)
        {
            var days = new List<DateTime>();
            var d = start.Date;
            while (days.Count < Math.Max(1, count))
            {
                if (d.DayOfWeek != DayOfWeek.Sunday) days.Add(d);
                d = d.AddDays(1);
            }
            return days;
        }

        public static DateTime End(DateTime start, int count)
        {
            var days = Days(start, count);
            return days[days.Count - 1];
        }

        // SQL: booking "x" overlaps the period @d (first day) to @e (last day)
        public const string OverlapSql = "x.BookingDate < DATEADD(DAY, 1, @e) AND ISNULL(x.EndDate, CAST(x.BookingDate AS date)) >= @d";

        public static void SetEnd(SqlConnection con, SqlTransaction tx, int bookingId, DateTime end)
        {
            using (var cmd = new SqlCommand("UPDATE dbo.BOOKING11 SET EndDate = @e WHERE BookingID = @b", con, tx))
            {
                cmd.Parameters.AddWithValue("@e", end.Date);
                cmd.Parameters.AddWithValue("@b", bookingId);
                cmd.ExecuteNonQuery();
            }
        }

        // Short text for tables: "Thu 5 Nov" or "Thu 5 Nov - Tue 10 Nov 2026"
        public static string RangeText(DateTime start, DateTime? end, bool withYear = true)
        {
            string fmt = withYear ? "ddd d MMM yyyy" : "ddd d MMM";
            if (!end.HasValue || end.Value.Date <= start.Date) return start.ToString(fmt);
            return start.ToString("ddd d MMM") + " - " + end.Value.ToString(fmt);
        }

        // Number of lesson days between start and end (Sundays skipped)
        public static int LessonsBetween(DateTime start, DateTime? end)
        {
            if (!end.HasValue || end.Value.Date <= start.Date) return 1;
            int n = 0;
            for (var d = start.Date; d <= end.Value.Date; d = d.AddDays(1)) if (d.DayOfWeek != DayOfWeek.Sunday) n++;
            return n;
        }
    }
}
