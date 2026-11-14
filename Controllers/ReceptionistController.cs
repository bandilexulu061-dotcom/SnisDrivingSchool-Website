using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Mvc;
using SnisDrivingSchool.Models;

namespace SnisDrivingSchool.Controllers
{
    // Only receptionists can open these pages (see Infrastructure/RoleRequiredAttribute.cs)
    [RoleRequired("Receptionist")]
    public partial class ReceptionistController : Controller
    {
        // Reception home: one tile per area, each with a live number (same look as the manager home)
        public ActionResult Index()
        {
            var home = new RecHome();
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    StudentApproval.EnsureColumns(con);
                    InstructorLeave.EnsureTable(con);
                    DateTime monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
                    DateTime month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                    using (var cmd = new SqlCommand(@"
                        SELECT
                          (SELECT COUNT(*) FROM dbo.BOOKING11 WHERE BookingDate >= @mon AND BookingDate < DATEADD(DAY, 7, @mon)
                                                               AND ISNULL(BookingStatus, '') <> 'Cancelled') AS ThisWeek,
                          (SELECT COUNT(*) FROM dbo.BOOKING11 WHERE CAST(BookingDate AS date) = CAST(GETDATE() AS date)
                                                               AND ISNULL(BookingStatus, '') <> 'Cancelled') AS Today,
                          (SELECT COUNT(*) FROM dbo.Student1) AS Students,
                          (SELECT COUNT(*) FROM dbo.Student1 s WHERE " + StudentApproval.StageSql + @" = 'Waiting') AS Waiting,
                          (SELECT COUNT(*) FROM dbo.AdminStaff11) + (SELECT COUNT(*) FROM dbo.INSTRUCTORNEW) AS Staff,
                          (SELECT COUNT(DISTINCT InstructorID) FROM dbo.InstructorLeave WHERE CAST(GETDATE() AS date) BETWEEN FromDate AND ToDate) AS OnLeave,
                          (SELECT COUNT(*) FROM dbo.Vehicle3 WHERE ISNULL(Archived, 0) = 0) AS Vehicles,
                          (SELECT COUNT(*) FROM dbo.Vehicle3 WHERE ISNULL(Archived, 0) = 0 AND [Disk-ExpiryDate] < DATEADD(DAY, 30, GETDATE())) AS DisksDue,
                          (SELECT COUNT(*) FROM dbo.PAYMENT11 WHERE PaymentDate >= @month) AS Payments,
                          (SELECT ISNULL(SUM(Amount), 0) FROM dbo.PAYMENT11 WHERE PaymentDate >= @month) AS PaidThisMonth", con))
                    {
                        cmd.Parameters.AddWithValue("@mon", monday);
                        cmd.Parameters.AddWithValue("@month", month);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                home.ThisWeek = Convert.ToInt32(r["ThisWeek"]);
                                home.Today = Convert.ToInt32(r["Today"]);
                                home.Students = Convert.ToInt32(r["Students"]);
                                home.Waiting = Convert.ToInt32(r["Waiting"]);
                                home.Staff = Convert.ToInt32(r["Staff"]);
                                home.OnLeave = Convert.ToInt32(r["OnLeave"]);
                                home.Vehicles = Convert.ToInt32(r["Vehicles"]);
                                home.DisksDue = Convert.ToInt32(r["DisksDue"]);
                                home.Payments = Convert.ToInt32(r["Payments"]);
                                home.PaidThisMonth = Convert.ToDecimal(r["PaidThisMonth"]);
                            }
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load the dashboard numbers.", ex);
            }
            ViewBag.Name = Convert.ToString(Session["UserName"]);
            return View(home);
        }

        public ActionResult Bookings()
        {
            LoadFormState();
            var bookings = new List<ReceptionistBookingRow>();
            const string sql = @"
                SELECT b.BookingID, b.AdminStaffID, b.StudentID, b.PackageID, b.TSID,
                       b.BookingDate,
                       CASE WHEN b.BookingStatus = 'Cancelled' AND b.CancelledBy IS NOT NULL
                            THEN 'Cancelled by ' + LOWER(b.CancelledBy) ELSE b.BookingStatus END AS BookingStatus,
                       b.CancelReason, b.InstructorID,
                       (SELECT pi.Name + ' ' + pi.Surname FROM dbo.INSTRUCTORNEW pi WHERE pi.InstructorID = b.ProposedInstructorID) AS ProposedName,
                       i.Name + ' ' + i.Surname AS InstructorName,
                       COALESCE(NULLIF(LTRIM(RTRIM(b.StudentFirstName)), ''), s.StudentFirstName) AS StudentFirstName,
                       COALESCE(NULLIF(LTRIM(RTRIM(b.StudentLastName)), ''),  s.StudentLastName)  AS StudentLastName,
                       p.PackageName,
                       CONVERT(varchar(5), t.StartTime, 108) AS TimeText,
                       CASE WHEN b.RefundedOn IS NOT NULL THEN 'Refunded'
                            WHEN pay.PaymentID IS NOT NULL THEN 'PAID'
                            WHEN b.BookingStatus = 'Cancelled' THEN 'N/A'
                            ELSE ISNULL(NULLIF(LTRIM(RTRIM(b.PaymentStatus)), ''), 'Pending') END AS PaymentStatus
                FROM dbo.BOOKING11 b
                LEFT JOIN dbo.Student1 s       ON s.StudentID    = b.StudentID
                LEFT JOIN dbo.PACKAGEN p       ON p.PackageID    = b.PackageID
                LEFT JOIN dbo.TimeSlotNew t    ON t.TSID         = b.TSID
                LEFT JOIN dbo.INSTRUCTORNEW i  ON i.InstructorID = b.InstructorID
                OUTER APPLY (SELECT TOP 1 x.PaymentID FROM dbo.PAYMENT11 x
                             WHERE x.BookingID = b.BookingID ORDER BY x.PaymentID DESC) pay
                ORDER BY b.BookingID DESC";
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    Cancellation.EnsureColumns(con);
                    Replacement.EnsureColumns(con);
                    using (var cmd = new SqlCommand(sql, con))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            bookings.Add(new ReceptionistBookingRow
                            {
                                BookingID = (int)r["BookingID"],
                                AdminStaffID = r["AdminStaffID"] as int?,
                                StudentID = (int)r["StudentID"],
                                PackageID = (int)r["PackageID"],
                                TSID = (int)r["TSID"],
                                BookingDate = (DateTime)r["BookingDate"],
                                BookingStatus = r["BookingStatus"] as string,
                                CancelReason = r["CancelReason"] as string,
                                ProposedName = r["ProposedName"] as string,
                                InstructorID = r["InstructorID"] as int?,
                                InstructorName = r["InstructorName"] as string,
                                StudentFirstName = r["StudentFirstName"] as string,
                                StudentLastName = r["StudentLastName"] as string,
                                PaymentStatus = r["PaymentStatus"] as string,
                                PackageName = r["PackageName"] as string,
                                TimeText = r["TimeText"] as string
                            });
                        }
                    }
                    bool reopen = (ViewBag.OpenModal as string) == "booking";
                    ViewBag.BookingForm = new StaffBookingFormVm
                    {
                        Controller = "Receptionist",
                        Lists = StaffBooking.LoadLists(con),
                        Form = ViewBag.Form as Dictionary<string, string>,
                        Error = reopen ? ViewBag.ModalError as string : null,
                        Open = reopen
                    };
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load bookings.", ex);
            }

            return View(bookings);
        }

        public ActionResult Students()
        {
            LoadFormState();
            var students = new List<StudentRow>();
            const string sql = @"SELECT StudentID, StudentFirstName, StudentLastName, StudentPhoneNo,
                                        StudentEmailAdress, StudentAdress, StudentUserName, Status, ISNULL(IdNumber, '') AS IdNumber,
                                        ISNULL(LicenceStatus, '') AS LicenceStatus, " + Licence.CurrentDocSql + @" AS DocID, " + Licence.CurrentIdDocSql + @" AS IdDocID,
                                        " + StudentApproval.StageSql + @" AS Stage, s.ApprovalNote
                                 FROM dbo.Student1 s
                                 ORDER BY CASE " + StudentApproval.StageSql + @" WHEN 'Waiting' THEN 0 WHEN 'Verify' THEN 1 ELSE 2 END,
                                          StudentLastName, StudentFirstName";
            try
            {
                string connStr = ConfigurationManager.ConnectionStrings["SnisDb"].ConnectionString;
                using (var con = new SqlConnection(connStr))
                using (var cmd = new SqlCommand(sql, con))
                {
                    con.Open();
                    SaIdNumber.EnsureColumn(con);
                    Licence.EnsureColumns(con);
                    StudentApproval.EnsureColumns(con);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            students.Add(new StudentRow
                            {
                                StudentID = (int)r["StudentID"],
                                StudentFirstName = r["StudentFirstName"] as string,
                                StudentLastName = r["StudentLastName"] as string,
                                StudentPhoneNo = r["StudentPhoneNo"] as string,
                                StudentEmailAdress = r["StudentEmailAdress"] as string,
                                StudentAdress = r["StudentAdress"] as string,
                                StudentUserName = r["StudentUserName"] as string,
                                Status = r["Status"] as string,
                                IdNumber = r["IdNumber"] as string,
                                LicenceStatus = r["LicenceStatus"] as string,
                                DocumentId = r["DocID"] == DBNull.Value ? 0 : Convert.ToInt32(r["DocID"]),
                                IdDocumentId = r["IdDocID"] == DBNull.Value ? 0 : Convert.ToInt32(r["IdDocID"]),
                                Stage = r["Stage"] as string,
                                ApprovalNote = r["ApprovalNote"] as string
                            });
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load students.", ex);
            }

            return View(students);
        }

        // View-only: Receptionist can see staff but cannot add or edit them.
        // One page for everyone who works at the school: office staff and instructors (instructors are staff too).
        // The drop-down on the page filters receptionists / managers / instructors.
        public ActionResult Staff()
        {
            var page = new MgrStaffPage();
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    using (var cmd = new SqlCommand(
                        @"SELECT AdminStaffID, Name, Surname, AdminStaffRole, AdminStaffNo, AdminStaffEmail, UserName, OfficeNo
                          FROM dbo.AdminStaff11 ORDER BY Surname, Name", con))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string role = Convert.ToString(r["AdminStaffRole"]).Trim();
                            page.Admin.Add(new MgrStaffRow
                            {
                                StaffId = Convert.ToInt32(r["AdminStaffID"]),
                                Name = Convert.ToString(r["Name"]),
                                Surname = Convert.ToString(r["Surname"]),
                                Role = role.StartsWith("Rec", StringComparison.OrdinalIgnoreCase) ? "Receptionist" : role,
                                Phone = Convert.ToString(r["AdminStaffNo"]),
                                Email = Convert.ToString(r["AdminStaffEmail"]),
                                UserName = Convert.ToString(r["UserName"]),
                                OfficeNo = Convert.ToString(r["OfficeNo"])
                            });
                        }
                    }
                    using (var cmd = new SqlCommand(
                        @"SELECT InstructorID, Name, Surname, PhoneNo, Email, UserName, LicenseCode, YearsOfExperience, [Status]
                          FROM dbo.INSTRUCTORNEW ORDER BY Surname, Name", con))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            page.Instructors.Add(new MgrInstructorRow
                            {
                                InstructorId = Convert.ToInt32(r["InstructorID"]),
                                Name = Convert.ToString(r["Name"]),
                                Surname = Convert.ToString(r["Surname"]),
                                Phone = Convert.ToString(r["PhoneNo"]),
                                Email = Convert.ToString(r["Email"]),
                                UserName = Convert.ToString(r["UserName"]),
                                LicenseCode = Convert.ToString(r["LicenseCode"]),
                                Years = Convert.ToString(r["YearsOfExperience"]),
                                Status = Convert.ToString(r["Status"])
                            });
                        }
                    }
                    page.Leave = InstructorLeave.Upcoming(con);
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load staff.", ex);
            }
            return View(page);
        }

        // The old Instructors page is now part of Staff
        public ActionResult Instructors()
        {
            return RedirectToAction("Staff", new { show = "instructor" });
        }

        // ---------------- VEHICLES: reception can add a vehicle ----------------

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AddVehicle(string registrationNo, string model, string licenseCode, string diskExpiry, string status)
        {
            string error = ManagerController.SaveNewVehicle(registrationNo, model, licenseCode, diskExpiry, status);
            if (error != null)
            {
                TempData["Error"] = error;
                TempData["VehicleForm"] = new Dictionary<string, string>
                {
                    { "registrationNo", registrationNo ?? "" }, { "model", model ?? "" }, { "licenseCode", licenseCode ?? "" },
                    { "diskExpiry", diskExpiry ?? "" }, { "status", status ?? "" }
                };
            }
            else TempData["Success"] = "Vehicle " + (registrationNo ?? "").Trim().ToUpperInvariant() + " has been added.";
            return RedirectToAction("Vehicles");
        }

        // ---------------- INSTRUCTOR LEAVE (reception records it) ----------------

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult SetLeave(int instructorId, DateTime? fromDate, DateTime? toDate, string note)
        {
            string done = null, error;
            if (!fromDate.HasValue || !toDate.HasValue) error = "Please choose the first and last day of leave.";
            else
            {
                try { error = InstructorLeave.Add(instructorId, fromDate.Value, toDate.Value, note, out done); }
                catch (SqlException ex) { error = Db.Friendly("Could not save the leave.", ex); }
            }
            if (error != null) TempData["Error"] = error; else TempData["Success"] = done;
            return RedirectToAction("Staff");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult RemoveLeave(int leaveId)
        {
            string error;
            try { error = InstructorLeave.Remove(leaveId); }
            catch (SqlException ex) { error = Db.Friendly("Could not remove the leave.", ex); }
            if (error != null) TempData["Error"] = error; else TempData["Success"] = "The leave was removed. The instructor can be booked on those days again.";
            return RedirectToAction("Staff");
        }

        public ActionResult Vehicles()
        {
            var vehicles = new List<VehicleRow>();
            const string sql = @"SELECT VehicleID, RegistractionNo, Model, [License-Code] AS LicenseCode,
                                        [Disk-ExpiryDate] AS DiskExpiryDate,
                                        [Vehicle-Status] AS VehicleStatus,
                                        ISNULL(Archived, 0) AS Archived
                                 FROM dbo.Vehicle3
                                 ORDER BY VehicleID";
            try
            {
                string connStr = ConfigurationManager.ConnectionStrings["SnisDb"].ConnectionString;
                using (var con = new SqlConnection(connStr))
                using (var cmd = new SqlCommand(sql, con))
                {
                    con.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            vehicles.Add(new VehicleRow
                            {
                                VehicleID = (int)r["VehicleID"],
                                RegistractionNo = r["RegistractionNo"] as string,
                                Model = r["Model"] as string,
                                LicenseCode = r["LicenseCode"] as int?,
                                DiskExpiryDate = (DateTime)r["DiskExpiryDate"],
                                VehicleStatus = r["VehicleStatus"] as string,
                                Archived = (bool)r["Archived"]
                            });
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load vehicles.", ex);
            }

            return View(vehicles);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AddStudent(string firstName, string lastName, string phone, string email,
                                       string address, string userName, string password, string confirmPassword, string idNumber,
                                       string licenceStatus, HttpPostedFileBase licenceDocument, HttpPostedFileBase idDocument)
        {
            var id = SaIdNumber.Parse(idNumber);
            licenceStatus = Licence.Normalize(licenceStatus);
            firstName = Clean(firstName); lastName = Clean(lastName); email = Clean(email);
            address = Clean(address); userName = Clean(userName); password = Clean(password);
            confirmPassword = Clean(confirmPassword);
            string phoneClean = Regex.Replace(phone ?? "", @"[\s\-\(\)]", "");

            var form = new Dictionary<string, string>
            {
                { "firstName", firstName }, { "lastName", lastName }, { "phone", Clean(phone) },
                { "email", email }, { "address", address }, { "userName", userName }, { "idNumber", id.Number }, { "licenceStatus", licenceStatus }
            };

            string error = null;
            if (firstName == "" || lastName == "" || phoneClean == "" || email == "" || userName == "" || password == "")
                error = "Please fill in all required fields.";
            else if (firstName.Length > 50 || lastName.Length > 50) error = "The name or surname is too long.";
            else if (!Regex.IsMatch(phoneClean, @"^\+?\d{10,13}$")) error = "Enter a valid phone number (10 to 13 digits).";
            else if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$") || email.Length > 50) error = "Enter a valid email address (50 characters at most).";
            else if (address.Length > 50) error = "The address is too long (50 characters at most).";
            else if (userName.Length < 3 || userName.Length > 50 || Regex.IsMatch(userName, @"\s"))
                error = "The username must be 3 to 50 characters with no spaces.";
            else if (password.Length < 6 || password.Length > 50) error = "The password must be 6 to 50 characters.";
            else if (password != confirmPassword) error = "The passwords do not match.";
            else if (!id.IsValid) error = id.Error;
            else if (!id.LearnerLight) error = "The student must be at least " + SaIdNumber.LearnerLightAge + " (learner's licence age). " + id.EligibilityText;
            else error = Licence.StatusError(licenceStatus, id);
            byte[] pdf = null;
            string pdfName = null;
            byte[] idPdf = null;
            string idPdfName = null;
            if (error == null) error = Licence.ReadPdf(idDocument, out idPdf, out idPdfName, "the student's ID document");
            if (error == null && Licence.NeedsDocument(licenceStatus)) error = Licence.ReadPdf(licenceDocument, out pdf, out pdfName);
            if (error != null) return Reject("Students", "student", error, form);

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();

                    using (var chk = new SqlCommand(
                        "SELECT COUNT(*) FROM dbo.Student1 WHERE StudentEmailAdress = @e OR StudentUserName = @u", con))
                    {
                        chk.Parameters.AddWithValue("@e", email);
                        chk.Parameters.AddWithValue("@u", userName);
                        if ((int)chk.ExecuteScalar() > 0)
                            return Reject("Students", "student", "A student with this email or username already exists.", form);
                    }
                    SaIdNumber.EnsureColumn(con);
                    Licence.EnsureColumns(con);
                    StudentApproval.EnsureColumns(con);
                    if (SaIdNumber.InUse(con, id.Number, 0))
                        return Reject("Students", "student", "A student with this ID number already exists.", form);

                    using (var tx = con.BeginTransaction())
                    {
                        int newId = Db.InsertRow(con, tx, "Student1", "StudentID",
                            "StudentFirstName, StudentLastName, StudentPhoneNo, StudentEmailAdress, StudentAdress, StudentUserName, StudentPassWord, [Status], IdNumber, LicenceStatus",
                            "@n, @s, @p, @e, @a, @u, @pw, 'Active', @idn, @lic", c =>
                            {
                                c.Parameters.AddWithValue("@n", firstName);
                                c.Parameters.AddWithValue("@s", lastName);
                                c.Parameters.AddWithValue("@p", phoneClean);
                                c.Parameters.AddWithValue("@e", email);
                                c.Parameters.AddWithValue("@a", address);
                                c.Parameters.AddWithValue("@u", userName);
                                c.Parameters.AddWithValue("@pw", password);
                                c.Parameters.AddWithValue("@idn", id.Number);
                                c.Parameters.AddWithValue("@lic", licenceStatus);
                            });
                        Licence.SaveIdDocument(con, tx, newId, idPdfName, idPdf);
                        if (pdf != null) Licence.SaveDocument(con, tx, newId, licenceStatus, pdfName, pdf);
                        StudentApproval.MarkApproved(con, tx, newId, Convert.ToInt32(Session["UserId"]));   // registered in person
                        tx.Commit();
                    }
                }
            }
            catch (SqlException ex)
            {
                return Reject("Students", "student", Db.Friendly("Could not save the student.", ex), form);
            }

            TempData["Success"] = firstName + " " + lastName + " has been registered. They can log in with their username or email.";
            return RedirectToAction("Students");
        }

        // Book a lesson for any learner (same rules as the manager: see Infrastructure/StaffBooking.cs)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AddBooking(int? studentId, int? instructorId, int? packageId, int? tsId, string bookingDate)
        {
            var form = new Dictionary<string, string>
            {
                { "studentId", studentId.HasValue ? studentId.Value.ToString() : "" },
                { "instructorId", instructorId.HasValue ? instructorId.Value.ToString() : "" },
                { "packageId", packageId.HasValue ? packageId.Value.ToString() : "" },
                { "tsId", tsId.HasValue ? tsId.Value.ToString() : "" },
                { "bookingDate", Clean(bookingDate) }
            };

            string who, error;
            DateTime date;
            try
            {
                error = StaffBooking.Create(Convert.ToInt32(Session["UserId"]), studentId, instructorId, packageId, tsId, bookingDate,
                                           out who, out date);
            }
            catch (SqlException ex)
            {
                return Reject("Bookings", "booking", Db.Friendly("Could not save the booking.", ex), form);
            }
            if (error != null) return Reject("Bookings", "booking", error, form);

            TempData["Success"] = "Booking saved for " + who + " on " + date.ToString("yyyy/MM/dd") + " as Pending. It becomes Booked once payment is confirmed.";
            return RedirectToAction("Bookings");
        }

        // ---------------- CONFIRM PAYMENT ----------------

        // Reception has checked the bank (EFT) or taken the money at the desk (Cash).
        // ---------------- REPORTS: who paid ----------------

        // Payments report for one month (or a whole year): every confirmed payment, refunds and what is still unpaid
        public ActionResult Reports(int? year, int? month)
        {
            var vm = new ReceptionPaymentsReport
            {
                Year = year ?? DateTime.Today.Year,
                Month = month ?? DateTime.Today.Month
            };
            DateTime from = vm.Month > 0 ? new DateTime(vm.Year, vm.Month, 1) : new DateTime(vm.Year, 1, 1);
            DateTime to = vm.Month > 0 ? from.AddMonths(1) : from.AddYears(1);
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    Cancellation.EnsureColumns(con);
                    using (var cmd = new SqlCommand(
                        @"SELECT pay.PaymentID, pay.PaymentDate, pay.Amount, ISNULL(pay.PaymentStatus, '') AS Method,
                                 b.BookingID, b.BookingDate, ISNULL(b.RefundAmount, 0) AS Refund,
                                 LTRIM(RTRIM(ISNULL(s.StudentFirstName, '') + ' ' + ISNULL(s.StudentLastName, ''))) AS Student,
                                 p.PackageName
                          FROM dbo.PAYMENT11 pay
                          JOIN dbo.BOOKING11 b     ON b.BookingID = pay.BookingID
                          LEFT JOIN dbo.Student1 s ON s.StudentID = b.StudentID
                          LEFT JOIN dbo.PACKAGEN p ON p.PackageID = b.PackageID
                          WHERE pay.PaymentDate >= @from AND pay.PaymentDate < @to
                          ORDER BY pay.PaymentDate DESC", con))
                    {
                        cmd.Parameters.AddWithValue("@from", from);
                        cmd.Parameters.AddWithValue("@to", to);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                vm.Payments.Add(new ReceptionPaymentRow
                                {
                                    PaymentId = Convert.ToInt32(r["PaymentID"]),
                                    PaidOn = Convert.ToDateTime(r["PaymentDate"]),
                                    Amount = r["Amount"] == DBNull.Value ? 0m : Convert.ToDecimal(r["Amount"]),
                                    Method = Convert.ToString(r["Method"]).Trim(),
                                    BookingId = Convert.ToInt32(r["BookingID"]),
                                    LessonDate = Convert.ToDateTime(r["BookingDate"]),
                                    Refund = Convert.ToDecimal(r["Refund"]),
                                    Student = Convert.ToString(r["Student"]),
                                    Package = Convert.ToString(r["PackageName"])
                                });
                            }
                        }
                    }

                    // Upcoming lessons that are still not paid
                    using (var cmd = new SqlCommand(
                        @"SELECT COUNT(*), ISNULL(SUM(p.PackagePrice), 0)
                          FROM dbo.BOOKING11 b LEFT JOIN dbo.PACKAGEN p ON p.PackageID = b.PackageID
                          WHERE ISNULL(b.BookingStatus, '') NOT IN ('Cancelled', 'Completed')
                            AND b.BookingDate >= CAST(GETDATE() AS date)
                            AND NOT EXISTS (SELECT 1 FROM dbo.PAYMENT11 x WHERE x.BookingID = b.BookingID)", con))
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            vm.UnpaidCount = Convert.ToInt32(r[0]);
                            vm.UnpaidAmount = Convert.ToDecimal(r[1]);
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load the payments report.", ex);
            }
            return View(vm);
        }

        // ---------------- STUDENT APPROVAL ----------------

        // The receptionist approves a student who has verified their account
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ApproveStudent(int id)
        {
            string error;
            try { error = StudentApproval.Decide(id, true, Convert.ToInt32(Session["UserId"]), null); }
            catch (SqlException ex) { error = Db.Friendly("Could not approve the student.", ex); }
            if (error != null) TempData["Error"] = error;
            else TempData["Success"] = "Student " + id + " is approved and can now book and pay.";
            return RedirectToAction("Students");
        }

        // The receptionist does not approve a student (a reason is required; the student sees it)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DisapproveStudent(int id, string reason)
        {
            string error;
            try { error = StudentApproval.Decide(id, false, Convert.ToInt32(Session["UserId"]), reason); }
            catch (SqlException ex) { error = Db.Friendly("Could not update the student.", ex); }
            if (error != null) TempData["Error"] = error;
            else TempData["Success"] = "Student " + id + " is marked as not approved. They will see your reason when they log in.";
            return RedirectToAction("Students");
        }

        // (Payments are approved by the bank when the student pays, so reception no longer confirms them.)

        // ---------------- HELPERS ----------------

        private static string Clean(string s) { return (s ?? "").Trim(); }

        // Keeps what the receptionist typed (never the password) so the form can be shown again after an error
        private void LoadFormState()
        {
            ViewBag.Form = TempData["RecForm"] as Dictionary<string, string> ?? new Dictionary<string, string>();
            ViewBag.ModalError = TempData["RecError"];
            ViewBag.OpenModal = TempData["RecOpen"] as string;
        }

        private ActionResult Reject(string action, string modal, string error, Dictionary<string, string> form)
        {
            TempData["RecError"] = error;
            TempData["RecForm"] = form;
            TempData["RecOpen"] = modal;
            return RedirectToAction(action);
        }

        // NOTE: there is intentionally NO AddStaff / EditStaff / AddInstructor / AddVehicle action here.
    }
}