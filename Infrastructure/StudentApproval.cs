using System;
using System.Data.SqlClient;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SnisDrivingSchool
{
    // New students: register -> verify with a 6-digit code (emailed to the student's address) -> the receptionist
    // approves or disapproves them. Only verified AND approved students can book and pay.
    // Students that already existed before this feature, and students added by staff, count as approved.
    public static class StudentApproval
    {
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        public const int CodeMinutes = 15;

        // Where a student is in the process
        public enum Stage { Verify, Waiting, Approved, Rejected }

        public class State
        {
            public int StudentId { get; set; }
            public string Name { get; set; }
            public string Email { get; set; }
            public bool Verified { get; set; }
            public string Approval { get; set; }   // Pending / Approved / Rejected
            public string Note { get; set; }       // reason given when not approved
            public Stage Stage
            {
                get
                {
                    if (Approval == Rejected) return Stage.Rejected;
                    if (!Verified) return Stage.Verify;
                    return Approval == Approved ? Stage.Approved : Stage.Waiting;
                }
            }
            public bool CanBook { get { return Stage == Stage.Approved; } }
        }

        // ---------- database ----------

        private static bool _checked;

        public static void EnsureColumns(SqlConnection con)
        {
            if (_checked) return;
            using (var cmd = new SqlCommand(
                @"IF COL_LENGTH('dbo.Student1', 'ApprovalStatus') IS NULL
                  BEGIN
                      ALTER TABLE dbo.Student1 ADD ApprovalStatus VARCHAR(20) NULL, VerifiedOn DATETIME NULL,
                          VerifyCodeHash VARCHAR(64) NULL, VerifyExpires DATETIME NULL,
                          ApprovalNote NVARCHAR(300) NULL, ApprovedOn DATETIME NULL, ApprovedBy INT NULL;
                      -- everyone who registered before this feature is already verified and approved
                      EXEC('UPDATE dbo.Student1 SET ApprovalStatus = ''Approved'', VerifiedOn = GETDATE(), ApprovedOn = GETDATE()');
                  END", con))
            {
                cmd.ExecuteNonQuery();
            }
            _checked = true;
        }

        public static State Get(SqlConnection con, int studentId)
        {
            EnsureColumns(con);
            using (var cmd = new SqlCommand(
                @"SELECT StudentID, LTRIM(RTRIM(ISNULL(StudentFirstName, '') + ' ' + ISNULL(StudentLastName, ''))) AS Name,
                         LTRIM(RTRIM(ISNULL(StudentEmailAdress, ''))) AS Email,
                         VerifiedOn, ISNULL(ApprovalStatus, 'Approved') AS ApprovalStatus, ApprovalNote
                  FROM dbo.Student1 WHERE StudentID = @s", con))
            {
                cmd.Parameters.AddWithValue("@s", studentId);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new State
                    {
                        StudentId = studentId,
                        Name = Convert.ToString(r["Name"]),
                        Email = Convert.ToString(r["Email"]),
                        Verified = r["VerifiedOn"] != DBNull.Value,
                        Approval = Convert.ToString(r["ApprovalStatus"]),
                        Note = r["ApprovalNote"] == DBNull.Value ? "" : Convert.ToString(r["ApprovalNote"])
                    };
                }
            }
        }

        public static State Get(int studentId)
        {
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                return Get(con, studentId);
            }
        }

        // Error message for staff booking for a student who isn't approved yet (null = fine)
        public static string BookingError(SqlConnection con, int studentId, string name)
        {
            var s = Get(con, studentId);
            if (s == null || s.CanBook) return null;
            return name + " can't book yet: " + StageText(s.Stage).ToLowerInvariant() + ".";
        }

        // ---------- verification code ----------

        // Makes a new 6-digit code (valid for 15 minutes), saves only its hash, and emails it to the student.
        // Returns the code; emailed = false when the email could not be sent (the page then shows the code).
        public static string SendCode(int studentId, out bool emailed)
        {
            string code;
            var bytes = new byte[4];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            code = (BitConverter.ToUInt32(bytes, 0) % 1000000).ToString("000000");

            string name;
            string email;
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                var s = Get(con, studentId);
                name = s == null ? "a student" : s.Name;
                email = s == null ? "" : s.Email;
                using (var cmd = new SqlCommand(
                    @"UPDATE dbo.Student1 SET VerifyCodeHash = @h, VerifyExpires = DATEADD(MINUTE, @m, GETDATE())
                      WHERE StudentID = @s", con))
                {
                    cmd.Parameters.AddWithValue("@h", Hash(studentId, code));
                    cmd.Parameters.AddWithValue("@m", CodeMinutes);
                    cmd.Parameters.AddWithValue("@s", studentId);
                    cmd.ExecuteNonQuery();
                }
            }

            string subject = "Snis Driving Academy - verification code for " + name;
            string text = "Verification code for " + name + " (student #" + studentId + "): " + code + "\r\n\r\n"
                        + "Type this code on the \"Verify your account\" page. It expires in " + CodeMinutes + " minutes.";
            string html = "<div style=\"font-family:Arial,sans-serif;max-width:480px;\">"
                        + "<h2>Verify your account</h2>"
                        + "<p>Verification code for <b>" + WebUtility.HtmlEncode(name) + "</b> (student #" + studentId + "):</p>"
                        + "<p style=\"font-size:30px;font-weight:bold;letter-spacing:6px;margin:10px 0;\">" + code + "</p>"
                        + "<p>Type this code on the <b>Verify your account</b> page.</p>"
                        + "<p style=\"color:#666;font-size:12px;\">The code expires in " + CodeMinutes + " minutes. "
                         + "This code was sent to the email address on your student account.</p></div>";
            emailed = DemoMail.Send(email, subject, text, html);
            return code;
        }

        // Checks the code the student typed. Returns an error, or null when the account is now verified.
        public static string CheckCode(int studentId, string code)
        {
            code = (code ?? "").Trim().Replace(" ", "");
            if (code.Length != 6 || !IsDigits(code)) return "The code has 6 digits. Check the email and try again.";

            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureColumns(con);
                string hash = null;
                DateTime? expires = null;
                using (var cmd = new SqlCommand("SELECT VerifyCodeHash, VerifyExpires FROM dbo.Student1 WHERE StudentID = @s", con))
                {
                    cmd.Parameters.AddWithValue("@s", studentId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            hash = r["VerifyCodeHash"] == DBNull.Value ? null : Convert.ToString(r["VerifyCodeHash"]);
                            expires = r["VerifyExpires"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["VerifyExpires"]);
                        }
                    }
                }
                if (hash == null) return "There is no active code. Click \"Send a new code\".";
                if (!expires.HasValue || expires.Value < DateTime.Now) return "This code has expired. Click \"Send a new code\".";
                if (!string.Equals(hash, Hash(studentId, code), StringComparison.OrdinalIgnoreCase)) return "That code is not correct. Check the email and try again.";

                using (var cmd = new SqlCommand(
                    "UPDATE dbo.Student1 SET VerifiedOn = GETDATE(), VerifyCodeHash = NULL, VerifyExpires = NULL WHERE StudentID = @s", con))
                {
                    cmd.Parameters.AddWithValue("@s", studentId);
                    cmd.ExecuteNonQuery();
                }
            }
            return null;
        }

        // ---------- receptionist decision ----------

        // approve = true: Approved (only once verified). approve = false: not approved, with a reason.
        public static string Decide(int studentId, bool approve, int staffId, string reason)
        {
            reason = (reason ?? "").Trim();
            if (!approve && reason.Length < 5) return "Please give a short reason why the student is not approved.";
            if (reason.Length > 300) return "The reason is too long (300 characters at most).";

            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                var s = Get(con, studentId);
                if (s == null) return "That student could not be found.";
                if (approve && !s.Verified) return s.Name + " has not verified their account yet, so they can't be approved.";

                using (var cmd = new SqlCommand(
                    @"UPDATE dbo.Student1 SET ApprovalStatus = @a, ApprovalNote = @n, ApprovedOn = GETDATE(), ApprovedBy = @by
                      WHERE StudentID = @s", con))
                {
                    cmd.Parameters.AddWithValue("@a", approve ? Approved : Rejected);
                    cmd.Parameters.AddWithValue("@n", approve ? (object)DBNull.Value : reason);
                    cmd.Parameters.AddWithValue("@by", staffId);
                    cmd.Parameters.AddWithValue("@s", studentId);
                    cmd.ExecuteNonQuery();
                }
            }
            return null;
        }

        // Students registered by staff (in person) start verified and approved
        // (call EnsureColumns before starting the transaction)
        public static void MarkApproved(SqlConnection con, SqlTransaction tx, int studentId, int staffId)
        {
            using (var cmd = new SqlCommand(
                @"UPDATE dbo.Student1 SET ApprovalStatus = 'Approved', VerifiedOn = GETDATE(), ApprovedOn = GETDATE(), ApprovedBy = @by
                  WHERE StudentID = @s", con, tx))
            {
                cmd.Parameters.AddWithValue("@by", staffId);
                cmd.Parameters.AddWithValue("@s", studentId);
                cmd.ExecuteNonQuery();
            }
        }

        // A student who registered online starts as Pending and not verified
        // (call EnsureColumns before starting the transaction)
        public static void MarkNew(SqlConnection con, SqlTransaction tx, int studentId)
        {
            using (var cmd = new SqlCommand(
                "UPDATE dbo.Student1 SET ApprovalStatus = 'Pending', VerifiedOn = NULL WHERE StudentID = @s", con, tx))
            {
                cmd.Parameters.AddWithValue("@s", studentId);
                cmd.ExecuteNonQuery();
            }
        }

        // ---------- text for pages ----------

        public static string StageText(Stage stage)
        {
            switch (stage)
            {
                case Stage.Verify: return "Not verified yet";
                case Stage.Waiting: return "Waiting for approval";
                case Stage.Rejected: return "Not approved";
                default: return "Approved";
            }
        }

        // Badge colour (works with the m-ok / m-warn / m-bad / m-info classes and the st- ones)
        public static string StageCss(Stage stage)
        {
            switch (stage)
            {
                case Stage.Verify: return "info";
                case Stage.Waiting: return "warn";
                case Stage.Rejected: return "bad";
                default: return "ok";
            }
        }

        // SQL for lists: the stage of student "s" as text (use as a column)
        public const string StageSql =
            @"CASE WHEN ISNULL(s.ApprovalStatus, 'Approved') = 'Rejected' THEN 'Rejected'
                   WHEN s.VerifiedOn IS NULL AND ISNULL(s.ApprovalStatus, 'Approved') <> 'Approved' THEN 'Verify'
                   WHEN ISNULL(s.ApprovalStatus, 'Approved') = 'Approved' THEN 'Approved'
                   ELSE 'Waiting' END";

        public static Stage ParseStage(string s)
        {
            Stage st;
            return Enum.TryParse(s, out st) ? st : Stage.Approved;
        }

        private static string Hash(int studentId, string code)
        {
            using (var sha = SHA256.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(studentId + ":" + code));
                return BitConverter.ToString(b).Replace("-", "");
            }
        }

        private static bool IsDigits(string s)
        {
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }
    }
}
