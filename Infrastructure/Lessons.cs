using System;
using System.Collections.Generic;
using System.Data.SqlClient;

namespace SnisDrivingSchool
{
    // After the lesson:
    //  - the instructor marks a paid ("Booked") lesson as done on or after its date -> "Completed"
    //  - the student can then rate that lesson once (1 to 5 stars + an optional comment)
    // Reviews are kept in dbo.LessonReviews (created automatically the first time it is needed).
    public static class Lessons
    {
        public const int MaxComment = 500;

        // ---------- database ----------

        private static bool _checked;

        public static void EnsureTables(SqlConnection con)
        {
            if (_checked) return;
            using (var cmd = new SqlCommand(
                @"IF COL_LENGTH('dbo.BOOKING11', 'CompletedOn') IS NULL ALTER TABLE dbo.BOOKING11 ADD CompletedOn DATETIME NULL;
                  IF OBJECT_ID('dbo.LessonReviews', 'U') IS NULL
                  CREATE TABLE dbo.LessonReviews(
                      ReviewID      INT IDENTITY(1,1) PRIMARY KEY,
                      BookingID     INT            NOT NULL UNIQUE,
                      StudentID     INT            NOT NULL,
                      InstructorID  INT            NULL,
                      Rating        TINYINT        NOT NULL CHECK (Rating BETWEEN 1 AND 5),
                      Comment       NVARCHAR(500)  NULL,
                      CreatedOn     DATETIME       NOT NULL DEFAULT GETDATE()
                  );", con))
            {
                cmd.ExecuteNonQuery();
            }
            _checked = true;
        }

        // ---------- marking a lesson as done ----------

        // Can this lesson be marked as done now? Paid (Booked), not cancelled, and the lesson day has come.
        public static bool CanComplete(string status, DateTime lessonDate)
        {
            return string.Equals((status ?? "").Trim(), "Booked", StringComparison.OrdinalIgnoreCase)
                && lessonDate.Date <= DateTime.Today;
        }

        // The instructor marks one of their lessons as done. Returns an error, or null when it worked.
        public static string Complete(int bookingId, int instructorId, out string done)
        {
            done = null;
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureTables(con);
                Cancellation.EnsureColumns(con);
                using (var tx = con.BeginTransaction())
                {
                    int owner = 0;
                    string status = null;
                    DateTime date = DateTime.MinValue;
                    bool paid = false;
                    using (var cmd = new SqlCommand(
                        @"SELECT ISNULL(b.InstructorID, 0) AS InstructorID, b.BookingDate, LTRIM(RTRIM(ISNULL(b.BookingStatus, ''))) AS St,
                                 CASE WHEN EXISTS (SELECT 1 FROM dbo.PAYMENT11 p WHERE p.BookingID = b.BookingID) THEN 1 ELSE 0 END AS Paid
                          FROM dbo.BOOKING11 b WITH (UPDLOCK) WHERE b.BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                owner = Convert.ToInt32(r["InstructorID"]);
                                date = Convert.ToDateTime(r["BookingDate"]);
                                status = Convert.ToString(r["St"]);
                                paid = Convert.ToInt32(r["Paid"]) == 1;
                            }
                        }
                    }

                    if (status == null) return "Booking " + bookingId + " was not found.";
                    if (owner != instructorId) return "You can only mark your own lessons as done.";
                    if (status == "Completed") return "This lesson is already marked as done.";
                    if (status == "Cancelled") return "A cancelled lesson can't be marked as done.";
                    if (date.Date > DateTime.Today) return "You can only mark a lesson as done on or after the lesson day (" + date.ToString("d MMMM") + ").";
                    if (!paid) return "This lesson has not been paid yet, so it can't be marked as done. Reception must confirm the payment first.";

                    using (var cmd = new SqlCommand(
                        "UPDATE dbo.BOOKING11 SET BookingStatus = 'Completed', CompletedOn = GETDATE() WHERE BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    done = "Lesson " + bookingId + " on " + date.ToString("d MMMM yyyy") + " is marked as done. The student can now rate it.";
                    return null;
                }
            }
        }

        // ---------- reviews ----------

        // The student rates one of their completed lessons. Returns an error, or null when it worked.
        public static string SaveReview(int studentId, int bookingId, int rating, string comment)
        {
            comment = (comment ?? "").Trim();
            if (rating < 1 || rating > 5) return "Please choose a rating from 1 to 5 stars.";
            if (comment.Length > MaxComment) return "The comment is too long (" + MaxComment + " characters at most).";

            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureTables(con);
                using (var tx = con.BeginTransaction())
                {
                    int owner = 0, instructor = 0;
                    string status = null;
                    using (var cmd = new SqlCommand(
                        @"SELECT ISNULL(StudentID, 0) AS S, ISNULL(InstructorID, 0) AS I, LTRIM(RTRIM(ISNULL(BookingStatus, ''))) AS St
                          FROM dbo.BOOKING11 WHERE BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                owner = Convert.ToInt32(r["S"]);
                                instructor = Convert.ToInt32(r["I"]);
                                status = Convert.ToString(r["St"]);
                            }
                        }
                    }
                    if (status == null || owner != studentId) return "You can only rate your own lessons.";
                    if (status != "Completed") return "You can rate a lesson once your instructor has marked it as done.";

                    using (var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.LessonReviews WHERE BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return "You have already rated this lesson.";
                    }

                    using (var cmd = new SqlCommand(
                        @"INSERT INTO dbo.LessonReviews (BookingID, StudentID, InstructorID, Rating, Comment)
                          VALUES (@b, @s, @i, @r, @c)", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        cmd.Parameters.AddWithValue("@s", studentId);
                        cmd.Parameters.AddWithValue("@i", instructor > 0 ? (object)instructor : DBNull.Value);
                        cmd.Parameters.AddWithValue("@r", rating);
                        cmd.Parameters.AddWithValue("@c", comment == "" ? (object)DBNull.Value : comment);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    return null;
                }
            }
        }

        // Reviews, newest first. studentId > 0: one student's; instructorId > 0: one instructor's; both 0: all.
        public static List<LessonReview> LoadReviews(SqlConnection con, int studentId, int instructorId)
        {
            EnsureTables(con);
            var list = new List<LessonReview>();
            using (var cmd = new SqlCommand(
                @"SELECT rv.ReviewID, rv.BookingID, rv.Rating, rv.Comment, rv.CreatedOn, b.BookingDate,
                         p.PackageName,
                         LTRIM(RTRIM(ISNULL(s.StudentFirstName, '') + ' ' + ISNULL(s.StudentLastName, ''))) AS Student,
                         LTRIM(RTRIM(ISNULL(i.Name, '') + ' ' + ISNULL(i.Surname, ''))) AS Instructor
                  FROM dbo.LessonReviews rv
                  JOIN dbo.BOOKING11 b ON b.BookingID = rv.BookingID
                  LEFT JOIN dbo.PACKAGEN p      ON p.PackageID    = b.PackageID
                  LEFT JOIN dbo.Student1 s      ON s.StudentID    = rv.StudentID
                  LEFT JOIN dbo.INSTRUCTORNEW i ON i.InstructorID = rv.InstructorID
                  WHERE (@s = 0 OR rv.StudentID = @s) AND (@i = 0 OR rv.InstructorID = @i)
                  ORDER BY rv.CreatedOn DESC", con))
            {
                cmd.Parameters.AddWithValue("@s", studentId);
                cmd.Parameters.AddWithValue("@i", instructorId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new LessonReview
                        {
                            ReviewId = Convert.ToInt32(r["ReviewID"]),
                            BookingId = Convert.ToInt32(r["BookingID"]),
                            Rating = Convert.ToInt32(r["Rating"]),
                            Comment = r["Comment"] == DBNull.Value ? "" : Convert.ToString(r["Comment"]),
                            CreatedOn = Convert.ToDateTime(r["CreatedOn"]),
                            LessonDate = Convert.ToDateTime(r["BookingDate"]),
                            PackageName = Convert.ToString(r["PackageName"]),
                            Student = Convert.ToString(r["Student"]),
                            Instructor = Convert.ToString(r["Instructor"])
                        });
                    }
                }
            }
            return list;
        }

        // Average rating and number of reviews for every instructor that has reviews
        public static Dictionary<int, RatingSummary> Averages(SqlConnection con)
        {
            EnsureTables(con);
            var map = new Dictionary<int, RatingSummary>();
            using (var cmd = new SqlCommand(
                @"SELECT InstructorID, AVG(CAST(Rating AS DECIMAL(5,2))), COUNT(*)
                  FROM dbo.LessonReviews WHERE InstructorID IS NOT NULL GROUP BY InstructorID", con))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    map[Convert.ToInt32(r[0])] = new RatingSummary { Average = Convert.ToDecimal(r[1]), Count = Convert.ToInt32(r[2]) };
            }
            return map;
        }

        // "★★★★☆" for a whole-number rating
        public static string Stars(int rating)
        {
            rating = Math.Max(0, Math.Min(5, rating));
            return new string('★', rating) + new string('☆', 5 - rating);
        }
    }

    public class LessonReview
    {
        public int ReviewId { get; set; }
        public int BookingId { get; set; }
        public int Rating { get; set; }
        public string Comment { get; set; }
        public DateTime CreatedOn { get; set; }
        public DateTime LessonDate { get; set; }
        public string PackageName { get; set; }
        public string Student { get; set; }
        public string Instructor { get; set; }
    }

    public class RatingSummary
    {
        public decimal Average { get; set; }
        public int Count { get; set; }
        public string Text { get { return Count == 0 ? "No ratings yet" : Average.ToString("0.0") + " / 5 (" + Count + " review" + (Count == 1 ? "" : "s") + ")"; } }
    }
}
