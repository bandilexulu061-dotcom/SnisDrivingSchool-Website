using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Text.RegularExpressions;
using System.Web.Mvc;

namespace SnisDrivingSchool
{
    // One cancellation request waiting for the manager / reception
    public class CancelRequestRow
    {
        public int BookingId { get; set; }
        public string Student { get; set; }
        public string Package { get; set; }
        public string DateText { get; set; }
        public string Time { get; set; }
        public string Instructor { get; set; }
        public string Reason { get; set; }
        public DateTime RequestedOn { get; set; }
        public bool Paid { get; set; }
    }

    // Instructor can't take a lesson:
    //   1. The instructor asks to cancel (a reason is required). The lesson stays theirs and shows "Cancel Requested".
    //   2. The manager or a receptionist ACCEPTS or DECLINES the request.
    //   3. On ACCEPT the system automatically gives the lesson to another approved instructor who is
    //      not on leave and is free at the same time on every lesson day. If nobody is free, the request stays
    //      open (staff can decline it, or cancel the lesson and refund the student).
    // When leave is recorded for an instructor, Offer() still proposes a new instructor to the student (unchanged).
    public static class Replacement
    {
        private static bool _checked;

        public static void EnsureColumns(SqlConnection con)
        {
            if (_checked) return;
            Cancellation.EnsureColumns(con);
            InstructorLeave.EnsureTable(con);
            LessonPlan.EnsureColumns(con);
            using (var cmd = new SqlCommand(
                @"IF COL_LENGTH('dbo.BOOKING11', 'ProposedInstructorID') IS NULL
                      ALTER TABLE dbo.BOOKING11 ADD ProposedInstructorID INT NULL, ReplaceReason NVARCHAR(500) NULL,
                          ReplacedFrom INT NULL, ReplaceOn DATETIME NULL;
                  IF COL_LENGTH('dbo.BOOKING11', 'CancelRequestState') IS NULL
                      ALTER TABLE dbo.BOOKING11 ADD CancelRequestState VARCHAR(20) NULL, CancelRequestReason NVARCHAR(500) NULL,
                          CancelRequestedOn DATETIME NULL, CancelRequestNote NVARCHAR(300) NULL, CancelDecidedBy INT NULL;", con))
            {
                cmd.ExecuteNonQuery();
            }
            _checked = true;
        }

        // SQL: instructor "{0}" is busy at time slot @t on any day from @d to @e (a lesson, or an open proposal),
        // not counting the booking being handed over (@b). Use inside WHERE NOT EXISTS (...).
        public const string BusySql =
            @"SELECT 1 FROM dbo.BOOKING11 x
              WHERE (x.InstructorID = {0} OR x.ProposedInstructorID = {0}) AND x.TSID = @t AND x.BookingID <> @b
                AND x.BookingDate < DATEADD(DAY, 1, @e) AND ISNULL(x.EndDate, CAST(x.BookingDate AS date)) >= @d
                AND ISNULL(x.BookingStatus, '') <> 'Cancelled'";

        // "On leave" is not a reason to cancel: leave is recorded by the manager/reception instead
        public static string LeaveReasonError(string reason)
        {
            if (Regex.IsMatch(reason ?? "", @"\b(on|annual|sick|study|family|maternity|paternity)\s+leave\b|\bholiday\b|\bvacation\b", RegexOptions.IgnoreCase))
                return "\"On leave\" is not a reason to cancel a lesson. Ask the manager or reception to record your leave: "
                     + "you then can't be booked on those days, and lessons you already have are handed to another instructor.";
            return null;
        }

        // ---------- finding another instructor ----------

        // Approved, not the same instructor, not on leave on those days, free at that time.
        // Instructors whose licence code matches the lesson's code come first. Returns 0 when nobody is free.
        private static int FindFreeInstructor(SqlConnection con, SqlTransaction tx, int bookingId, int current,
                                              int tsId, DateTime date, DateTime end, string code, out string name)
        {
            name = null;
            using (var cmd = new SqlCommand(
                @"SELECT TOP 1 i.InstructorID, i.Name + ' ' + i.Surname AS FullName
                  FROM dbo.INSTRUCTORNEW i
                  WHERE i.[Status] = 'Approved' AND i.InstructorID <> @cur
                    AND NOT " + string.Format(InstructorLeave.OnLeaveSql, "i.InstructorID") + @"
                    AND NOT EXISTS (" + string.Format(BusySql, "i.InstructorID") + @")
                  ORDER BY CASE WHEN @code <> '' AND ISNULL(i.LicenseCode, '') LIKE '%' + REPLACE(@code, 'Code ', '') + '%' THEN 0 ELSE 1 END,
                           (SELECT COUNT(*) FROM dbo.BOOKING11 y WHERE y.InstructorID = i.InstructorID
                              AND CAST(y.BookingDate AS date) = @d AND ISNULL(y.BookingStatus, '') <> 'Cancelled'),
                           i.InstructorID", con, tx))
            {
                cmd.Parameters.AddWithValue("@cur", current);
                cmd.Parameters.AddWithValue("@b", bookingId);
                cmd.Parameters.AddWithValue("@e", end.Date);
                cmd.Parameters.AddWithValue("@d", date.Date);
                cmd.Parameters.AddWithValue("@t", tsId);
                cmd.Parameters.AddWithValue("@code", code ?? "");
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        name = Convert.ToString(r["FullName"]).Trim();
                        return Convert.ToInt32(r["InstructorID"]);
                    }
                }
            }
            return 0;
        }

        private static string WhenText(DateTime date, DateTime end)
        {
            return end > date ? "from " + date.ToString("d MMMM") + " to " + end.ToString("d MMMM") : "on " + date.ToString("d MMMM");
        }

        // ---------- leave: propose a new instructor to the student (unchanged behaviour) ----------

        // Finds a free instructor for this booking and proposes them to the student.
        // Returns true when someone was proposed; otherwise false (the booking is not changed).
        public static bool Offer(int bookingId, string reason, out string message)
        {
            message = null;
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureColumns(con);
                using (var tx = con.BeginTransaction())
                {
                    int current = 0, tsId = 0;
                    DateTime date = DateTime.MinValue, end = DateTime.MinValue;
                    string code = "";
                    using (var cmd = new SqlCommand(
                        @"SELECT ISNULL(InstructorID, 0) AS I, TSID, BookingDate, ISNULL(EndDate, CAST(BookingDate AS date)) AS EndDate, ISNULL(LicenceCode, '') AS Code
                          FROM dbo.BOOKING11 WITH (UPDLOCK) WHERE BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) { message = "Booking not found."; return false; }
                            current = Convert.ToInt32(r["I"]);
                            tsId = Convert.ToInt32(r["TSID"]);
                            date = Convert.ToDateTime(r["BookingDate"]);
                            end = Convert.ToDateTime(r["EndDate"]);
                            code = Convert.ToString(r["Code"]);
                        }
                    }

                    string newName;
                    int newId = FindFreeInstructor(con, tx, bookingId, current, tsId, date, end, code, out newName);
                    if (newId == 0)
                    {
                        message = "No other instructor is free at that time " + WhenText(date, end) + ".";
                        return false;
                    }

                    using (var cmd = new SqlCommand(
                        @"UPDATE dbo.BOOKING11 SET ProposedInstructorID = @n, ReplaceReason = @why, ReplacedFrom = @cur, ReplaceOn = GETDATE()
                          WHERE BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@n", newId);
                        cmd.Parameters.AddWithValue("@why", (reason ?? "").Length > 500 ? reason.Substring(0, 500) : (reason ?? ""));
                        cmd.Parameters.AddWithValue("@cur", current);
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    message = newName + " has been proposed to the student for " + date.ToString("d MMMM") + ". The student must accept the new instructor.";
                    return true;
                }
            }
        }

        // ---------- 1. the instructor ASKS to cancel ----------

        // The lesson is NOT cancelled and NOT handed over yet: it becomes a request for the manager / reception.
        public static string InstructorCancels(int bookingId, int instructorId, string reason, out string done)
        {
            done = null;
            reason = (reason ?? "").Trim();
            if (reason.Length < Cancellation.MinReason)
                return "Please give a reason for cancelling (at least " + Cancellation.MinReason + " characters).";
            if (reason.Length > Cancellation.MaxReason)
                return "The reason is too long (" + Cancellation.MaxReason + " characters at most).";
            string leave = LeaveReasonError(reason);
            if (leave != null) return leave;

            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureColumns(con);

                DateTime date;
                using (var cmd = new SqlCommand(
                    @"SELECT ISNULL(InstructorID, 0), LTRIM(RTRIM(ISNULL(BookingStatus, ''))), BookingDate, ProposedInstructorID,
                             ISNULL(CancelRequestState, '')
                      FROM dbo.BOOKING11 WHERE BookingID = @b", con))
                {
                    cmd.Parameters.AddWithValue("@b", bookingId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return "Booking " + bookingId + " was not found.";
                        if (Convert.ToInt32(r[0]) != instructorId) return "You can only cancel bookings that are assigned to you.";
                        string st = Convert.ToString(r[1]);
                        if (st == "Cancelled") return "This booking is already cancelled.";
                        if (st == "Completed") return "A completed lesson can't be cancelled.";
                        date = Convert.ToDateTime(r[2]);
                        if (date.Date < DateTime.Today) return "Past bookings can't be cancelled.";
                        if (r[3] != DBNull.Value) return "This lesson is already waiting for the student to accept another instructor.";
                        if (Convert.ToString(r[4]) == "Pending") return "You have already asked to cancel this lesson. Please wait for the manager or reception to answer.";
                    }
                }

                using (var cmd = new SqlCommand(
                    @"UPDATE dbo.BOOKING11
                      SET CancelRequestState = 'Pending', CancelRequestReason = @why, CancelRequestedOn = GETDATE(),
                          CancelRequestNote = NULL, CancelDecidedBy = NULL
                      WHERE BookingID = @b", con))
                {
                    cmd.Parameters.AddWithValue("@why", reason);
                    cmd.Parameters.AddWithValue("@b", bookingId);
                    cmd.ExecuteNonQuery();
                }
                done = "Your request to cancel lesson " + bookingId + " on " + date.ToString("d MMMM")
                     + " has been sent. The manager or reception will answer it. Until then the lesson is still yours.";
                return null;
            }
        }

        // ---------- 2. the manager / receptionist answers ----------

        // All requests waiting for an answer (oldest first)
        public static List<CancelRequestRow> OpenRequests()
        {
            var list = new List<CancelRequestRow>();
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureColumns(con);
                using (var cmd = new SqlCommand(
                    @"SELECT b.BookingID, b.BookingDate, ISNULL(b.EndDate, CAST(b.BookingDate AS date)) AS EndDate,
                             CONVERT(varchar(5), t.StartTime, 108) AS StartTime, p.PackageName,
                             LTRIM(RTRIM(ISNULL(s.StudentFirstName, '') + ' ' + ISNULL(s.StudentLastName, ''))) AS Student,
                             LTRIM(RTRIM(ISNULL(i.Name, '') + ' ' + ISNULL(i.Surname, ''))) AS Instructor,
                             b.CancelRequestReason, b.CancelRequestedOn,
                             CASE WHEN EXISTS (SELECT 1 FROM dbo.PAYMENT11 x WHERE x.BookingID = b.BookingID) THEN 1 ELSE 0 END AS Paid
                      FROM dbo.BOOKING11 b
                      LEFT JOIN dbo.Student1 s      ON s.StudentID    = b.StudentID
                      LEFT JOIN dbo.PACKAGEN p      ON p.PackageID    = b.PackageID
                      LEFT JOIN dbo.TimeSlotNew t   ON t.TSID         = b.TSID
                      LEFT JOIN dbo.INSTRUCTORNEW i ON i.InstructorID = b.InstructorID
                      WHERE b.CancelRequestState = 'Pending' AND ISNULL(b.BookingStatus, '') NOT IN ('Cancelled', 'Completed')
                      ORDER BY b.CancelRequestedOn", con))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        DateTime date = Convert.ToDateTime(r["BookingDate"]);
                        DateTime end = Convert.ToDateTime(r["EndDate"]);
                        list.Add(new CancelRequestRow
                        {
                            BookingId = Convert.ToInt32(r["BookingID"]),
                            Student = Convert.ToString(r["Student"]),
                            Package = r["PackageName"] == DBNull.Value ? "" : Convert.ToString(r["PackageName"]).Trim(),
                            DateText = LessonPlan.RangeText(date, end),
                            Time = r["StartTime"] == DBNull.Value ? "" : Convert.ToString(r["StartTime"]),
                            Instructor = Convert.ToString(r["Instructor"]),
                            Reason = r["CancelRequestReason"] == DBNull.Value ? "" : Convert.ToString(r["CancelRequestReason"]),
                            RequestedOn = r["CancelRequestedOn"] == DBNull.Value ? DateTime.Now : Convert.ToDateTime(r["CancelRequestedOn"]),
                            Paid = Convert.ToInt32(r["Paid"]) == 1
                        });
                    }
                }
            }
            return list;
        }

        // How many cancellation requests are waiting (0 if the database can't be reached). Used by the dashboards.
        public static int OpenRequestCount()
        {
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    EnsureColumns(con);
                    using (var cmd = new SqlCommand(
                        @"SELECT COUNT(*) FROM dbo.BOOKING11
                          WHERE CancelRequestState = 'Pending' AND ISNULL(BookingStatus, '') NOT IN ('Cancelled', 'Completed')", con))
                    {
                        return Convert.ToInt32(cmd.ExecuteScalar());
                    }
                }
            }
            catch (SqlException) { return 0; }
        }

        // ACCEPT: the instructor is released and another free instructor is assigned automatically.
        // Returns an error (and changes nothing) when nobody else is free.
        public static string AcceptRequest(int bookingId, int staffId, out string done)
        {
            done = null;
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureColumns(con);
                using (var tx = con.BeginTransaction())
                {
                    int current = 0, tsId = 0;
                    DateTime date = DateTime.MinValue, end = DateTime.MinValue;
                    string code = "", state = "", status = "", why = "";
                    using (var cmd = new SqlCommand(
                        @"SELECT ISNULL(InstructorID, 0) AS I, TSID, BookingDate, ISNULL(EndDate, CAST(BookingDate AS date)) AS EndDate,
                                 ISNULL(LicenceCode, '') AS Code, ISNULL(CancelRequestState, '') AS St,
                                 LTRIM(RTRIM(ISNULL(BookingStatus, ''))) AS Bs, ISNULL(CancelRequestReason, '') AS Why
                          FROM dbo.BOOKING11 WITH (UPDLOCK) WHERE BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) return "Booking " + bookingId + " was not found.";
                            current = Convert.ToInt32(r["I"]);
                            tsId = Convert.ToInt32(r["TSID"]);
                            date = Convert.ToDateTime(r["BookingDate"]);
                            end = Convert.ToDateTime(r["EndDate"]);
                            code = Convert.ToString(r["Code"]);
                            state = Convert.ToString(r["St"]);
                            status = Convert.ToString(r["Bs"]);
                            why = Convert.ToString(r["Why"]);
                        }
                    }
                    if (state != "Pending") return "This request was already answered.";
                    if (status == "Cancelled" || status == "Completed") return "This lesson is already " + status.ToLowerInvariant() + ".";

                    string newName;
                    int newId = FindFreeInstructor(con, tx, bookingId, current, tsId, date, end, code, out newName);
                    if (newId == 0)
                        return "No other approved instructor is free at that time " + WhenText(date, end)
                             + ". The request is still open: you can decline it, or cancel the lesson and refund the student.";

                    string replaceReason = "Your instructor cancelled: " + why;
                    if (replaceReason.Length > 500) replaceReason = replaceReason.Substring(0, 500);
                    using (var cmd = new SqlCommand(
                        @"UPDATE dbo.BOOKING11
                          SET InstructorID = @n, ProposedInstructorID = NULL, ReplacedFrom = @cur, ReplaceReason = @why, ReplaceOn = GETDATE(),
                              CancelRequestState = 'Accepted', CancelDecidedBy = @staff
                          WHERE BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@n", newId);
                        cmd.Parameters.AddWithValue("@cur", current);
                        cmd.Parameters.AddWithValue("@why", replaceReason);
                        cmd.Parameters.AddWithValue("@staff", staffId);
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    done = "Request accepted. " + newName + " now takes lesson " + bookingId + " (" + LessonPlan.RangeText(date, end) + ").";
                    return null;
                }
            }
        }

        // DECLINE: the lesson stays with the same instructor
        public static string DeclineRequest(int bookingId, int staffId, string note, out string done)
        {
            done = null;
            note = (note ?? "").Trim();
            if (note.Length < 5) return "Please give a short reason for declining (at least 5 characters).";
            if (note.Length > 300) return "The reason is too long (300 characters at most).";

            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureColumns(con);
                using (var cmd = new SqlCommand(
                    @"UPDATE dbo.BOOKING11 SET CancelRequestState = 'Declined', CancelRequestNote = @n, CancelDecidedBy = @s
                      WHERE BookingID = @b AND CancelRequestState = 'Pending'", con))
                {
                    cmd.Parameters.AddWithValue("@n", note);
                    cmd.Parameters.AddWithValue("@s", staffId);
                    cmd.Parameters.AddWithValue("@b", bookingId);
                    if (cmd.ExecuteNonQuery() == 0) return "This request was already answered.";
                }
            }
            done = "Request declined. Lesson " + bookingId + " stays with the same instructor.";
            return null;
        }

        // CANCEL THE LESSON instead (used when nobody else is free): refunds the student if they paid
        public static string CancelRequestedLesson(int bookingId, int staffId, out string done)
        {
            done = null;
            int instructorId = 0;
            string state = "", why = "";
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureColumns(con);
                using (var cmd = new SqlCommand(
                    @"SELECT ISNULL(InstructorID, 0), ISNULL(CancelRequestState, ''), ISNULL(CancelRequestReason, '')
                      FROM dbo.BOOKING11 WHERE BookingID = @b", con))
                {
                    cmd.Parameters.AddWithValue("@b", bookingId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return "Booking " + bookingId + " was not found.";
                        instructorId = Convert.ToInt32(r[0]);
                        state = Convert.ToString(r[1]);
                        why = Convert.ToString(r[2]);
                    }
                }
            }
            if (state != "Pending") return "This request was already answered.";

            string reason = why + " (Request accepted by the manager/reception and the lesson was cancelled.)";
            if (reason.Length > Cancellation.MaxReason) reason = reason.Substring(0, Cancellation.MaxReason);
            string error = Cancellation.Cancel(bookingId, Cancellation.ByInstructor, instructorId, reason, out done);
            if (error != null) return error;

            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(
                    "UPDATE dbo.BOOKING11 SET CancelRequestState = 'Accepted', CancelDecidedBy = @s WHERE BookingID = @b", con))
                {
                    cmd.Parameters.AddWithValue("@s", staffId);
                    cmd.Parameters.AddWithValue("@b", bookingId);
                    cmd.ExecuteNonQuery();
                }
            }
            return null;
        }

        // ---------- the student answers a proposed instructor (used when leave is recorded) ----------

        // The student accepts the proposed instructor: the lesson moves to them
        public static string Accept(int bookingId, int studentId, out string done)
        {
            done = null;
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureColumns(con);
                using (var tx = con.BeginTransaction())
                {
                    int student = 0, proposed = 0;
                    string name = null;
                    using (var cmd = new SqlCommand(
                        @"SELECT ISNULL(b.StudentID, 0) AS S, ISNULL(b.ProposedInstructorID, 0) AS P, i.Name + ' ' + i.Surname AS N
                          FROM dbo.BOOKING11 b WITH (UPDLOCK)
                          LEFT JOIN dbo.INSTRUCTORNEW i ON i.InstructorID = b.ProposedInstructorID
                          WHERE b.BookingID = @b AND ISNULL(b.BookingStatus, '') <> 'Cancelled'", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                student = Convert.ToInt32(r["S"]);
                                proposed = Convert.ToInt32(r["P"]);
                                name = Convert.ToString(r["N"]).Trim();
                            }
                        }
                    }
                    if (student != studentId) return "You can only answer for your own bookings.";
                    if (proposed == 0) return "There is no new instructor to accept for this booking.";

                    using (var cmd = new SqlCommand(
                        "UPDATE dbo.BOOKING11 SET InstructorID = ProposedInstructorID, ProposedInstructorID = NULL WHERE BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    done = "Thank you! " + name + " is now your instructor for booking #" + bookingId + ".";
                    return null;
                }
            }
        }

        // The student declines: the lesson is cancelled (refunded if paid), recorded as cancelled by the instructor
        public static string Decline(int bookingId, int studentId, out string done)
        {
            done = null;
            int student = 0, original = 0;
            string why = "";
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureColumns(con);
                using (var cmd = new SqlCommand(
                    "SELECT ISNULL(StudentID, 0), ISNULL(InstructorID, 0), ISNULL(ReplaceReason, ''), ProposedInstructorID FROM dbo.BOOKING11 WHERE BookingID = @b", con))
                {
                    cmd.Parameters.AddWithValue("@b", bookingId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return "Booking not found.";
                        student = Convert.ToInt32(r[0]);
                        original = Convert.ToInt32(r[1]);
                        why = Convert.ToString(r[2]);
                        if (r[3] == DBNull.Value) return "There is no new instructor to decline for this booking.";
                    }
                }
                if (student != studentId) return "You can only answer for your own bookings.";
                using (var cmd = new SqlCommand("UPDATE dbo.BOOKING11 SET ProposedInstructorID = NULL WHERE BookingID = @b", con))
                {
                    cmd.Parameters.AddWithValue("@b", bookingId);
                    cmd.ExecuteNonQuery();
                }
            }
            string reason = (why == "" ? "The instructor could not take the lesson." : why) + " (The student declined the new instructor.)";
            if (reason.Length > Cancellation.MaxReason) reason = reason.Substring(0, Cancellation.MaxReason);
            string error = Cancellation.Cancel(bookingId, Cancellation.ByInstructor, original, reason, out done);
            return error;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Manager and receptionist pages for the cancellation requests (same page, same rules for both).
// Both controllers are "partial" classes, so these actions simply join the existing ones.
// ---------------------------------------------------------------------------------------------
namespace SnisDrivingSchool.Controllers
{
    public partial class ManagerController
    {
        public ActionResult CancelRequests()
        {
            var list = new List<CancelRequestRow>();
            try { list = Replacement.OpenRequests(); }
            catch (SqlException ex) { ViewBag.Error = Db.Friendly("Could not load the cancellation requests.", ex); }
            ViewBag.Ctl = "Manager";
            return View("~/Views/Shared/CancelRequests.cshtml", list);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AcceptCancel(int id)
        {
            string done, error;
            try { error = Replacement.AcceptRequest(id, Convert.ToInt32(Session["UserId"]), out done); }
            catch (SqlException ex) { error = Db.Friendly("Could not accept the request.", ex); done = null; }
            if (error != null) TempData["Error"] = error; else TempData["Success"] = done;
            return RedirectToAction("CancelRequests");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DeclineCancel(int id, string note)
        {
            string done, error;
            try { error = Replacement.DeclineRequest(id, Convert.ToInt32(Session["UserId"]), note, out done); }
            catch (SqlException ex) { error = Db.Friendly("Could not decline the request.", ex); done = null; }
            if (error != null) TempData["Error"] = error; else TempData["Success"] = done;
            return RedirectToAction("CancelRequests");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult CancelLesson(int id)
        {
            string done, error;
            try { error = Replacement.CancelRequestedLesson(id, Convert.ToInt32(Session["UserId"]), out done); }
            catch (SqlException ex) { error = Db.Friendly("Could not cancel the lesson.", ex); done = null; }
            if (error != null) TempData["Error"] = error; else TempData["Success"] = done;
            return RedirectToAction("CancelRequests");
        }
    }

    public partial class ReceptionistController
    {
        public ActionResult CancelRequests()
        {
            var list = new List<CancelRequestRow>();
            try { list = Replacement.OpenRequests(); }
            catch (SqlException ex) { ViewBag.Error = Db.Friendly("Could not load the cancellation requests.", ex); }
            ViewBag.Ctl = "Receptionist";
            return View("~/Views/Shared/CancelRequests.cshtml", list);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AcceptCancel(int id)
        {
            string done, error;
            try { error = Replacement.AcceptRequest(id, Convert.ToInt32(Session["UserId"]), out done); }
            catch (SqlException ex) { error = Db.Friendly("Could not accept the request.", ex); done = null; }
            if (error != null) TempData["Error"] = error; else TempData["Success"] = done;
            return RedirectToAction("CancelRequests");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DeclineCancel(int id, string note)
        {
            string done, error;
            try { error = Replacement.DeclineRequest(id, Convert.ToInt32(Session["UserId"]), note, out done); }
            catch (SqlException ex) { error = Db.Friendly("Could not decline the request.", ex); done = null; }
            if (error != null) TempData["Error"] = error; else TempData["Success"] = done;
            return RedirectToAction("CancelRequests");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult CancelLesson(int id)
        {
            string done, error;
            try { error = Replacement.CancelRequestedLesson(id, Convert.ToInt32(Session["UserId"]), out done); }
            catch (SqlException ex) { error = Db.Friendly("Could not cancel the lesson.", ex); done = null; }
            if (error != null) TempData["Error"] = error; else TempData["Success"] = done;
            return RedirectToAction("CancelRequests");
        }
    }
}