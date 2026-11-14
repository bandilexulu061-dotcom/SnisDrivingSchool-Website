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
    // Only managers can open these pages (see Infrastructure/RoleRequiredAttribute.cs)
    [RoleRequired("Manager")]
    public partial class ManagerController : Controller
    {
        // ---------------- HELPERS ----------------

        private static string Clean(string s) { return (s ?? "").Trim(); }

        private static bool Has(SqlDataReader r, string name)
        {
            for (int i = 0; i < r.FieldCount; i++)
                if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Get(SqlDataReader r, string name)
        {
            if (!Has(r, name)) return "";
            object v = r[name];
            return v == DBNull.Value ? "" : Convert.ToString(v).Trim();
        }

        private static bool IsEmail(string s) { return Regex.IsMatch(s, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"); }
        private static string CleanPhone(string s) { return Regex.Replace(s ?? "", @"[\s\-\(\)]", ""); }
        private static bool IsPhone(string s) { return Regex.IsMatch(s, @"^\+?\d{10,13}$"); }

        private static int Count(SqlConnection con, string sql, Action<SqlCommand> addParams)
        {
            using (var cmd = new SqlCommand(sql, con))
            {
                addParams(cmd);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        private static bool ColumnExists(SqlConnection con, string table, string column)
        {
            using (var cmd = new SqlCommand("SELECT COL_LENGTH(@t, @c)", con))
            {
                cmd.Parameters.AddWithValue("@t", "dbo." + table);
                cmd.Parameters.AddWithValue("@c", column);
                object o = cmd.ExecuteScalar();
                return o != null && o != DBNull.Value;
            }
        }

        // Finds the real column names of dbo.Vehicle3 so the code works whatever they are spelled
        private static Dictionary<string, string> VehicleCols(SqlConnection con, out string missing, out string found)
        {
            var all = new List<string>();
            using (var cmd = new SqlCommand("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Vehicle3')", con))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) all.Add(r.GetString(0));

            Func<string[], string> pick = names =>
            {
                foreach (var n in names)
                    foreach (var a in all)
                        if (string.Equals(a, n, StringComparison.OrdinalIgnoreCase)) return a;
                return null;
            };

            var map = new Dictionary<string, string>
            {
                { "reg",   pick(new[] { "RegistrationNo", "RegistractionNo", "RegistrationNumber", "RegNo" }) },
                { "model", pick(new[] { "Model" }) },
                { "lic",   pick(new[] { "License-Code", "LicenseCode", "License_Code", "Licence-Code" }) },
                { "disk",  pick(new[] { "Disk-ExpiryDate", "DiskExpiryDate", "Disk_ExpiryDate" }) },
                { "st",    pick(new[] { "Vehicle-Status", "VehicleStatus", "Vehicle_Status", "Status" }) },
                { "arch",  pick(new[] { "Archived" }) }    // optional
            };

            found = string.Join(", ", all);
            missing = null;
            foreach (var k in new[] { "reg", "model", "lic", "disk", "st" })
                if (map[k] == null) { missing = k; break; }
            return map;
        }

        // Keeps what the manager typed (never the password) so the form can be shown again after an error
        private void LoadFormState()
        {
            ViewBag.Form = TempData["MgrForm"] as Dictionary<string, string> ?? new Dictionary<string, string>();
            ViewBag.ModalError = TempData["MgrError"];
            ViewBag.OpenModal = TempData["MgrOpen"] != null;
            ViewBag.OpenModalId = TempData["MgrModalId"] as string;
        }

        private ActionResult Reject(string action, string error, Dictionary<string, string> form, string modalId = null)
        {
            TempData["MgrError"] = error;
            TempData["MgrForm"] = form;
            TempData["MgrOpen"] = true;
            TempData["MgrModalId"] = modalId;
            return RedirectToAction(action);
        }

        // Errors from the Edit pop-ups are shown at the top of the page
        private ActionResult EditFailed(string action, string error)
        {
            TempData["Error"] = error;
            return RedirectToAction(action);
        }

        // ---------------- PAGES ----------------

        // Manager home: one tile per area, each with a live number
        public ActionResult Index()
        {
            var home = new MgrHome();
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    DateTime monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
                    using (var cmd = new SqlCommand(@"
                        SELECT
                          (SELECT COUNT(*) FROM dbo.BOOKING11) AS Bookings,
                          (SELECT COUNT(*) FROM dbo.BOOKING11 WHERE BookingDate >= @mon AND BookingDate < DATEADD(DAY, 7, @mon)
                                                               AND ISNULL(BookingStatus, '') <> 'Cancelled') AS ThisWeek,
                          (SELECT COUNT(*) FROM dbo.BOOKING11 b WHERE ISNULL(b.BookingStatus, '') NOT IN ('Cancelled', 'Completed')
                                AND b.BookingDate >= CAST(GETDATE() AS date)
                                AND NOT EXISTS (SELECT 1 FROM dbo.PAYMENT11 x WHERE x.BookingID = b.BookingID)) AS ToConfirm,
                          (SELECT COUNT(*) FROM dbo.Student1) AS Students,
                          (SELECT COUNT(*) FROM dbo.AdminStaff11) AS Office,
                          (SELECT COUNT(*) FROM dbo.INSTRUCTORNEW) AS Instructors,
                          (SELECT COUNT(*) FROM dbo.INSTRUCTORNEW WHERE [Status] LIKE 'Pending%') AS PendingInstructors", con))
                    {
                        cmd.Parameters.AddWithValue("@mon", monday);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                home.Bookings = Convert.ToInt32(r["Bookings"]);
                                home.ThisWeek = Convert.ToInt32(r["ThisWeek"]);
                                home.ToConfirm = Convert.ToInt32(r["ToConfirm"]);
                                home.Students = Convert.ToInt32(r["Students"]);
                                home.Office = Convert.ToInt32(r["Office"]);
                                home.Instructors = Convert.ToInt32(r["Instructors"]);
                                home.PendingInstructors = Convert.ToInt32(r["PendingInstructors"]);
                            }
                        }
                    }
                    var vehicles = LoadVehicles(con);
                    home.Vehicles = vehicles.Count;
                    home.DisksExpired = vehicles.Count(v => v.DiskExpiry.HasValue && v.DiskExpiry.Value.Date < DateTime.Today);
                    home.DisksDue = vehicles.Count(v => v.DiskExpiry.HasValue && v.DiskExpiry.Value.Date >= DateTime.Today
                                                        && v.DiskExpiry.Value.Date <= DateTime.Today.AddDays(30));
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load the dashboard numbers.", ex);
            }
            ViewBag.Name = Session["UserName"] as string;
            return View(home);
        }

        // ---------------- BOOKINGS (view all) ----------------

        public ActionResult Bookings()
        {
            LoadFormState();
            var list = new List<MgrBookingRow>();
            const string sql = @"
                SELECT b.BookingID,
                       COALESCE(NULLIF(LTRIM(RTRIM(b.StudentFirstName)), ''), s.StudentFirstName) AS FirstName,
                       COALESCE(NULLIF(LTRIM(RTRIM(b.StudentLastName)), ''),  s.StudentLastName)  AS LastName,
                       p.PackageName,
                       i.Name AS InsName, i.Surname AS InsSurname,
                       b.BookingDate,
                       CONVERT(varchar(5), t.StartTime, 108) AS StartTime,
                       CASE WHEN b.BookingStatus = 'Cancelled' AND b.CancelledBy IS NOT NULL
                            THEN 'Cancelled by ' + LOWER(b.CancelledBy) ELSE b.BookingStatus END AS BookingStatus,
                       b.CancelReason,
                       (SELECT pi.Name + ' ' + pi.Surname FROM dbo.INSTRUCTORNEW pi WHERE pi.InstructorID = b.ProposedInstructorID) AS ProposedName,
                       CASE WHEN b.RefundedOn IS NOT NULL THEN 'Refunded'
                            WHEN pay.PaymentID IS NOT NULL THEN 'PAID'
                            ELSE ISNULL(NULLIF(LTRIM(RTRIM(b.PaymentStatus)), ''), 'Pending') END AS PayStatus
                FROM dbo.BOOKING11 b
                LEFT JOIN dbo.Student1 s       ON s.StudentID    = b.StudentID
                LEFT JOIN dbo.PACKAGEN p       ON p.PackageID     = b.PackageID
                LEFT JOIN dbo.TimeSlotNew t    ON t.TSID          = b.TSID
                LEFT JOIN dbo.INSTRUCTORNEW i  ON i.InstructorID  = b.InstructorID
                OUTER APPLY (SELECT TOP 1 x.PaymentID FROM dbo.PAYMENT11 x
                             WHERE x.BookingID = b.BookingID ORDER BY x.PaymentID DESC) pay
                ORDER BY b.BookingID DESC";
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                using (var cmd = new SqlCommand(sql, con))
                {
                    con.Open();
                    Cancellation.EnsureColumns(con);
                    Replacement.EnsureColumns(con);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            list.Add(new MgrBookingRow
                            {
                                BookingId = Convert.ToInt32(r["BookingID"]),
                                Student = (Get(r, "FirstName") + " " + Get(r, "LastName")).Trim(),
                                Package = Get(r, "PackageName"),
                                Instructor = (Get(r, "InsName") + " " + Get(r, "InsSurname")).Trim(),
                                Date = Convert.ToDateTime(r["BookingDate"]),
                                Time = Get(r, "StartTime"),
                                Status = Get(r, "BookingStatus"),
                                CancelReason = Get(r, "CancelReason"),
                                ProposedName = Get(r, "ProposedName"),
                                Payment = Get(r, "PayStatus")
                            });
                        }
                    }
                    bool reopen = (ViewBag.OpenModalId as string) == "staffBookingDialog";
                    ViewBag.BookingForm = new StaffBookingFormVm
                    {
                        Controller = "Manager",
                        Lists = StaffBooking.LoadLists(con),
                        Form = ViewBag.Form as Dictionary<string, string>,
                        Error = reopen ? ViewBag.ModalError as string : null,
                        Open = reopen
                    };
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load the bookings.", ex);
            }
            return View(list);
        }

        // Book a lesson for any learner (same rules as reception: see Infrastructure/StaffBooking.cs)
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
                return Reject("Bookings", Db.Friendly("Could not save the booking.", ex), form, "staffBookingDialog");
            }
            if (error != null) return Reject("Bookings", error, form, "staffBookingDialog");

            TempData["Success"] = "Booking saved for " + who + " on " + date.ToString("yyyy/MM/dd") + " as Pending. It becomes Booked once payment is confirmed.";
            return RedirectToAction("Bookings");
        }

        // ---------------- STUDENTS ----------------

        public ActionResult Students()
        {
            LoadFormState();
            var list = new List<MgrStudentRow>();
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                using (var cmd = new SqlCommand("SELECT StudentID, StudentFirstName, StudentLastName, StudentPhoneNo, StudentEmailAdress, StudentAdress, StudentUserName, Status, ISNULL(IdNumber, '') AS IdNumber, ISNULL(LicenceStatus, '') AS LicenceStatus, " + Licence.CurrentDocSql + " AS DocID, " + Licence.CurrentIdDocSql + " AS IdDocID, " + StudentApproval.StageSql + " AS Stage, (SELECT COUNT(*) FROM dbo.BOOKING11 bk WHERE bk.StudentID = s.StudentID) AS BookingCount FROM dbo.Student1 s ORDER BY StudentID DESC", con))
                {
                    con.Open();
                    SaIdNumber.EnsureColumn(con);
                    Licence.EnsureColumns(con);
                    StudentApproval.EnsureColumns(con);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            list.Add(new MgrStudentRow
                            {
                                StudentId = Convert.ToInt32(r["StudentID"]),
                                UserName = Get(r, "StudentUserName"),
                                Name = Get(r, "StudentFirstName"),
                                Surname = Get(r, "StudentLastName"),
                                Phone = Get(r, "StudentPhoneNo"),
                                Email = Get(r, "StudentEmailAdress"),
                                Address = Get(r, "StudentAdress"),
                                Status = Get(r, "Status"),
                                IdNumber = Get(r, "IdNumber"),
                                LicenceStatus = Get(r, "LicenceStatus"),
                                DocumentId = r["DocID"] == DBNull.Value ? 0 : Convert.ToInt32(r["DocID"]),
                                IdDocumentId = r["IdDocID"] == DBNull.Value ? 0 : Convert.ToInt32(r["IdDocID"]),
                                Stage = Get(r, "Stage"),
                                BookingCount = Convert.ToInt32(r["BookingCount"])
                            });
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load the students.", ex);
            }
            return View(list);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AddStudent(string name, string surname, string userName, string phone,
                                       string email, string address, string password, string confirmPassword, string idNumber,
                                       string licenceStatus, HttpPostedFileBase licenceDocument, HttpPostedFileBase idDocument)
        {
            var id = SaIdNumber.Parse(idNumber);
            licenceStatus = Licence.Normalize(licenceStatus);
            name = Clean(name); surname = Clean(surname); userName = Clean(userName);
            email = Clean(email); address = Clean(address); password = Clean(password); confirmPassword = Clean(confirmPassword);
            string phoneClean = CleanPhone(phone);

            var form = new Dictionary<string, string>
            {
                { "name", name }, { "surname", surname }, { "userName", userName },
                { "phone", Clean(phone) }, { "email", email }, { "address", address }, { "idNumber", id.Number }, { "licenceStatus", licenceStatus }
            };

            string error = null;
            if (name == "" || surname == "" || userName == "" || phoneClean == "" || email == "" || password == "")
                error = "Please fill in all required fields.";
            else if (name.Length > 50 || surname.Length > 50) error = "The name or surname is too long.";
            else if (userName.Length < 3 || userName.Length > 50 || Regex.IsMatch(userName, @"\s"))
                error = "The username must be 3 to 50 characters with no spaces.";
            else if (!IsPhone(phoneClean)) error = "Enter a valid phone number (10 to 13 digits).";
            else if (!IsEmail(email) || email.Length > 50) error = "Enter a valid email address (50 characters at most).";
            else if (address.Length > 50) error = "The address is too long (50 characters at most).";
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
            if (error != null) return Reject("Students", error, form);

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();

                    if (Count(con, "SELECT COUNT(*) FROM dbo.Student1 WHERE StudentEmailAdress = @e OR StudentUserName = @u",
                              c => { c.Parameters.AddWithValue("@e", email); c.Parameters.AddWithValue("@u", userName); }) > 0)
                        return Reject("Students", "A student with this email or username already exists.", form);
                    SaIdNumber.EnsureColumn(con);
                    Licence.EnsureColumns(con);
                    StudentApproval.EnsureColumns(con);
                    if (SaIdNumber.InUse(con, id.Number, 0))
                        return Reject("Students", "A student with this ID number already exists.", form);

                    const string cols = "StudentFirstName, StudentLastName, StudentPhoneNo, StudentEmailAdress, StudentAdress, StudentUserName, StudentPassWord, [Status], IdNumber, LicenceStatus";
                    const string vals = "@n, @s, @p, @e, @a, @u, @pw, 'Active', @idn, @lic";

                    using (var tx = con.BeginTransaction())
                    {
                        int newId = Db.InsertRow(con, tx, "Student1", "StudentID", cols, vals, c =>
                        {
                            c.Parameters.AddWithValue("@u", userName);
                            c.Parameters.AddWithValue("@n", name);
                            c.Parameters.AddWithValue("@s", surname);
                            c.Parameters.AddWithValue("@p", phoneClean);
                            c.Parameters.AddWithValue("@e", email);
                            c.Parameters.AddWithValue("@a", address);
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
                return Reject("Students", Db.Friendly("Could not save the student.", ex), form);
            }

            TempData["Success"] = name + " " + surname + " has been registered. They can log in with their username or email.";
            return RedirectToAction("Students");
        }

        // ---------------- STAFF (receptionists) ----------------

        public ActionResult Staff()
        {
            LoadFormState();
            var page = new MgrStaffPage();
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    using (var cmd = new SqlCommand(
                        @"SELECT AdminStaffID, Name, Surname, AdminStaffRole, AdminStaffNo, AdminStaffEmail, UserName, OfficeNo
                          FROM dbo.AdminStaff11 ORDER BY AdminStaffID", con))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            page.Admin.Add(new MgrStaffRow
                            {
                                StaffId = Convert.ToInt32(r["AdminStaffID"]),
                                Name = Get(r, "Name"),
                                Surname = Get(r, "Surname"),
                                Role = Get(r, "AdminStaffRole").StartsWith("Rec", StringComparison.OrdinalIgnoreCase)
                                       ? "Receptionist" : Get(r, "AdminStaffRole"),
                                Phone = Get(r, "AdminStaffNo"),
                                Email = Get(r, "AdminStaffEmail"),
                                UserName = Get(r, "UserName"),
                                OfficeNo = Get(r, "OfficeNo")
                            });
                        }
                    }
                    using (var cmd = new SqlCommand(
                        @"SELECT InstructorID, Name, Surname, PhoneNo, Email, UserName, LicenseCode, YearsOfExperience, [Status]
                          FROM dbo.INSTRUCTORNEW ORDER BY InstructorID DESC", con))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            page.Instructors.Add(new MgrInstructorRow
                            {
                                InstructorId = Convert.ToInt32(r["InstructorID"]),
                                Name = Get(r, "Name"),
                                Surname = Get(r, "Surname"),
                                Phone = Get(r, "PhoneNo"),
                                Email = Get(r, "Email"),
                                UserName = Get(r, "UserName"),
                                LicenseCode = Get(r, "LicenseCode"),
                                Years = Get(r, "YearsOfExperience"),
                                Status = Get(r, "Status")
                            });
                        }
                    }
                    page.Leave = InstructorLeave.Upcoming(con);
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load the staff.", ex);
            }
            return View(page);
        }

        // ---------------- DELETE A STUDENT ----------------

        // Removes a student and their documents. Students with paid bookings are kept, so payment records stay complete;
        // their unpaid / cancelled bookings are removed with them.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DeleteStudent(int id)
        {
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    Lessons.EnsureTables(con);
                    Licence.EnsureColumns(con);
                    string name = null;
                    using (var cmd = new SqlCommand("SELECT StudentFirstName + ' ' + StudentLastName FROM dbo.Student1 WHERE StudentID = @s", con))
                    {
                        cmd.Parameters.AddWithValue("@s", id);
                        object o = cmd.ExecuteScalar();
                        if (o == null) { TempData["Error"] = "That student could not be found."; return RedirectToAction("Students"); }
                        name = Convert.ToString(o).Trim();
                    }
                    int paid = Count(con, @"SELECT COUNT(*) FROM dbo.PAYMENT11 p JOIN dbo.BOOKING11 b ON b.BookingID = p.BookingID WHERE b.StudentID = @s",
                                     c => c.Parameters.AddWithValue("@s", id));
                    if (paid > 0)
                    {
                        TempData["Error"] = name + " can't be deleted: they have " + paid + " paid booking" + (paid == 1 ? "" : "s")
                                          + ", and payment records must be kept. You can set them as not approved instead.";
                        return RedirectToAction("Students");
                    }
                    using (var tx = con.BeginTransaction())
                    {
                        foreach (var sql in new[]
                        {
                            "DELETE FROM dbo.LessonReviews WHERE StudentID = @s",
                            "DELETE FROM dbo.StudentDocuments WHERE StudentID = @s",
                            "DELETE FROM dbo.BOOKING11 WHERE StudentID = @s",
                            "DELETE FROM dbo.Student1 WHERE StudentID = @s"
                        })
                        {
                            using (var cmd = new SqlCommand(sql, con, tx))
                            {
                                cmd.Parameters.AddWithValue("@s", id);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        tx.Commit();
                    }
                    TempData["Success"] = name + " (student " + id + ") has been deleted.";
                }
            }
            catch (SqlException ex)
            {
                TempData["Error"] = Db.Friendly("Could not delete the student.", ex);
            }
            return RedirectToAction("Students");
        }

        // ---------------- INSTRUCTOR LEAVE ----------------

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

        // Receptionist / manager: change phone, email or office number
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult EditStaff(int id, string phone, string email, string officeNo)
        {
            email = Clean(email); officeNo = Clean(officeNo);
            string phoneClean = CleanPhone(phone);
            if (!IsPhone(phoneClean)) return EditFailed("Staff", "Enter a valid phone number (10 to 13 digits).");
            if (!IsEmail(email) || email.Length > 100) return EditFailed("Staff", "Enter a valid email address.");
            if (officeNo.Length > 10) return EditFailed("Staff", "The office number is too long (10 characters at most).");

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    if (Count(con, "SELECT COUNT(*) FROM dbo.AdminStaff11 WHERE AdminStaffEmail = @e AND AdminStaffID <> @id",
                              c => { c.Parameters.AddWithValue("@e", email); c.Parameters.AddWithValue("@id", id); }) > 0)
                        return EditFailed("Staff", "Another staff member already uses that email.");

                    using (var cmd = new SqlCommand(
                        "UPDATE dbo.AdminStaff11 SET AdminStaffNo = @p, AdminStaffEmail = @e, OfficeNo = @o WHERE AdminStaffID = @id", con))
                    {
                        cmd.Parameters.AddWithValue("@p", phoneClean);
                        cmd.Parameters.AddWithValue("@e", email);
                        cmd.Parameters.AddWithValue("@o", officeNo);
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (SqlException ex)
            {
                return EditFailed("Staff", Db.Friendly("Could not update the staff member.", ex));
            }
            TempData["Success"] = "Staff member " + id + " has been updated.";
            return RedirectToAction("Staff");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AddStaff(string name, string surname, string phone, string email,
                                     string userName, string password, string officeNo, string confirmPassword)
        {
            name = Clean(name); surname = Clean(surname); email = Clean(email);
            userName = Clean(userName); password = Clean(password); confirmPassword = Clean(confirmPassword); officeNo = Clean(officeNo);
            string phoneClean = CleanPhone(phone);

            var form = new Dictionary<string, string>
            {
                { "name", name }, { "surname", surname }, { "phone", Clean(phone) },
                { "email", email }, { "userName", userName }, { "officeNo", officeNo }
            };

            string error = null;
            if (name == "" || surname == "" || phoneClean == "" || email == "" || userName == "" || password == "")
                error = "Please fill in all required fields.";
            else if (name.Length > 50 || surname.Length > 50) error = "The name or surname is too long.";
            else if (!IsPhone(phoneClean)) error = "Enter a valid phone number (10 to 13 digits).";
            else if (!IsEmail(email) || email.Length > 100) error = "Enter a valid email address.";
            else if (userName.Length < 3 || userName.Length > 50 || Regex.IsMatch(userName, @"\s"))
                error = "The username must be 3 to 50 characters with no spaces.";
            else if (officeNo.Length > 10) error = "The office number is too long (10 characters at most).";
            else if (password.Length < 6 || password.Length > 50) error = "The password must be 6 to 50 characters.";
            else if (password != confirmPassword) error = "The passwords do not match.";
            if (error != null) return Reject("Staff", error, form, "addStaffModal");

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();

                    if (Count(con, "SELECT COUNT(*) FROM dbo.AdminStaff11 WHERE AdminStaffEmail = @e OR UserName = @u",
                              c => { c.Parameters.AddWithValue("@e", email); c.Parameters.AddWithValue("@u", userName); }) > 0)
                        return Reject("Staff", "A staff member with this email or username already exists.", form, "addStaffModal");

                    using (var tx = con.BeginTransaction())
                    {
                        Db.InsertRow(con, tx, "AdminStaff11", "AdminStaffID",
                            "Name, Surname, AdminStaffRole, AdminStaffNo, AdminStaffEmail, UserName, [Password], OfficeNo",
                            "@n, @s, 'Receptionist', @p, @e, @u, @pw, @o", c =>
                            {
                                c.Parameters.AddWithValue("@n", name);
                                c.Parameters.AddWithValue("@s", surname);
                                c.Parameters.AddWithValue("@p", phoneClean);
                                c.Parameters.AddWithValue("@e", email);
                                c.Parameters.AddWithValue("@u", userName);
                                c.Parameters.AddWithValue("@pw", password);
                                c.Parameters.AddWithValue("@o", officeNo);
                            });
                        tx.Commit();
                    }
                }
            }
            catch (SqlException ex)
            {
                return Reject("Staff", Db.Friendly("Could not save the receptionist.", ex), form, "addStaffModal");
            }

            TempData["Success"] = name + " " + surname + " has been registered as a receptionist. They can log in with their username or email.";
            return RedirectToAction("Staff");
        }

        // ---------------- INSTRUCTORS ----------------

        // Instructors are now listed on the Staff page
        public ActionResult Instructors()
        {
            return RedirectToAction("Staff");
        }

        // Instructor: change contact details, licence code, experience and status (Approved / Pending / Rejected)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult EditInstructor(int id, string phone, string email, string licenseCode, int? years, string status)
        {
            email = Clean(email); licenseCode = Clean(licenseCode); status = Clean(status);
            string phoneClean = CleanPhone(phone);
            if (!IsPhone(phoneClean)) return EditFailed("Staff", "Enter a valid phone number (10 to 13 digits).");
            if (!IsEmail(email) || email.Length > 100) return EditFailed("Staff", "Enter a valid email address.");
            if (licenseCode == "" || licenseCode.Length > 50) return EditFailed("Staff", "Enter the licence code.");
            if (!years.HasValue || years.Value < 0 || years.Value > 60) return EditFailed("Staff", "Years of experience must be between 0 and 60.");
            if (status == "Pending") status = "Pending Approval";   // same wording as instructor sign-up
            if (status != "Approved" && status != "Pending Approval" && status != "Rejected") return EditFailed("Staff", "Choose a status.");

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    if (Count(con, "SELECT COUNT(*) FROM dbo.INSTRUCTORNEW WHERE Email = @e AND InstructorID <> @id",
                              c => { c.Parameters.AddWithValue("@e", email); c.Parameters.AddWithValue("@id", id); }) > 0)
                        return EditFailed("Staff", "Another instructor already uses that email.");

                    using (var cmd = new SqlCommand(
                        @"UPDATE dbo.INSTRUCTORNEW SET PhoneNo = @p, Email = @e, LicenseCode = @lc, YearsOfExperience = @y, [Status] = @st
                          WHERE InstructorID = @id", con))
                    {
                        cmd.Parameters.AddWithValue("@p", phoneClean);
                        cmd.Parameters.AddWithValue("@e", email);
                        cmd.Parameters.AddWithValue("@lc", licenseCode);
                        cmd.Parameters.AddWithValue("@y", years.Value);
                        cmd.Parameters.AddWithValue("@st", status);
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (SqlException ex)
            {
                return EditFailed("Staff", Db.Friendly("Could not update the instructor.", ex));
            }
            TempData["Success"] = "Instructor " + id + " has been updated (" + status + ").";
            return RedirectToAction("Staff");
        }

        // Quick Approve / Reject buttons for instructors who registered themselves
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult SetInstructorStatus(int id, string status)
        {
            status = Clean(status);
            if (status != "Approved" && status != "Rejected") return EditFailed("Staff", "Unknown status.");
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                using (var cmd = new SqlCommand("UPDATE dbo.INSTRUCTORNEW SET [Status] = @st WHERE InstructorID = @id", con))
                {
                    cmd.Parameters.AddWithValue("@st", status);
                    cmd.Parameters.AddWithValue("@id", id);
                    con.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (SqlException ex)
            {
                return EditFailed("Staff", Db.Friendly("Could not change the instructor's status.", ex));
            }
            TempData["Success"] = "Instructor " + id + " is now " + status + ".";
            return RedirectToAction("Staff");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AddInstructor(string name, string surname, string phone, string email, string userName,
                                          string password, string licenseCode, int? years, string confirmPassword)
        {
            name = Clean(name); surname = Clean(surname); email = Clean(email);
            userName = Clean(userName); password = Clean(password); confirmPassword = Clean(confirmPassword); licenseCode = Clean(licenseCode);
            string phoneClean = CleanPhone(phone);

            var form = new Dictionary<string, string>
            {
                { "name", name }, { "surname", surname }, { "phone", Clean(phone) }, { "email", email },
                { "userName", userName }, { "licenseCode", licenseCode },
                { "years", years.HasValue ? years.Value.ToString(CultureInfo.InvariantCulture) : "" }
            };

            string error = null;
            if (name == "" || surname == "" || phoneClean == "" || email == "" || userName == "" || password == ""
                || licenseCode == "" || !years.HasValue)
                error = "Please fill in all required fields.";
            else if (name.Length > 50 || surname.Length > 50) error = "The name or surname is too long.";
            else if (!IsPhone(phoneClean)) error = "Enter a valid phone number (10 to 13 digits).";
            else if (!IsEmail(email) || email.Length > 100) error = "Enter a valid email address.";
            else if (userName.Length < 3 || userName.Length > 50 || Regex.IsMatch(userName, @"\s"))
                error = "The username must be 3 to 50 characters with no spaces.";
            else if (years.Value < 0 || years.Value > 60) error = "Years of experience must be between 0 and 60.";
            else if (licenseCode.Length > 50) error = "The licence code is too long.";
            else if (password.Length < 6 || password.Length > 50) error = "The password must be 6 to 50 characters.";
            else if (password != confirmPassword) error = "The passwords do not match.";
            if (error != null) return Reject("Staff", error, form, "addInstructorModal");

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();

                    if (Count(con, "SELECT COUNT(*) FROM dbo.INSTRUCTORNEW WHERE Email = @e OR UserName = @u",
                              c => { c.Parameters.AddWithValue("@e", email); c.Parameters.AddWithValue("@u", userName); }) > 0)
                        return Reject("Staff", "An instructor with this email or username already exists.", form, "addInstructorModal");

                    using (var tx = con.BeginTransaction())
                    {
                        Db.InsertRow(con, tx, "INSTRUCTORNEW", "InstructorID",
                            "Name, Surname, PhoneNo, Email, UserName, [PassWord], LicenseCode, YearsOfExperience, [Status]",
                            "@n, @s, @p, @e, @u, @pw, @lc, @y, 'Approved'", c =>
                            {
                                c.Parameters.AddWithValue("@n", name);
                                c.Parameters.AddWithValue("@s", surname);
                                c.Parameters.AddWithValue("@p", phoneClean);
                                c.Parameters.AddWithValue("@e", email);
                                c.Parameters.AddWithValue("@u", userName);
                                c.Parameters.AddWithValue("@pw", password);
                                c.Parameters.AddWithValue("@lc", licenseCode);
                                c.Parameters.AddWithValue("@y", years.Value);
                            });
                        tx.Commit();
                    }
                }
            }
            catch (SqlException ex)
            {
                return Reject("Staff", Db.Friendly("Could not save the instructor.", ex), form, "addInstructorModal");
            }

            TempData["Success"] = name + " " + surname + " has been registered as an instructor (Approved). They can log in with their username or email.";
            return RedirectToAction("Staff");
        }

        // ---------------- VEHICLES ----------------

        public ActionResult Vehicles()
        {
            LoadFormState();
            var list = new List<MgrVehicleRow>();
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    string missing, found;
                    var c = VehicleCols(con, out missing, out found);
                    if (missing != null)
                    {
                        ViewBag.Error = "dbo.Vehicle3 has no column matching '" + missing + "'. Columns found: " + found;
                        return View(list);
                    }
                    ViewBag.CanRemove = c["arch"] != null;
                    list = LoadVehicles(con);
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load the vehicles.", ex);
            }
            return View(list);
        }

        // All vehicles that are not archived (empty list if the table's columns can't be matched)
        private static List<MgrVehicleRow> LoadVehicles(SqlConnection con)
        {
            var list = new List<MgrVehicleRow>();
            string missing, found;
            var c = VehicleCols(con, out missing, out found);
            if (missing != null) return list;

            string sql = "SELECT VehicleID, [" + c["reg"] + "] AS RegNo, [" + c["model"] + "] AS Model, ["
                       + c["lic"] + "] AS LicCode, [" + c["disk"] + "] AS DiskExpiry, ["
                       + c["st"] + "] AS VStatus FROM dbo.Vehicle3"
                       + (c["arch"] != null ? " WHERE ISNULL([" + c["arch"] + "], 0) = 0" : "")
                       + " ORDER BY VehicleID DESC";

            using (var cmd = new SqlCommand(sql, con))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    DateTime exp;
                    list.Add(new MgrVehicleRow
                    {
                        VehicleId = Convert.ToInt32(r["VehicleID"]),
                        RegistrationNo = Get(r, "RegNo"),
                        Model = Get(r, "Model"),
                        LicenseCode = Get(r, "LicCode"),
                        DiskExpiry = DateTime.TryParse(Get(r, "DiskExpiry"), out exp) ? (DateTime?)exp : null,
                        Status = Get(r, "VStatus")
                    });
                }
            }
            return list;
        }

        // Change any detail of a vehicle, e.g. a new licence disk expiry date after renewal
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult EditVehicle(int id, string registrationNo, string model, string licenseCode,
                                        string diskExpiry, string status)
        {
            registrationNo = Clean(registrationNo).ToUpperInvariant();
            model = Clean(model); licenseCode = Clean(licenseCode); status = Clean(status);

            DateTime expiry;
            if (registrationNo == "" || model == "" || licenseCode == "" || Clean(diskExpiry) == "" || status == "")
                return EditFailed("Vehicles", "Please fill in all fields.");
            if (!Regex.IsMatch(registrationNo, @"^[A-Z0-9 \-]{3,20}$"))
                return EditFailed("Vehicles", "Enter a valid registration number (letters, numbers and spaces).");
            if (model.Length > 50) return EditFailed("Vehicles", "The model is too long.");
            if (licenseCode != "8" && licenseCode != "10" && licenseCode != "14") return EditFailed("Vehicles", "Choose a licence code.");
            if (status != "Available" && status != "In Use" && status != "Maintenance") return EditFailed("Vehicles", "Choose a vehicle status.");
            if (!DateTime.TryParseExact(Clean(diskExpiry), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out expiry))
                return EditFailed("Vehicles", "Pick the licence disk expiry date.");

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    string missing, found;
                    var c = VehicleCols(con, out missing, out found);
                    if (missing != null)
                        return EditFailed("Vehicles", "dbo.Vehicle3 has no column matching '" + missing + "'.");

                    if (Count(con, "SELECT COUNT(*) FROM dbo.Vehicle3 WHERE [" + c["reg"] + "] = @r AND VehicleID <> @id",
                              x => { x.Parameters.AddWithValue("@r", registrationNo); x.Parameters.AddWithValue("@id", id); }) > 0)
                        return EditFailed("Vehicles", "Another vehicle already has registration " + registrationNo + ".");

                    using (var cmd = new SqlCommand(
                        "UPDATE dbo.Vehicle3 SET [" + c["reg"] + "] = @r, [" + c["model"] + "] = @m, [" + c["lic"] + "] = @lc, ["
                        + c["disk"] + "] = @d, [" + c["st"] + "] = @st WHERE VehicleID = @id", con))
                    {
                        cmd.Parameters.AddWithValue("@r", registrationNo);
                        cmd.Parameters.AddWithValue("@m", model);
                        cmd.Parameters.AddWithValue("@lc", licenseCode);
                        cmd.Parameters.AddWithValue("@d", expiry.Date);
                        cmd.Parameters.AddWithValue("@st", status);
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (SqlException ex)
            {
                return EditFailed("Vehicles", Db.Friendly("Could not update the vehicle.", ex));
            }

            TempData["Success"] = "Vehicle " + registrationNo + " has been updated"
                                + (expiry.Date < DateTime.Today ? " (note: the licence disk is still expired)." : ".");
            return RedirectToAction("Vehicles");
        }

        // Hides a vehicle that the school no longer uses (only when dbo.Vehicle3 has an Archived column)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult RemoveVehicle(int id)
        {
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    string missing, found;
                    var c = VehicleCols(con, out missing, out found);
                    if (c["arch"] == null)
                        return EditFailed("Vehicles", "Vehicles can't be removed because dbo.Vehicle3 has no Archived column.");
                    using (var cmd = new SqlCommand("UPDATE dbo.Vehicle3 SET [" + c["arch"] + "] = 1 WHERE VehicleID = @id", con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (SqlException ex)
            {
                return EditFailed("Vehicles", Db.Friendly("Could not remove the vehicle.", ex));
            }
            TempData["Success"] = "Vehicle " + id + " has been removed from the list.";
            return RedirectToAction("Vehicles");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AddVehicle(string registrationNo, string model, string licenseCode,
                                       string diskExpiry, string status)
        {
            registrationNo = Clean(registrationNo).ToUpperInvariant();
            model = Clean(model); licenseCode = Clean(licenseCode); status = Clean(status);

            var form = new Dictionary<string, string>
            {
                { "registrationNo", registrationNo }, { "model", model }, { "licenseCode", licenseCode },
                { "diskExpiry", Clean(diskExpiry) }, { "status", status }
            };

            string error = SaveNewVehicle(registrationNo, model, licenseCode, diskExpiry, status);
            if (error != null) return Reject("Vehicles", error, form, "addVehicleModal");

            TempData["Success"] = "Vehicle " + registrationNo + " has been added.";
            return RedirectToAction("Vehicles");
        }

        // Checks and saves a new vehicle (used by the manager and by reception). Returns an error, or null.
        internal static string SaveNewVehicle(string registrationNo, string model, string licenseCode, string diskExpiry, string status)
        {
            registrationNo = Clean(registrationNo).ToUpperInvariant();
            model = Clean(model); licenseCode = Clean(licenseCode); status = Clean(status);

            DateTime expiry = DateTime.MinValue;
            string error = null;
            if (registrationNo == "" || model == "" || licenseCode == "" || Clean(diskExpiry) == "" || status == "")
                error = "Please fill in all fields.";
            else if (!Regex.IsMatch(registrationNo, @"^[A-Z0-9 \-]{3,20}$"))
                error = "Enter a valid registration number (letters, numbers and spaces, for example ND 202 KZN).";
            else if (model.Length > 50) error = "The model is too long.";
            else if (licenseCode != "8" && licenseCode != "10" && licenseCode != "14")
                error = "Choose a licence code.";
            else if (status != "Available" && status != "In Use" && status != "Maintenance")
                error = "Choose a vehicle status.";
            else if (!DateTime.TryParseExact(Clean(diskExpiry), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                             DateTimeStyles.None, out expiry))
                error = "Pick the licence disk expiry date.";
            else if (expiry.Date < DateTime.Today)
                error = "The licence disk has already expired. Enter a valid expiry date.";
            if (error != null) return error;

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();

                    string missing, found;
                    var c = VehicleCols(con, out missing, out found);
                    if (missing != null)
                        return "dbo.Vehicle3 has no column matching '" + missing + "'. Columns found: " + found;

                    if (Count(con, "SELECT COUNT(*) FROM dbo.Vehicle3 WHERE [" + c["reg"] + "] = @r",
                              x => x.Parameters.AddWithValue("@r", registrationNo)) > 0)
                        return "A vehicle with this registration number already exists.";

                    string cols = "[" + c["reg"] + "], [" + c["model"] + "], [" + c["lic"] + "], ["
                                + c["disk"] + "], [" + c["st"] + "]" + (c["arch"] != null ? ", [" + c["arch"] + "]" : "");
                    string vals = "@r, @m, @lc, @d, @st" + (c["arch"] != null ? ", 0" : "");

                    using (var tx = con.BeginTransaction())
                    {
                        Db.InsertRow(con, tx, "Vehicle3", "VehicleID", cols, vals, x =>
                        {
                            x.Parameters.AddWithValue("@r", registrationNo);
                            x.Parameters.AddWithValue("@m", model);
                            x.Parameters.AddWithValue("@lc", licenseCode);
                            x.Parameters.AddWithValue("@d", expiry.Date);
                            x.Parameters.AddWithValue("@st", status);
                        });
                        tx.Commit();
                    }
                }
            }
            catch (SqlException ex)
            {
                return Db.Friendly("Could not save the vehicle.", ex);
            }

            return null;
        }
    }
}

namespace SnisDrivingSchool.Models
{
    public class MgrBookingRow
    {
        public int BookingId { get; set; }
        public string Student { get; set; }
        public string Package { get; set; }
        public string Instructor { get; set; }
        public DateTime Date { get; set; }
        public string Time { get; set; }
        public string Status { get; set; }
        public string Payment { get; set; }
        public string CancelReason { get; set; }
        public string ProposedName { get; set; }
    }

    public class MgrStudentRow
    {
        public int StudentId { get; set; }
        public string UserName { get; set; }
        public string Name { get; set; }
        public string Surname { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string Address { get; set; }
        public string Status { get; set; }
        public string IdNumber { get; set; }
        public string LicenceStatus { get; set; }
        public int DocumentId { get; set; }
        public int IdDocumentId { get; set; }
        public string Stage { get; set; }        // Verify / Waiting / Approved / Rejected
        public int BookingCount { get; set; }
    }

    public class MgrStaffRow
    {
        public int StaffId { get; set; }
        public string Name { get; set; }
        public string Surname { get; set; }
        public string Role { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string UserName { get; set; }
        public string OfficeNo { get; set; }
    }

    // Numbers on the manager home page
    public class MgrHome
    {
        public int Bookings { get; set; }
        public int ThisWeek { get; set; }
        public int ToConfirm { get; set; }
        public int Students { get; set; }
        public int Office { get; set; }
        public int Instructors { get; set; }
        public int PendingInstructors { get; set; }
        public int Vehicles { get; set; }
        public int DisksExpired { get; set; }
        public int DisksDue { get; set; }
    }

    // Staff page: office staff and instructors together
    public class MgrStaffPage
    {
        public List<MgrStaffRow> Admin { get; set; }
        public List<MgrInstructorRow> Instructors { get; set; }
        public Dictionary<int, List<SnisDrivingSchool.InstructorLeave.Period>> Leave { get; set; }   // current/upcoming leave per instructor
        public MgrStaffPage()
        {
            Leave = new Dictionary<int, List<SnisDrivingSchool.InstructorLeave.Period>>();
            Admin = new List<MgrStaffRow>();
            Instructors = new List<MgrInstructorRow>();
        }
    }

    public class MgrInstructorRow
    {
        public int InstructorId { get; set; }
        public string Name { get; set; }
        public string Surname { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string UserName { get; set; }
        public string LicenseCode { get; set; }
        public string Years { get; set; }
        public string Status { get; set; }
    }

    public class MgrVehicleRow
    {
        public int VehicleId { get; set; }
        public string RegistrationNo { get; set; }
        public string Model { get; set; }
        public string LicenseCode { get; set; }
        public DateTime? DiskExpiry { get; set; }
        public string Status { get; set; }
    }

    public static class MgrUi
    {
        // CSS class for the coloured status label
        public static string Badge(string status)
        {
            string s = (status ?? "").Trim().ToLowerInvariant();
            if (s == "booked" || s == "paid" || s == "completed" || s == "approved" || s == "available" || s == "active")
                return "m-ok";
            if (s.StartsWith("pending") || s == "in use" || s == "maintenance")
                return "m-warn";
            if (s.StartsWith("cancelled") || s == "rejected" || s == "expired")
                return "m-bad";
            return "m-info";
        }
    }
}