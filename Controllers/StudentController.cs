using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Mvc;
using SnisDrivingSchool.Models;

namespace SnisDrivingSchool.Controllers
{
    // Only students can open these pages. Guests are sent to log in and then brought back here.
    [RoleRequired("Student", RememberPageForGuests = true)]
    public class StudentController : Controller
    {
        // ---------------- SETTINGS ----------------

        // A new booking stays Pending until the EFT payment is confirmed, then becomes Booked
        private const string StatusPending = "Pending";
        private const string StatusBooked = "Booked";
        private const string StatusCancelled = "Cancelled";
        private const string StatusCompleted = "Completed";

        // Students can only pay online by EFT
        // BOOKING11.PaymentStatus after the student says they paid (waiting for reception)
        private const string PaymentSent = "Sent";

        // ---------------- HELPERS ----------------

        private int CurrentStudentId
        {
            get { return Convert.ToInt32(Session["UserId"]); }
        }

        private static string Str(SqlDataReader r, string col)
        {
            object v = r[col];
            return v == DBNull.Value ? "" : v.ToString().Trim();
        }

        private StudentProfile LoadProfile(int studentId)
        {
            const string sql = @"SELECT StudentID, StudentFirstName, StudentLastName, StudentPhoneNo,
                                        StudentEmailAdress, StudentAdress, StudentUserName, [Status], ISNULL(IdNumber, '') AS IdNumber,
                                        ISNULL(LicenceStatus, '') AS LicenceStatus
                                 FROM dbo.Student1 WHERE StudentID = @id";
            using (var con = new SqlConnection(Db.ConnStr))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@id", studentId);
                con.Open();
                SaIdNumber.EnsureColumn(con);
                Licence.EnsureColumns(con);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new StudentProfile
                    {
                        StudentId = Convert.ToInt32(r["StudentID"]),
                        UserName = Str(r, "StudentUserName"),
                        Name = Str(r, "StudentFirstName"),
                        Surname = Str(r, "StudentLastName"),
                        PhoneNumber = Str(r, "StudentPhoneNo"),
                        Email = Str(r, "StudentEmailAdress"),
                        Address = Str(r, "StudentAdress"),
                        Status = Str(r, "Status"),
                        IdNumber = Str(r, "IdNumber"),
                        LicenceStatus = Str(r, "LicenceStatus")
                    };
                }
            }
        }

        // This student's bookings only, newest booking first (highest BookingID on the first row).
        // search = text to look for in the package name; bookingId = 0 means "all bookings";
        // paidOnly = true returns only bookings with a completed payment (cancelled or not).
        private List<BookingRow> LoadMyBookings(string search, int bookingId, bool paidOnly)
        {
            var list = new List<BookingRow>();
            const string sql = @"
                SELECT b.BookingID, b.PackageID, p.PackageName, p.PackagePrice, b.TSID,
                       CONVERT(varchar(5), t.StartTime, 108) AS StartTime,
                       b.InstructorID, i.Name, i.Surname, b.BookingDate, b.EndDate, b.BookingStatus,
                       b.PaymentStatus AS BookingPaymentStatus,
                       b.CancelledBy, b.CancelReason, b.CancelledOn, ISNULL(b.RefundAmount, 0) AS RefundAmount,
                       b.ReplaceReason, (SELECT pi.Name + ' ' + pi.Surname FROM dbo.INSTRUCTORNEW pi WHERE pi.InstructorID = b.ProposedInstructorID) AS ProposedName,
                       pay.PaymentID, pay.Amount AS PaidAmount, pay.PaymentDate, pay.PaymentStatus
                FROM dbo.BOOKING11 b
                LEFT JOIN dbo.PACKAGEN p       ON p.PackageID    = b.PackageID
                LEFT JOIN dbo.TimeSlotNew t    ON t.TSID         = b.TSID
                LEFT JOIN dbo.INSTRUCTORNEW i  ON i.InstructorID = b.InstructorID
                OUTER APPLY (SELECT TOP 1 x.PaymentID, x.Amount, x.PaymentDate, x.PaymentStatus
                             FROM dbo.PAYMENT11 x
                             WHERE x.BookingID = b.BookingID
                             ORDER BY x.PaymentID DESC) pay
                WHERE b.StudentID = @s
                  AND (@q = '' OR p.PackageName LIKE '%' + @q + '%')
                  AND (@b = 0 OR b.BookingID = @b)
                  AND (@po = 0 OR pay.PaymentID IS NOT NULL)
                ORDER BY b.BookingID DESC";

            using (var con = new SqlConnection(Db.ConnStr))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@s", CurrentStudentId);
                cmd.Parameters.AddWithValue("@q", (search ?? "").Trim());
                cmd.Parameters.AddWithValue("@b", bookingId);
                cmd.Parameters.AddWithValue("@po", paidOnly ? 1 : 0);
                con.Open();
                Cancellation.EnsureColumns(con);
                Replacement.EnsureColumns(con);
                LessonPlan.EnsureColumns(con);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new BookingRow
                        {
                            BookingId = Convert.ToInt32(r["BookingID"]),
                            PackageId = Convert.ToInt32(r["PackageID"]),
                            PackageName = Str(r, "PackageName"),
                            Price = r["PackagePrice"] == DBNull.Value ? 0m : Convert.ToDecimal(r["PackagePrice"]),
                            TsId = Convert.ToInt32(r["TSID"]),
                            TimeText = Str(r, "StartTime"),
                            InstructorId = r["InstructorID"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["InstructorID"]),
                            InstructorName = (Str(r, "Name") + " " + Str(r, "Surname")).Trim(),
                            BookingDate = Convert.ToDateTime(r["BookingDate"]),
                            EndDate = r["EndDate"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["EndDate"]),
                            Status = Str(r, "BookingStatus"),
                            BookingPaymentStatus = Str(r, "BookingPaymentStatus"),
                            CancelledBy = Str(r, "CancelledBy"),
                            CancelReason = Str(r, "CancelReason"),
                            CancelledOn = r["CancelledOn"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["CancelledOn"]),
                            RefundAmount = Convert.ToDecimal(r["RefundAmount"]),
                            ProposedInstructor = Str(r, "ProposedName"),
                            ReplaceReason = Str(r, "ReplaceReason"),
                            PaymentId = r["PaymentID"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["PaymentID"]),
                            PaidAmount = r["PaidAmount"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(r["PaidAmount"]),
                            PaidOn = r["PaymentDate"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["PaymentDate"]),
                            PaymentMethod = Str(r, "PaymentStatus")
                        });
                    }
                }
            }
            return list;
        }

        // ---------------- ACCOUNT STATUS (verify, then approval by reception) ----------------

        // Pages a student can open before their account is verified and approved
        private static readonly HashSet<string> OpenBeforeApproval = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Index", "Verify", "ResendCode", "Profile"
        };

        // Runs before every student page: not verified / not approved students can only see their status
        protected override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            base.OnActionExecuting(filterContext);
            if (filterContext.Result != null || (Session["Role"] as string) != "Student") return;

            StudentApproval.State state;
            try { state = StudentApproval.Get(CurrentStudentId); }
            catch (SqlException) { return; }   // the page itself will show the database error
            ViewBag.Account = state;
            if (state == null || state.CanBook) return;

            string action = filterContext.ActionDescriptor.ActionName;
            if (OpenBeforeApproval.Contains(action)) return;

            TempData["Error"] = state.Stage == StudentApproval.Stage.Verify
                ? "Please verify your account first."
                : state.Stage == StudentApproval.Stage.Waiting
                    ? "Your account is waiting for approval by reception. You can book and pay once it is approved."
                    : "Your account was not approved, so you can't book or pay. Please contact the school.";
            filterContext.Result = RedirectToAction(state.Stage == StudentApproval.Stage.Verify ? "Verify" : "Index");
        }

        // ---------------- NEW INSTRUCTOR PROPOSED (the first one couldn't take the lesson) ----------------

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AcceptInstructor(int id)
        {
            string done, error;
            try { error = Replacement.Accept(id, CurrentStudentId, out done); }
            catch (SqlException ex) { error = Db.Friendly("Could not save your answer.", ex); done = null; }
            if (error != null) TempData["Error"] = error; else TempData["Success"] = done;
            return RedirectToAction("Bookings");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DeclineInstructor(int id)
        {
            string done, error;
            try { error = Replacement.Decline(id, CurrentStudentId, out done); }
            catch (SqlException ex) { error = Db.Friendly("Could not save your answer.", ex); done = null; }
            if (error != null) TempData["Error"] = error; else TempData["Success"] = done;
            return RedirectToAction("Bookings");
        }

        // GET: Student/Verify  (type the 6-digit code sent to the student's email address)
        public ActionResult Verify()
        {
            var state = ViewBag.Account as StudentApproval.State;
            if (state != null && state.Verified) return RedirectToAction("Index");
            ViewBag.Email = MaskEmail(state == null ? "" : state.Email);
            return View(state);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Verify(string code)
        {
            string error;
            try { error = StudentApproval.CheckCode(CurrentStudentId, code); }
            catch (SqlException ex) { error = Db.Friendly("Could not check the code.", ex); }
            if (error != null)
            {
                TempData["Error"] = error;
                return RedirectToAction("Verify");
            }
            TempData["Success"] = "Thank you, your account is verified. Reception will now check your details and approve your account.";
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ResendCode()
        {
            try
            {
                bool emailed;
                string code = StudentApproval.SendCode(CurrentStudentId, out emailed);
                if (emailed) TempData["Success"] = "A new code has been sent. Codes expire after " + StudentApproval.CodeMinutes + " minutes.";
                else TempData["DemoCode"] = code;
            }
            catch (SqlException ex)
            {
                TempData["Error"] = Db.Friendly("Could not create a new code.", ex);
            }
            return RedirectToAction("Verify");
        }

        // "1nomcebozulu@gmail.com" -> "1n••••••••@gmail.com"
        private static string MaskEmail(string email)
        {
            int at = (email ?? "").IndexOf('@');
            if (at < 2) return email ?? "";
            return email.Substring(0, 2) + new string('\u2022', Math.Max(3, at - 2)) + email.Substring(at);
        }

        // ---------------- DASHBOARD ----------------

        // Home page: next lesson, what still needs doing (pay, documents) and quick links
        public ActionResult Index()
        {
            // Not approved yet: show where the account is instead of the dashboard
            var state = ViewBag.Account as StudentApproval.State;
            if (state != null && !state.CanBook)
            {
                if (state.Stage == StudentApproval.Stage.Verify) return RedirectToAction("Verify");
                var pending = new StudentHomeViewModel { Account = state };
                try
                {
                    pending.Profile = LoadProfile(CurrentStudentId);
                    pending.LicenceDocumentId = MyDocumentId();
                    pending.IdDocumentId = MyIdDocumentId();
                }
                catch (SqlException ex) { ViewBag.Error = Db.Friendly("Could not load your details.", ex); }
                return View("Status", pending);
            }

            var vm = new StudentHomeViewModel();
            try
            {
                vm.Profile = LoadProfile(CurrentStudentId);
                vm.Bookings = LoadMyBookings(null, 0, false);
                vm.LicenceDocumentId = MyDocumentId();
                vm.IdDocumentId = MyIdDocumentId();
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    var rated = new HashSet<int>(Lessons.LoadReviews(con, CurrentStudentId, 0).Select(x => x.BookingId));
                    vm.ToRateCount = vm.Bookings.Count(b => b.IsCompleted && !rated.Contains(b.BookingId));
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load your dashboard.", ex);
            }
            return View(vm);
        }

        // ---------------- PROFILE ----------------

        [HttpGet]
      
        public new ActionResult Profile()
        {
            var model = LoadProfile(CurrentStudentId);
            if (model == null) return RedirectToAction("Index");
            model.DocumentId = MyDocumentId();
            model.IdDocumentId = MyIdDocumentId();
            return View(model);
        }

        [HttpPost, ActionName("Profile")]
        [ValidateAntiForgeryToken]
        public ActionResult ProfilePost(StudentProfile model, HttpPostedFileBase licenceDocument, HttpPostedFileBase idDocument)
        {
            var existing = LoadProfile(CurrentStudentId);
            if (existing == null) return RedirectToAction("Index");
            existing.DocumentId = MyDocumentId();
            model.DocumentId = existing.DocumentId;
            model.IdDocumentId = MyIdDocumentId();

            model.StudentId = existing.StudentId;
            model.UserName = existing.UserName;
            model.Status = existing.Status;
            model.Name = existing.Name;
            model.Surname = existing.Surname;
            ModelState.Remove("Name");
            ModelState.Remove("Surname");

            // A valid ID number is locked; a missing or invalid one can be added/corrected here
            bool idLocked = SaIdNumber.Parse(existing.IdNumber).IsValid;
            SaIdNumber newId = null;
            if (idLocked)
            {
                model.IdNumber = existing.IdNumber;
            }
            else if (!string.IsNullOrWhiteSpace(model.IdNumber))
            {
                newId = SaIdNumber.Parse(model.IdNumber);
                if (!newId.IsValid)
                {
                    ViewBag.Error = newId.Error;
                    return View("Profile", model);
                }
                model.IdNumber = newId.Number;
            }
            else
            {
                model.IdNumber = existing.IdNumber;
            }

            // Current licence (they can update it, e.g. after getting Code 8). A new licence needs a new PDF.
            model.LicenceStatus = Licence.Normalize(model.LicenceStatus);
            string licenceError = Licence.StatusError(model.LicenceStatus,
                                                      SaIdNumber.Parse(newId != null ? newId.Number : existing.IdNumber));
            if (licenceError != null)
            {
                ViewBag.Error = licenceError;
                return View("Profile", model);
            }
            bool licenceChanged = model.LicenceStatus != Licence.Normalize(existing.LicenceStatus);
            bool fileChosen = licenceDocument != null && licenceDocument.ContentLength > 0;
            byte[] pdf = null;
            string pdfName = null;
            if (Licence.NeedsDocument(model.LicenceStatus) && (fileChosen || licenceChanged || existing.DocumentId == 0))
            {
                string pdfError = Licence.ReadPdf(licenceDocument, out pdf, out pdfName);
                if (pdfError != null)
                {
                    ViewBag.Error = (licenceChanged ? "You changed your licence, so a PDF copy of the new one is needed. " : "") + pdfError;
                    return View("Profile", model);
                }
            }

            // ID document: required when none is on file yet; otherwise only to replace it
            byte[] idPdf = null;
            string idPdfName = null;
            if ((idDocument != null && idDocument.ContentLength > 0) || model.IdDocumentId == 0)
            {
                string idError = Licence.ReadPdf(idDocument, out idPdf, out idPdfName, Licence.IdDocWhat);
                if (idError != null)
                {
                    ViewBag.Error = idError;
                    return View("Profile", model);
                }
            }

            if (!ModelState.IsValid) return View("Profile", model);

            bool changePassword = !string.IsNullOrWhiteSpace(model.NewPassword);

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();

                    using (var check = new SqlCommand(
                        "SELECT COUNT(*) FROM dbo.Student1 WHERE StudentEmailAdress = @e AND StudentID <> @id", con))
                    {
                        check.Parameters.AddWithValue("@e", model.Email.Trim());
                        check.Parameters.AddWithValue("@id", model.StudentId);
                        if ((int)check.ExecuteScalar() > 0)
                        {
                            ViewBag.Error = "That email address is already used by another account.";
                            return View("Profile", model);
                        }
                    }

                    if (newId != null && SaIdNumber.InUse(con, newId.Number, model.StudentId))
                    {
                        ViewBag.Error = "That ID number is already used by another account. Please contact the school.";
                        return View("Profile", model);
                    }

                    string sql = "UPDATE dbo.Student1 SET StudentPhoneNo = @p, StudentEmailAdress = @e, StudentAdress = @a"
                               + (changePassword ? ", StudentPassWord = @pw" : "")
                               + (newId != null ? ", IdNumber = @idn" : "")
                               + ", LicenceStatus = @lic"
                               + " WHERE StudentID = @id";

                    using (var tx = con.BeginTransaction())
                    using (var cmd = new SqlCommand(sql, con, tx))
                    {
                        cmd.Parameters.AddWithValue("@p", model.PhoneNumber.Trim());
                        cmd.Parameters.AddWithValue("@e", model.Email.Trim());
                        cmd.Parameters.AddWithValue("@a", (model.Address ?? "").Trim());
                        cmd.Parameters.AddWithValue("@id", model.StudentId);
                        if (changePassword) cmd.Parameters.AddWithValue("@pw", model.NewPassword.Trim());
                        if (newId != null) cmd.Parameters.AddWithValue("@idn", newId.Number);
                        cmd.Parameters.AddWithValue("@lic", model.LicenceStatus);
                        cmd.ExecuteNonQuery();
                        if (pdf != null) Licence.SaveDocument(con, tx, model.StudentId, model.LicenceStatus, pdfName, pdf);
                        if (idPdf != null) Licence.SaveIdDocument(con, tx, model.StudentId, idPdfName, idPdf);
                        tx.Commit();
                    }
                }

                TempData["Success"] = "Your profile has been updated.";
                return RedirectToAction("Profile");
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not save your profile.", ex);
                return View("Profile", model);
            }
        }

        // Newest licence PDF of the logged-in student (0 = none)
        private int MyDocumentId()
        {
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                return Licence.CurrentDocumentId(con, CurrentStudentId);
            }
        }

        // Newest ID document PDF of the logged-in student (0 = none)
        private int MyIdDocumentId()
        {
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                return Licence.CurrentIdDocumentId(con, CurrentStudentId);
            }
        }

        // ---------------- BOOK A LESSON ----------------

        private BookLessonViewModel BuildBookLessonModel()
        {
            var vm = new BookLessonViewModel();

            var profile = LoadProfile(CurrentStudentId);
            if (profile != null)
            {
                vm.StudentFirstName = profile.Name;
                vm.StudentLastName = profile.Surname;
                var id = SaIdNumber.Parse(profile.IdNumber);
                vm.LicenceStatus = profile.LicenceStatus;
                bool hasDoc = MyDocumentId() > 0;
                ViewBag.Eligibility = id.EligibilityText + " " + Licence.NextStepText(profile.LicenceStatus)
                                    + (Licence.NeedsDocument(profile.LicenceStatus) && !hasDoc ? " Please upload a PDF copy of your licence on your Profile page." : "");
                ViewBag.CanBook = id.LearnerLight && Licence.IsStatus(profile.LicenceStatus) && (hasDoc || !Licence.NeedsDocument(profile.LicenceStatus));
            }

            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                vm.BookedMonths = Cancellation.BookedMonths(con, CurrentStudentId);
                vm.PackageLessons = LessonPlan.PackageCounts(con);

                // Lesson days the student already has (shown on the calendar)
                using (var cmd = new SqlCommand(
                    @"SELECT BookingDate, ISNULL(EndDate, CAST(BookingDate AS date)) AS EndDate FROM dbo.BOOKING11
                      WHERE StudentID = @s AND ISNULL(BookingStatus, '') <> 'Cancelled'
                        AND ISNULL(EndDate, CAST(BookingDate AS date)) >= CAST(GETDATE() AS date)", con))
                {
                    cmd.Parameters.AddWithValue("@s", CurrentStudentId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            DateTime a = Convert.ToDateTime(r["BookingDate"]).Date, z = Convert.ToDateTime(r["EndDate"]).Date;
                            for (var d = a; d <= z; d = d.AddDays(1))
                                if (d.DayOfWeek != DayOfWeek.Sunday) vm.LessonDays.Add(d.ToString("yyyy-MM-dd"));
                        }
                    }
                }

                using (var cmd = new SqlCommand(
                    "SELECT PackageID, PackageName, PackagePrice FROM dbo.PACKAGEN ORDER BY PackageID", con))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string price = r["PackagePrice"] == DBNull.Value ? "" :
                            " - " + Fmt.Rand(Convert.ToDecimal(r["PackagePrice"]));
                        vm.Packages.Add(new SelectItem
                        {
                            Id = Convert.ToInt32(r["PackageID"]),
                            Text = Str(r, "PackageName") + price
                        });
                    }
                }

                using (var cmd = new SqlCommand(
                    "SELECT InstructorID, Name, Surname FROM dbo.INSTRUCTORNEW WHERE Status = 'Approved' ORDER BY Name, Surname", con))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        vm.Instructors.Add(new SelectItem
                        {
                            Id = Convert.ToInt32(r["InstructorID"]),
                            Text = (Str(r, "Name") + " " + Str(r, "Surname")).Trim()
                        });
                    }
                }
                InstructorLeave.MarkInList(con, vm.Instructors);   // shows who is on leave (they can't be booked on those days)

                using (var cmd = new SqlCommand(
                    @"SELECT TSID,
                             CONVERT(varchar(5), StartTime, 108) AS S,
                             CONVERT(varchar(5), EndTime, 108)   AS E
                      FROM dbo.TimeSlotNew ORDER BY StartTime", con))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string s = Str(r, "S"), e = Str(r, "E");
                        vm.TimeSlots.Add(new SelectItem
                        {
                            Id = Convert.ToInt32(r["TSID"]),
                            Text = e == "" ? s : s + " - " + e
                        });
                    }
                }
            }
            return vm;
        }

        [HttpGet]
        public ActionResult BookLesson()
        {
            try
            {
                return View(BuildBookLessonModel());
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load the booking page.", ex);
                return View(new BookLessonViewModel());
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult BookLesson(int? packageId, int? instructorId, int? tsId, string bookingDate)
        {
            int licenceCode = 0;
            string error = null;
            DateTime date = DateTime.MinValue;
            int newBookingId = 0;

            if (!packageId.HasValue || !instructorId.HasValue || !tsId.HasValue || string.IsNullOrWhiteSpace(bookingDate))
                error = "Please choose an instructor, package, time and date.";
            else if (!DateTime.TryParseExact(bookingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                error = "Please pick a valid date from the calendar.";
            else if (date.Date < Cancellation.EarliestBookingDate)
                error = Cancellation.TooSoonError();
            else if (date.Date > DateTime.Today.AddMonths(2))
                error = "Lessons can only be booked up to two months ahead (until "
                      + DateTime.Today.AddMonths(2).ToString("yyyy/MM/dd") + ").";

            if (error == null)
            {
                // South African age rules (learner's licence 17+, driving test packages 18+)
                try
                {
                    using (var con = new SqlConnection(Db.ConnStr))
                    {
                        con.Open();
                        string idNumber = SaIdNumber.ForStudent(con, CurrentStudentId);
                        string package = SaIdNumber.PackageName(con, packageId.Value);
                        string status = Licence.ForStudent(con, CurrentStudentId);
                        error = SaIdNumber.BookingError(idNumber, package)
                             ?? Licence.BookingError(status, package, Licence.CurrentDocumentId(con, CurrentStudentId) > 0);
                        licenceCode = Licence.TargetCode(status, package);   // learner's -> 8, Code 8 -> 10, Code 10 -> 14
                    }
                }
                catch (SqlException ex)
                {
                    error = Db.Friendly("Could not check your age.", ex);
                }
            }

            if (error == null)
            {
                try
                {
                    error = SaveBooking(packageId.Value, instructorId.Value, tsId.Value, date.Date, licenceCode, out newBookingId);
                }
                catch (SqlException ex)
                {
                    // If the Output window shows an AdminStaffID error, run this once in SQL:
                    // ALTER TABLE dbo.BOOKING11 ALTER COLUMN AdminStaffID INT NULL;
                    error = Db.Friendly("Could not save the booking.", ex);
                }
            }

            if (error == null)
            {
                TempData["Success"] = "Your lesson for " + date.ToString("yyyy/MM/dd")
                                    + " has been saved as Pending. It becomes Booked once your EFT payment is confirmed.";
                return RedirectToAction("Payment", new { id = newBookingId });
            }

            BookLessonViewModel vm;
            try { vm = BuildBookLessonModel(); }
            catch (SqlException ex) { vm = new BookLessonViewModel(); error += " " + Db.Friendly("The booking form could not be reloaded.", ex); }

            vm.SelectedPackageId = packageId ?? 0;
            vm.SelectedInstructorId = instructorId ?? 0;
            vm.SelectedTsId = tsId ?? 0;
            vm.SelectedDate = bookingDate ?? "";
            ViewBag.Error = error;
            return View(vm);
        }

        // Returns an error message, or null when the booking was saved
        private string SaveBooking(int packageId, int instructorId, int tsId, DateTime date, int licenceCode, out int bookingId)
        {
            bookingId = 0;

            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();

                // 1. The chosen package, instructor and time slot must really exist
                using (var cmd = new SqlCommand(
                    @"SELECT (SELECT COUNT(*) FROM dbo.PACKAGEN WHERE PackageID = @p),
                             (SELECT COUNT(*) FROM dbo.INSTRUCTORNEW WHERE InstructorID = @i AND Status = 'Approved'),
                             (SELECT COUNT(*) FROM dbo.TimeSlotNew WHERE TSID = @t)", con))
                {
                    cmd.Parameters.AddWithValue("@p", packageId);
                    cmd.Parameters.AddWithValue("@i", instructorId);
                    cmd.Parameters.AddWithValue("@t", tsId);
                    using (var r = cmd.ExecuteReader())
                    {
                        r.Read();
                        if (Convert.ToInt32(r[0]) == 0 || Convert.ToInt32(r[1]) == 0 || Convert.ToInt32(r[2]) == 0)
                            return "Please choose a valid instructor, package and time.";
                    }
                }

                // 2. Only one booking per calendar month (cancelled ones don't count)
                string monthError = Cancellation.MonthLimitError(con, CurrentStudentId, date, "You");
                if (monthError != null) return monthError;

                // The package's lessons: one a day from the chosen date, Sundays skipped
                Replacement.EnsureColumns(con);
                LessonPlan.EnsureColumns(con);
                if (date.DayOfWeek == DayOfWeek.Sunday) return "There are no lessons on Sundays. Please choose another start date.";
                int lessons = LessonPlan.CountFor(con, null, packageId);
                DateTime end = LessonPlan.End(date, lessons);
                string period = lessons == 1 ? date.ToString("d MMMM") : date.ToString("d MMMM") + " to " + end.ToString("d MMMM");

                // 2b. Instructor must not be on leave on any of the lesson days
                if (InstructorLeave.IsOnLeave(con, null, instructorId, date, end))
                    return "That instructor is on leave during " + period + ". Please choose another instructor or date.";

                // 2c. Instructor must be free at that time on every lesson day (a lesson, or a lesson offered to them)
                using (var cmd = new SqlCommand(
                    @"SELECT COUNT(*) FROM dbo.BOOKING11 x
                      WHERE (x.InstructorID = @i OR x.ProposedInstructorID = @i) AND x.TSID = @t
                        AND " + LessonPlan.OverlapSql + " AND x.BookingStatus <> @c", con))
                {
                    cmd.Parameters.AddWithValue("@i", instructorId);
                    cmd.Parameters.AddWithValue("@t", tsId);
                    cmd.Parameters.AddWithValue("@d", date.Date);
                    cmd.Parameters.AddWithValue("@e", end.Date);
                    cmd.Parameters.AddWithValue("@c", StatusCancelled);
                    if ((int)cmd.ExecuteScalar() > 0)
                        return "That instructor already has a lesson at that time during " + period + ". Please choose another time, date or instructor.";
                }

                // 3. Student must not already have lessons during that period
                using (var cmd = new SqlCommand(
                    @"SELECT COUNT(*) FROM dbo.BOOKING11 x
                      WHERE x.StudentID = @s AND " + LessonPlan.OverlapSql + " AND x.BookingStatus <> @c", con))
                {
                    cmd.Parameters.AddWithValue("@s", CurrentStudentId);
                    cmd.Parameters.AddWithValue("@d", date.Date);
                    cmd.Parameters.AddWithValue("@e", end.Date);
                    cmd.Parameters.AddWithValue("@c", StatusCancelled);
                    if ((int)cmd.ExecuteScalar() > 0)
                        return "You already have lessons booked during " + period + ".";
                }

                // 4. Save as Pending (it is only Booked once paid)
                int studentId = CurrentStudentId;
                using (var tx = con.BeginTransaction())
                {
                    bookingId = Db.InsertRow(con, tx, "BOOKING11", "BookingID",
                        "AdminStaffID, StudentID, PackageID, TSID, BookingDate, BookingStatus, InstructorID",
                        "NULL, @s, @p, @t, @d, @st, @i",
                        cmd =>
                        {
                            cmd.Parameters.AddWithValue("@s", studentId);
                            cmd.Parameters.AddWithValue("@p", packageId);
                            cmd.Parameters.AddWithValue("@t", tsId);
                            cmd.Parameters.AddWithValue("@d", date);
                            cmd.Parameters.AddWithValue("@st", StatusPending);
                            cmd.Parameters.AddWithValue("@i", instructorId);
                        });
                    Licence.SetBookingCode(con, tx, bookingId, licenceCode);
                    LessonPlan.SetEnd(con, tx, bookingId, end);
                    tx.Commit();
                }
                return null;
            }
        }

        // ---------------- MY BOOKINGS ----------------

        [HttpGet]
        public ActionResult Bookings(string search)
        {
            var vm = new MyBookingsViewModel { Search = (search ?? "").Trim() };
            try
            {
                vm.Bookings = LoadMyBookings(vm.Search, 0, false);
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load your bookings.", ex);
            }
            return View(vm);
        }

        // Printable history of ALL bookings (paid, unpaid and cancelled) with a pie chart.
        // ?print=1 opens the browser's print window automatically.
        public ActionResult BookingHistory(string print)
        {
            var vm = new BookingHistoryViewModel
            {
                Generated = DateTime.Now,
                Student = LoadProfile(CurrentStudentId),
                AutoPrint = print == "1" || string.Equals(print, "true", StringComparison.OrdinalIgnoreCase)
            };
            try
            {
                vm.Bookings = LoadMyBookings(null, 0, false);
                foreach (var b in vm.Bookings)
                {
                    if (b.IsCancelled) vm.CancelledCount++;
                    else if (b.Paid) vm.PaidCount++;
                    else vm.UnpaidCount++;
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load your booking history.", ex);
            }
            return View(vm);
        }

        public ActionResult Reschedule(int? id) { return RedirectToAction("Bookings"); }

        [HttpGet]
        public ActionResult CancelBooking() { return RedirectToAction("Bookings"); }

        // The student cancels one of their bookings. A reason is required; if it was paid, the money is refunded first.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult CancelBooking(int id, string reason)
        {
            string done, error;
            try
            {
                error = Cancellation.Cancel(id, Cancellation.ByStudent, CurrentStudentId, reason, out done);
            }
            catch (SqlException ex)
            {
                error = Db.Friendly("Could not cancel the booking.", ex);
                done = null;
            }
            if (error != null) TempData["Error"] = error;
            else TempData["Success"] = done;
            return RedirectToAction("Bookings");
        }

        // ---------------- PAYMENT (EFT only) ----------------

        private PaymentViewModel MakePaymentModel(BookingRow b)
        {
            var p = LoadProfile(CurrentStudentId);
            string surname = p == null ? "" : Regex.Replace(p.Surname ?? "", @"\s+", "").ToUpperInvariant();
            string fullName = p == null ? "" : (p.Name + " " + p.Surname).Trim();
            return new PaymentViewModel
            {
                Booking = b,
                StudentName = fullName,
                Reference = "BK" + b.BookingId + "-" + surname,
                AccountName = fullName
            };
        }

        // Checks the bank, account name and the account number length for the chosen bank
        private static string ValidateEft(PaymentViewModel m)
        {
            var bank = SaBanks.Find(m.SelectedBank);
            if (bank == null) return "Please choose your bank.";

            if (string.IsNullOrWhiteSpace(m.AccountName)) return "Please enter the account name.";
            if (m.AccountName.Length > 100) return "The account name is too long.";

            string no = m.AccountNumber ?? "";
            if (no == "") return "Please enter your account number.";
            if (!Regex.IsMatch(no, @"^\d+$")) return "The account number must contain digits only.";
            if (Regex.IsMatch(no, @"^0+$")) return "Please enter a valid account number.";
            if (no.Length < bank.MinDigits || no.Length > bank.MaxDigits)
                return bank.Name + " account numbers must be " + bank.Rule + ". You entered " + no.Length + " digits.";

            return null;
        }

        [HttpGet]
        public ActionResult Payment(int? id)
        {
            try
            {
                BookingRow b = null;

                if (id.HasValue)
                {
                    var list = LoadMyBookings(null, id.Value, false);
                    if (list.Count > 0) b = list[0];
                }
                else
                {
                    // No booking chosen: use the newest booking that is not paid yet
                    foreach (var x in LoadMyBookings(null, 0, false))
                    {
                        if (!x.Paid && !x.IsCancelled) { b = x; break; }
                    }
                    if (b == null)
                    {
                        TempData["Success"] = "You have no unpaid bookings.";
                        return RedirectToAction("Bookings");
                    }
                }

                if (b == null)
                {
                    TempData["Error"] = "Booking not found.";
                    return RedirectToAction("Bookings");
                }
                return View(MakePaymentModel(b));
            }
            catch (SqlException ex)
            {
                TempData["Error"] = Db.Friendly("Could not load the payment page.", ex);
                return RedirectToAction("Bookings");
            }
        }

        [HttpPost, ActionName("Payment")]
        [ValidateAntiForgeryToken]
        public ActionResult PaymentPost(int id, string method, string bank, string accountName, string accountNumber,
                                        string cardName, string cardNumber, string cardExpiry, string cardCvc)
        {
            BookingRow b;
            try
            {
                var list = LoadMyBookings(null, id, false);
                if (list.Count == 0)
                {
                    TempData["Error"] = "Booking not found.";
                    return RedirectToAction("Bookings");
                }
                b = list[0];
            }
            catch (SqlException ex)
            {
                TempData["Error"] = Db.Friendly("Could not load the booking.", ex);
                return RedirectToAction("Bookings");
            }

            var model = MakePaymentModel(b);
            model.Method = method == PaymentBank.Card ? PaymentBank.Card : PaymentBank.Eft;
            model.SelectedBank = bank;
            model.AccountName = (accountName ?? "").Trim();
            model.AccountNumber = (accountNumber ?? "").Trim();
            model.CardName = (cardName ?? "").Trim();

            // The "bank" checks the details first (the card number and CVC are never stored)
            string declined = model.Method == PaymentBank.Card
                ? PaymentBank.CardError(cardName, cardNumber, cardExpiry, cardCvc)
                : PaymentBank.EftError(bank, accountName, accountNumber);
            if (declined != null)
            {
                ViewBag.Error = "Payment declined: " + declined;
                return View("Payment", model);
            }

            string error;
            decimal amount;
            try
            {
                error = PaymentBank.Pay(b.BookingId, CurrentStudentId, model.Method, out amount);
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not record the payment.", ex);
                return View("Payment", model);
            }
            if (error != null)
            {
                TempData["Error"] = error;
                return RedirectToAction("Payment", new { id = id });
            }

            TempData["Success"] = "Payment successful! " + Fmt.Rand(amount) + " was paid by "
                                + (model.Method == PaymentBank.Card ? "card ending in " + PaymentBank.LastFour(cardNumber) : "EFT from " + bank)
                                + ". Your lesson is now booked.";
            return RedirectToAction("Payment", new { id = id });
        }

        // ---------------- SUMMARY REPORT (completed payments only) ----------------

        // Opens the Power BI report (when its link is set in Web.config),
        // otherwise shows the built-in printable summary report.
        public ActionResult SummaryReport()
        {
            string url = ConfigurationManager.AppSettings["PowerBiReportUrl"];
            if (!string.IsNullOrWhiteSpace(url))
            {
                url = url.Trim();
                return Redirect(url + (url.Contains("?") ? "&" : "?")
                    + "filter=vw_StudentPaymentSummary/StudentID%20eq%20" + CurrentStudentId);
            }

            var vm = new SummaryReportViewModel
            {
                Generated = DateTime.Now,
                Student = LoadProfile(CurrentStudentId)
            };
            try
            {
                // Only bookings with a completed payment (cancelled or not)
                vm.Bookings = LoadMyBookings(null, 0, true);
                foreach (var b in vm.Bookings)
                {
                    vm.PaymentCount++;
                    vm.TotalPaid += (b.PaidAmount ?? 0m) - b.RefundAmount;
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load the report.", ex);
            }
            return View(vm);
        }

        // ---------------- REVIEWS ----------------

        // ---------------- RATE & REVIEW ----------------

        // Completed lessons the student has not rated yet, and the reviews they already wrote
        public ActionResult RateAndReviews()
        {
            var vm = new StudentReviewsViewModel();
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    vm.Mine = Lessons.LoadReviews(con, CurrentStudentId, 0);
                }
                var rated = new HashSet<int>(vm.Mine.Select(x => x.BookingId));
                vm.ToRate = LoadMyBookings(null, 0, false)
                    .Where(b => b.IsCompleted && !rated.Contains(b.BookingId))
                    .OrderByDescending(b => b.BookingDate).ToList();
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load your reviews.", ex);
            }
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult RateAndReviews(int bookingId, int? rating, string comment)
        {
            string error;
            try
            {
                error = Lessons.SaveReview(CurrentStudentId, bookingId, rating ?? 0, comment);
            }
            catch (SqlException ex)
            {
                error = Db.Friendly("Could not save your review.", ex);
            }
            if (error != null) TempData["Error"] = error;
            else TempData["Success"] = "Thank you! Your review for lesson #" + bookingId + " has been saved.";
            return RedirectToAction("RateAndReviews");
        }
    }
}