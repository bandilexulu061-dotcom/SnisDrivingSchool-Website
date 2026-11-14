using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Mvc;

namespace SnisDrivingSchool.Controllers
{
    public class AccountController : Controller
    {
        // GET: Account/Login
        [HttpGet]
        public ActionResult Login(string role)
        {
            ViewBag.Role = role;
            return View();
        }

        // POST: Account/Login
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Login(string username, string password)
        {
            username = (username ?? "").Trim();
            password = (password ?? "").Trim();

            if (username == "" || password == "")
            {
                ViewBag.Error = "Please enter your username and password.";
                return View();
            }

            try
            {
                string error;

                // 1. Admin staff (Manager / Receptionist)
                if (TryAdminStaff(username, password))
                {
                    Session.Remove("ReturnUrl");
                    string role = (string)Session["Role"];
                    return RedirectToAction("Index", role);   // ManagerController / ReceptionistController
                }

                // 2. Instructors
                if (TryInstructor(username, password, out error))
                {
                    Session.Remove("ReturnUrl");
                    return RedirectToAction("Index", "Instructor");
                }
                if (error != null)
                {
                    ViewBag.Error = error;
                    return View();
                }

                // 3. Students (go back to the page they clicked before logging in, if any)
                if (TryStudent(username, password))
                    return RedirectStudentAfterLogin();

                ViewBag.Error = "Invalid username or password.";
                return View();
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not connect to the database.", ex);
                return View();
            }
        }

        // GET: Account/Register
        [HttpGet]
        public ActionResult Register()
        {
            return View();
        }

        // POST: Account/Register
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Register(string name, string surname, string username, string phone,
                                     string email, string address, string password, string confirmPassword, string idNumber,
                                     string licenceStatus, HttpPostedFileBase licenceDocument, HttpPostedFileBase idDocument)
        {
            ViewBag.IdNumber = (idNumber ?? "").Trim();
            ViewBag.LicenceStatus = (licenceStatus ?? "").Trim();
            name = (name ?? "").Trim();
            surname = (surname ?? "").Trim();
            username = (username ?? "").Trim();
            phone = (phone ?? "").Trim();
            email = (email ?? "").Trim();
            address = (address ?? "").Trim();
            password = (password ?? "").Trim();
            confirmPassword = (confirmPassword ?? "").Trim();

            // Keep what was typed so the form can be shown again after an error
            ViewBag.Form = new System.Collections.Generic.Dictionary<string, string>
            {
                { "name", name }, { "surname", surname }, { "username", username },
                { "phone", phone }, { "email", email }, { "address", address }
            };

            string fieldError = Validate.First(
                Validate.Name(name, "First name"),
                Validate.Name(surname, "Surname"),
                Validate.UserName(username),
                Validate.Phone(phone),
                Validate.Email(email),
                Validate.Address(address),
                Validate.Password(password, confirmPassword));
            if (fieldError != null)
            {
                ViewBag.Error = fieldError;
                return View();
            }
            phone = Validate.CleanPhone(phone);

            // South African ID number: valid, and old enough for a learner's licence
            var id = SaIdNumber.Parse(idNumber);
            if (!id.IsValid)
            {
                ViewBag.Error = id.Error;
                return View();
            }
            if (!id.LearnerLight)
            {
                ViewBag.Error = "You must be at least " + SaIdNumber.LearnerLightAge + " to register (the minimum age for a learner's licence). "
                              + id.EligibilityText;
                return View();
            }
            string licenceError = Licence.StatusError(licenceStatus, id);
            if (licenceError != null)
            {
                ViewBag.Error = licenceError;
                return View();
            }

            // PDF copies of the ID document and of the licence chosen above (both required)
            byte[] idPdf;
            string idPdfName;
            string idPdfError = Licence.ReadPdf(idDocument, out idPdf, out idPdfName, Licence.IdDocWhat);
            if (idPdfError != null)
            {
                ViewBag.Error = idPdfError + " (Choose the files again: browsers clear them after an error.)";
                return View();
            }
            byte[] pdf = null;
            string pdfName = null;
            string pdfError = Licence.NeedsDocument(licenceStatus) ? Licence.ReadPdf(licenceDocument, out pdf, out pdfName) : null;
            if (pdfError != null)
            {
                ViewBag.Error = pdfError + " (Choose the file again: browsers clear it after an error.)";
                return View();
            }

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    SaIdNumber.EnsureColumn(con);
                    Licence.EnsureColumns(con);
                    StudentApproval.EnsureColumns(con);
                    if (SaIdNumber.InUse(con, id.Number, 0))
                    {
                        ViewBag.Error = "An account with this ID number already exists. Try logging in or resetting your password.";
                        return View();
                    }

                    // Stop duplicate accounts
                    using (var check = new SqlCommand(
                        "SELECT COUNT(*) FROM dbo.Student1 WHERE StudentEmailAdress = @e OR StudentUserName = @u", con))
                    {
                        check.Parameters.AddWithValue("@e", email);
                        check.Parameters.AddWithValue("@u", username);
                        if ((int)check.ExecuteScalar() > 0)
                        {
                            ViewBag.Error = "An account with this email or username already exists.";
                            return View();
                        }
                    }

                    // Student and licence PDF are saved together (or not at all)
                    using (var tx = con.BeginTransaction())
                    {
                        int newId = Db.InsertRow(con, tx, "Student1", "StudentID",
                            "StudentFirstName, StudentLastName, StudentPhoneNo, StudentEmailAdress, StudentAdress, StudentUserName, StudentPassWord, [Status], IdNumber, LicenceStatus",
                            "@n, @s, @p, @e, @a, @u, @pw, @st, @id, @lic", cmd =>
                            {
                                cmd.Parameters.AddWithValue("@n", name);
                                cmd.Parameters.AddWithValue("@s", surname);
                                cmd.Parameters.AddWithValue("@p", phone);
                                cmd.Parameters.AddWithValue("@e", email);
                                cmd.Parameters.AddWithValue("@a", address);   // column is NOT NULL, so an empty string is stored
                                cmd.Parameters.AddWithValue("@u", username);
                                cmd.Parameters.AddWithValue("@pw", password);
                                cmd.Parameters.AddWithValue("@st", "Active");
                                cmd.Parameters.AddWithValue("@id", id.Number);
                                cmd.Parameters.AddWithValue("@lic", Licence.Normalize(licenceStatus));
                            });
                        Licence.SaveIdDocument(con, tx, newId, idPdfName, idPdf);
                        if (pdf != null) Licence.SaveDocument(con, tx, newId, licenceStatus, pdfName, pdf);
                        StudentApproval.MarkNew(con, tx, newId);   // must verify, then be approved by reception
                        tx.Commit();

                        // Log the new student in and send the verification code to the demo inbox
                        Session["UserId"] = newId;
                        Session["UserName"] = name + " " + surname;
                        Session["Role"] = "Student";
                    }
                }

                // The account is saved; if the code can't be created now, the student can press "Send a new code"
                try
                {
                    bool emailed;
                    string code = StudentApproval.SendCode(Convert.ToInt32(Session["UserId"]), out emailed);
                    if (!emailed) TempData["DemoCode"] = code;
                }
                catch (SqlException) { }
                TempData["Success"] = "Your account has been created. One more step: verify it with the 6-digit code.";
                return RedirectToAction("Verify", "Student");
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Registration failed.", ex);
                return View();
            }
        }

        // ---------- FORGOT PASSWORD: a 6-digit code is emailed, then typed here with the new password ----------

        private const int CodeMinutes = 15;    // how long a code stays valid
        private const int MaxCodeTries = 5;    // wrong guesses allowed per code

        [HttpGet]
        public ActionResult ForgotPassword()
        {
            ViewBag.Step = 1;
            return View();
        }

        // Step 1: the person types their email -> a code is emailed -> the code box appears
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ForgotPassword(string email)
        {
            email = (email ?? "").Trim();
            ViewBag.Email = email;
            ViewBag.Step = 1;

            if (!IsValidEmail(email))
            {
                ViewBag.Error = "Please enter a valid email address.";
                return View();
            }

            string missing = MissingMailSettings();
            if (missing != "")
            {
                ViewBag.Error = "Email sending is not set up yet. These settings are missing or empty in the main Web.config: " + missing;
                return View();
            }

            try
            {
                string username, displayName;
                if (FindAccountByEmail(email, out username, out displayName))
                {
                    string code = NewCode();
                    SaveCode(email, username, code);
                    if (!MailjetEmailService.SendPasswordResetPin(email, displayName, code, CodeMinutes))
                    {
                        ViewBag.Error = "We could not send the email right now. Please try again in a few minutes.";
                        return View();
                    }
                }
                // The same page is shown whether or not the email is registered,
                // so nobody can test which emails have accounts.
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not reach the database.", ex);
                return View();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("Reset code email failed: " + ex);
                ViewBag.Error = "We could not send the email right now. Please try again in a few minutes.";
                return View();
            }

            ViewBag.Step = 2;
            ViewBag.Info = "If an account exists for " + email + ", we have sent a 6-digit code to it. "
                         + "Check the inbox (and spam folder). The code expires in " + CodeMinutes + " minutes.";
            return View();
        }

        // Step 2: the person types the code and a new password
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ResetWithCode(string email, string code, string newPassword, string confirmPassword)
        {
            email = (email ?? "").Trim();
            code = (code ?? "").Trim().Replace(" ", "");
            newPassword = (newPassword ?? "").Trim();
            confirmPassword = (confirmPassword ?? "").Trim();
            ViewBag.Email = email;
            ViewBag.Step = 2;
            ViewBag.Info = "Type the 6-digit code we emailed to " + email + ", then choose a new password.";

            string error = null;
            if (code.Length != 6 || !IsDigits(code)) error = "The code has 6 digits. Check your email and try again.";
            else if (newPassword == "") error = "Please type the new password.";
            else if (newPassword.Length < 6) error = "Password must be at least 6 characters.";
            else if (newPassword.Length > 50) error = "Password must be 50 characters or less.";
            else if (newPassword != confirmPassword) error = "Passwords do not match.";
            if (error != null)
            {
                ViewBag.Error = error;
                return View("ForgotPassword");
            }

            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    EnsureCodeTable(con);

                    // The newest code for this email that is not used and not expired
                    int codeId = 0, attempts = 0;
                    string username = null, hash = null;
                    using (var cmd = new SqlCommand(
                        @"SELECT TOP 1 CodeID, UserName, CodeHash, Attempts FROM dbo.PasswordResetCodes
                          WHERE Email = @e AND Used = 0 AND ExpiresOn > GETDATE() ORDER BY CodeID DESC", con))
                    {
                        cmd.Parameters.AddWithValue("@e", email);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                codeId = Convert.ToInt32(r["CodeID"]);
                                username = Convert.ToString(r["UserName"]);
                                hash = Convert.ToString(r["CodeHash"]);
                                attempts = Convert.ToInt32(r["Attempts"]);
                            }
                        }
                    }

                    if (codeId == 0)
                    {
                        ViewBag.Error = "This code has expired or was never requested. Click \"Send a new code\" below.";
                        return View("ForgotPassword");
                    }
                    if (attempts >= MaxCodeTries)
                    {
                        ViewBag.Error = "Too many wrong tries. Click \"Send a new code\" below.";
                        return View("ForgotPassword");
                    }
                    if (!string.Equals(hash, HashCode(email, code), StringComparison.OrdinalIgnoreCase))
                    {
                        using (var cmd = new SqlCommand("UPDATE dbo.PasswordResetCodes SET Attempts = Attempts + 1 WHERE CodeID = @id", con))
                        {
                            cmd.Parameters.AddWithValue("@id", codeId);
                            cmd.ExecuteNonQuery();
                        }
                        ViewBag.Error = "That code is not correct. Check your email and try again.";
                        return View("ForgotPassword");
                    }

                    // Correct code: change the password (the username can belong to a student, an instructor or a staff member)
                    using (var tx = con.BeginTransaction())
                    {
                        int changed = SetPassword(con, tx, "UPDATE dbo.Student1 SET StudentPassWord = @p WHERE StudentUserName = @u", newPassword, username)
                                    + SetPassword(con, tx, "UPDATE dbo.INSTRUCTORNEW SET [PassWord] = @p WHERE UserName = @u", newPassword, username)
                                    + SetPassword(con, tx, "UPDATE dbo.AdminStaff11 SET [Password] = @p WHERE UserName = @u", newPassword, username);
                        if (changed == 0)
                        {
                            tx.Rollback();
                            ViewBag.Error = "We could not find that account any more. Please contact the school.";
                            return View("ForgotPassword");
                        }

                        // The code can only be used once
                        using (var cmd = new SqlCommand("UPDATE dbo.PasswordResetCodes SET Used = 1 WHERE CodeID = @id", con, tx))
                        {
                            cmd.Parameters.AddWithValue("@id", codeId);
                            cmd.ExecuteNonQuery();
                        }
                        tx.Commit();
                    }
                }

                TempData["Success"] = "Your password has been updated. You can now log in with the new password.";
                return RedirectToAction("Login");
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not update the password.", ex);
                return View("ForgotPassword");
            }
        }

        public ActionResult Logout()
        {
            Session.Clear();
            Session.Abandon();
            return RedirectToAction("Index", "Home");
        }

        // Called when a guest clicks a protected link (e.g. Bookings).
        // Remembers where they were going, then sends them to the Home page to log in / register.
        public ActionResult RequireLogin(string returnUrl)
        {
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                Session["ReturnUrl"] = returnUrl;
            }
            TempData["LoginMessage"] = "Please log in or register to access Bookings.";
            return RedirectToAction("Index", "Home");
        }

        // ---------- helpers ----------

        // After a successful student login: go back to the page they originally clicked,
        // otherwise the student dashboard.
        private ActionResult RedirectStudentAfterLogin()
        {
            string returnUrl = Session["ReturnUrl"] as string;
            Session.Remove("ReturnUrl");

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            return RedirectToAction("Index", "Student");
        }

        private bool TryAdminStaff(string user, string pass)
        {
            const string sql = @"SELECT AdminStaffID, Name, Surname, AdminStaffRole, [Password]
                                 FROM dbo.AdminStaff11
                                 WHERE UserName = @u OR AdminStaffEmail = @u";
            using (var con = new SqlConnection(Db.ConnStr))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@u", user);
                con.Open();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string dbPass = r["Password"] == DBNull.Value ? "" : r["Password"].ToString().Trim();
                        if (!string.Equals(dbPass, pass, StringComparison.Ordinal)) continue;

                        string dbRole = r["AdminStaffRole"].ToString().Trim();
                        string role = dbRole.StartsWith("Rec", StringComparison.OrdinalIgnoreCase)
                                      ? "Receptionist" : dbRole;   // also handles the "Recepetionist" typo

                        Session["UserId"] = r["AdminStaffID"];
                        Session["UserName"] = r["Name"] + " " + r["Surname"];
                        Session["Role"] = role;
                        return true;
                    }
                }
            }
            return false;
        }

        private bool TryInstructor(string user, string pass, out string error)
        {
            error = null;
            const string sql = @"SELECT InstructorID, Name, Surname, [PassWord], [Status]
                                 FROM dbo.INSTRUCTORNEW
                                 WHERE UserName = @u OR Email = @u";
            using (var con = new SqlConnection(Db.ConnStr))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@u", user);
                con.Open();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string dbPass = r["PassWord"] == DBNull.Value ? "" : r["PassWord"].ToString().Trim();
                        if (!string.Equals(dbPass, pass, StringComparison.Ordinal)) continue;

                        string status = r["Status"].ToString().Trim();
                        if (!status.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                        {
                            error = "Your instructor account is still pending approval.";
                            return false;
                        }

                        Session["UserId"] = r["InstructorID"];
                        Session["UserName"] = r["Name"] + " " + r["Surname"];
                        Session["Role"] = "Instructor";
                        return true;
                    }
                }
            }
            return false;
        }

        private bool TryStudent(string user, string pass)
        {
            const string sql = @"SELECT StudentID, StudentFirstName, StudentLastName, StudentPassWord
                                 FROM dbo.Student1
                                 WHERE StudentUserName = @u OR StudentEmailAdress = @u";
            using (var con = new SqlConnection(Db.ConnStr))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@u", user);
                con.Open();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string dbPass = r["StudentPassWord"] == DBNull.Value ? "" : r["StudentPassWord"].ToString().Trim();
                        if (!string.Equals(dbPass, pass, StringComparison.Ordinal)) continue;

                        Session["UserId"] = r["StudentID"];
                        Session["UserName"] = r["StudentFirstName"] + " " + r["StudentLastName"];
                        Session["Role"] = "Student";
                        return true;
                    }
                }
            }
            return false;
        }

        // ---------- forgot-password helpers ----------

        // MailAddress is only used here to check the email format (no SMTP involved)
        private static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email) || email.Length > 150) return false;
            try
            {
                var a = new MailAddress(email);
                return a.Address == email && email.IndexOf('.', email.IndexOf('@')) > 0;
            }
            catch (FormatException) { return false; }
        }

        private static bool IsDigits(string s)
        {
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        // Names of the Mailjet settings that are missing or empty ("" when everything is there)
        private static string MissingMailSettings()
        {
            var missing = new System.Collections.Generic.List<string>();
            foreach (var key in new[] { "Mailjet.ApiKey", "Mailjet.SecretKey", "Mailjet.FromEmail" })
            {
                var value = ConfigurationManager.AppSettings[key];
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("YOUR_")) missing.Add(key);
            }
            return string.Join(", ", missing);
        }

        // Creates the table the first time it is needed. Only a hash of each code is stored.
        private static void EnsureCodeTable(SqlConnection con)
        {
            using (var cmd = new SqlCommand(@"
                IF OBJECT_ID('dbo.PasswordResetCodes', 'U') IS NULL
                CREATE TABLE dbo.PasswordResetCodes(
                    CodeID    INT IDENTITY(1,1) PRIMARY KEY,
                    UserName  VARCHAR(100) NOT NULL,
                    Email     VARCHAR(150) NOT NULL,
                    CodeHash  VARCHAR(64)  NOT NULL,
                    CreatedOn DATETIME     NOT NULL DEFAULT GETDATE(),
                    ExpiresOn DATETIME     NOT NULL,
                    Attempts  INT          NOT NULL DEFAULT 0,
                    Used      BIT          NOT NULL DEFAULT 0
                );", con))
            {
                cmd.ExecuteNonQuery();
            }
        }

        // Finds the account that uses this email (student, instructor or staff). Returns false when there is none.
        private static bool FindAccountByEmail(string email, out string username, out string displayName)
        {
            username = null;
            displayName = null;
            string[] queries =
            {
                "SELECT TOP 1 StudentUserName, StudentFirstName FROM dbo.Student1 WHERE StudentEmailAdress = @e",
                "SELECT TOP 1 UserName, Name FROM dbo.INSTRUCTORNEW WHERE Email = @e",
                "SELECT TOP 1 UserName, Name FROM dbo.AdminStaff11 WHERE AdminStaffEmail = @e"
            };
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                foreach (var sql in queries)
                {
                    using (var cmd = new SqlCommand(sql, con))
                    {
                        cmd.Parameters.AddWithValue("@e", email);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read() && r[0] != DBNull.Value && Convert.ToString(r[0]).Trim() != "")
                            {
                                username = Convert.ToString(r[0]).Trim();
                                displayName = r[1] == DBNull.Value ? "" : Convert.ToString(r[1]).Trim();
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        // A random 6-digit code
        private static string NewCode()
        {
            var bytes = new byte[4];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            return (BitConverter.ToUInt32(bytes, 0) % 1000000).ToString("000000");
        }

        private static string HashCode(string email, string code)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes((email ?? "").ToLowerInvariant() + ":" + code));
                return BitConverter.ToString(bytes).Replace("-", "");
            }
        }

        // Saves a new code (older unused codes for this email stop working)
        private static void SaveCode(string email, string username, string code)
        {
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureCodeTable(con);
                using (var cmd = new SqlCommand("UPDATE dbo.PasswordResetCodes SET Used = 1 WHERE Email = @e AND Used = 0", con))
                {
                    cmd.Parameters.AddWithValue("@e", email);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = new SqlCommand(
                    @"INSERT INTO dbo.PasswordResetCodes (UserName, Email, CodeHash, ExpiresOn)
                      VALUES (@u, @e, @h, DATEADD(MINUTE, @m, GETDATE()))", con))
                {
                    cmd.Parameters.AddWithValue("@u", username);
                    cmd.Parameters.AddWithValue("@e", email);
                    cmd.Parameters.AddWithValue("@h", HashCode(email, code));
                    cmd.Parameters.AddWithValue("@m", CodeMinutes);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static int SetPassword(SqlConnection con, SqlTransaction tx, string sql, string password, string username)
        {
            using (var cmd = new SqlCommand(sql, con, tx))
            {
                cmd.Parameters.AddWithValue("@p", password);
                cmd.Parameters.AddWithValue("@u", username);
                return cmd.ExecuteNonQuery();
            }
        }
    }
}