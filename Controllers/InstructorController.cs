using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Web.Mvc;

namespace SnisDrivingSchool.Controllers
{
    // One row on the instructor's page
    public class InstructorBooking
    {
        public int BookingID { get; set; }
        public DateTime BookingDate { get; set; }
        public DateTime? EndDate { get; set; }      // last lesson day of the package
        public string DateRange { get { return SnisDrivingSchool.LessonPlan.RangeText(BookingDate, EndDate); } }
        public int LessonCount { get { return SnisDrivingSchool.LessonPlan.LessonsBetween(BookingDate, EndDate); } }
        public string Status { get; set; }          // Booked, Pending, Completed, Cancelled, Cancel Requested ...
        public string StudentName { get; set; }
        public string TimeText { get; set; }        // e.g. "08:00 - 09:00"
        public string PackageName { get; set; }
        public string LicenceCode { get; set; }     // e.g. "Code 8"
        public string PaymentStatus { get; set; }
        public string CancelledBy { get; set; }
        public string CancelReason { get; set; }
        public DateTime? CancelledOn { get; set; }
        public decimal RefundAmount { get; set; }
        public string HandedTo { get; set; }        // another instructor was proposed; waiting for the student
        public bool CancelRequested { get; set; }   // the instructor asked to cancel; waiting for the manager / reception

        public bool IsCancelled { get { return string.Equals(Status, "Cancelled", StringComparison.OrdinalIgnoreCase); } }
        public bool IsCompleted { get { return string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase); } }
        public bool CanCancel { get { return !IsCancelled && !IsCompleted && !CancelRequested && BookingDate.Date >= DateTime.Today && string.IsNullOrEmpty(HandedTo); } }

        // Paid lesson whose day has come: the instructor can mark it as done
        public bool CanComplete { get { return Lessons.CanComplete(Status, BookingDate); } }
    }

    // Only instructors can open these pages (see Infrastructure/RoleRequiredAttribute.cs)
    [RoleRequired("Instructor")]
    public class InstructorController : Controller
    {
        // GET: Instructor
        public ActionResult Index()
        {
            int instructorId = Convert.ToInt32(Session["UserId"]);
            var list = new List<InstructorBooking>();

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    Licence.EnsureColumns(con);
                    Cancellation.EnsureColumns(con);
                    Replacement.EnsureColumns(con);
                    LessonPlan.EnsureColumns(con);

                    const string sql = @"
                        SELECT b.BookingID, b.BookingDate, b.EndDate, LTRIM(RTRIM(ISNULL(b.BookingStatus, ''))) AS BookingStatus,
                               COALESCE(NULLIF(LTRIM(RTRIM(s.StudentFirstName + ' ' + s.StudentLastName)), ''),
                                        LTRIM(RTRIM(ISNULL(b.StudentFirstName, '') + ' ' + ISNULL(b.StudentLastName, '')))) AS StudentName,
                               CONVERT(varchar(5), t.StartTime, 108) AS S, CONVERT(varchar(5), t.EndTime, 108) AS E,
                               p.PackageName, b.LicenceCode,
                               LTRIM(RTRIM(ISNULL(b.PaymentStatus, ''))) AS PaymentStatus, pay.PaymentID,
                               b.CancelledBy, b.CancelReason, b.CancelledOn, ISNULL(b.RefundAmount, 0) AS RefundAmount,
                               ISNULL(b.CancelRequestState, '') AS CancelRequestState,
                               (SELECT pi.Name + ' ' + pi.Surname FROM dbo.INSTRUCTORNEW pi WHERE pi.InstructorID = b.ProposedInstructorID) AS HandedTo
                        FROM dbo.BOOKING11 b
                        LEFT JOIN dbo.Student1 s     ON s.StudentID = b.StudentID
                        LEFT JOIN dbo.TimeSlotNew t  ON t.TSID      = b.TSID
                        LEFT JOIN dbo.PACKAGEN p     ON p.PackageID = b.PackageID
                        OUTER APPLY (SELECT TOP 1 x.PaymentID FROM dbo.PAYMENT11 x WHERE x.BookingID = b.BookingID) pay
                        WHERE b.InstructorID = @i
                        ORDER BY b.BookingDate, t.StartTime";
                    using (var cmd = new SqlCommand(sql, con))
                    {
                        cmd.Parameters.AddWithValue("@i", instructorId);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string status = Convert.ToString(r["BookingStatus"]);
                                if (status == "") status = "Pending";
                                if (status == "Request Cancel") status = "Booked";   // old cancellation requests: still active
                                decimal refund = Convert.ToDecimal(r["RefundAmount"]);

                                string payment;
                                if (refund > 0) payment = "Refunded";
                                else if (r["PaymentID"] != DBNull.Value) payment = "Paid";
                                else if (status == "Cancelled") payment = "N/A";
                                else payment = "Not paid yet";

                                // A booking that isn't paid yet is never shown as "Booked"
                                if (payment == "Not paid yet" && status == "Booked") status = "Pending";

                                // The instructor asked to cancel and the manager / reception has not answered yet
                                bool requested = Convert.ToString(r["CancelRequestState"]) == "Pending"
                                                 && status != "Cancelled" && status != "Completed";
                                if (requested) status = "Cancel Requested";

                                string start = Convert.ToString(r["S"]), end = Convert.ToString(r["E"]);
                                list.Add(new InstructorBooking
                                {
                                    BookingID = Convert.ToInt32(r["BookingID"]),
                                    BookingDate = Convert.ToDateTime(r["BookingDate"]),
                                    EndDate = r["EndDate"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["EndDate"]),
                                    Status = status,
                                    StudentName = Convert.ToString(r["StudentName"]),
                                    TimeText = start == "" ? "-" : (end == "" ? start : start + " - " + end),
                                    PackageName = Convert.ToString(r["PackageName"]),
                                    LicenceCode = Convert.ToString(r["LicenceCode"]),
                                    PaymentStatus = payment,
                                    CancelledBy = Convert.ToString(r["CancelledBy"]),
                                    CancelReason = Convert.ToString(r["CancelReason"]),
                                    CancelledOn = r["CancelledOn"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["CancelledOn"]),
                                    RefundAmount = refund,
                                    HandedTo = r["HandedTo"] == DBNull.Value ? null : Convert.ToString(r["HandedTo"]).Trim(),
                                    CancelRequested = requested
                                });
                            }
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load your bookings.", ex);
            }

            // What students said about this instructor's lessons
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    var reviews = Lessons.LoadReviews(con, 0, instructorId);
                    ViewBag.Reviews = reviews;
                    ViewBag.Rating = new RatingSummary
                    {
                        Count = reviews.Count,
                        Average = reviews.Count == 0 ? 0m : (decimal)reviews.Average(x => x.Rating)
                    };
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load your reviews.", ex);
            }

            return View(list);
        }

        // POST: Instructor/Complete  (the instructor marks a paid lesson as done, on or after the lesson day)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Complete(int bookingId)
        {
            string done, error;
            try
            {
                error = Lessons.Complete(bookingId, Convert.ToInt32(Session["UserId"]), out done);
            }
            catch (SqlException ex)
            {
                error = Db.Friendly("Could not mark the lesson as done.", ex);
                done = null;
            }
            if (error != null) TempData["Error"] = error;
            else TempData["Success"] = done;
            return RedirectToAction("Index");
        }

        // POST: Instructor/Cancel  (the instructor ASKS to cancel; a reason is required.
        // The manager or reception must accept it, then another free instructor is assigned automatically.)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Cancel(int bookingId, string reason)
        {
            string done, error;
            try
            {
                error = Replacement.InstructorCancels(bookingId, Convert.ToInt32(Session["UserId"]), reason, out done);
            }
            catch (SqlException ex)
            {
                error = Db.Friendly("Could not send the cancellation request.", ex);
                done = null;
            }
            if (error != null) TempData["Error"] = error;
            else TempData["Success"] = done;
            return RedirectToAction("Index");
        }
    }
}
