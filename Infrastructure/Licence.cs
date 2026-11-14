using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Web;

namespace SnisDrivingSchool
{
    // The licence a learner already has (chosen at registration, with a PDF copy as proof)
    // and which code their lessons are for, worked out automatically:
    //   None              -> no licence yet: lessons for the learner's licence (no licence PDF needed)
    //   Learner's licence -> lessons for Code 8 (B)
    //   Code 8            -> lessons for Code 10 (C1)
    //   Code 10           -> lessons for Code 14 (EC)
    //   Code 14           -> already the highest: Refresher lessons only
    //   Refresher package -> only for driving licence holders, for the code they hold
    // A higher driving licence covers the lower ones (Code 14 > Code 10 > Code 8).
    public static class Licence
    {
        public const string Learner = "Learner";
        public const string None = "None";      // no licence yet: learner's licence lessons
        public const int LearnersLessons = -1;  // TargetCode for "None": lessons for the learner's licence

        // Value saved in dbo.Student1.LicenceStatus -> text shown in drop-downs
        public static readonly List<KeyValuePair<string, string>> StatusOptions = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>(None,      "None yet - I want learner's licence lessons"),
            new KeyValuePair<string, string>(Learner,   "Learner's licence"),
            new KeyValuePair<string, string>("Code 8",  "Driving licence - Code 8 (B)"),
            new KeyValuePair<string, string>("Code 10", "Driving licence - Code 10 (C1)"),
            new KeyValuePair<string, string>("Code 14", "Driving licence - Code 14 (EC)")
        };

        // Older values ("Learner Code 2", "Learner Code 3") are read in the new way
        public static string Normalize(string status)
        {
            string s = (status ?? "").Trim();
            if (s.Equals(None, StringComparison.OrdinalIgnoreCase)) return None;
            if (s.StartsWith("Learner", StringComparison.OrdinalIgnoreCase)) return Learner;
            if (s == "Code 8" || s == "Code 10" || s == "Code 14") return s;
            return "";
        }

        public static bool IsStatus(string s) { return Normalize(s) != ""; }

        // A PDF copy is needed for every licence except "None" (nothing to upload yet)
        public static bool NeedsDocument(string s) { string n = Normalize(s); return n != "" && n != None; }

        public static string StatusText(string s)
        {
            string n = Normalize(s);
            foreach (var o in StatusOptions) if (o.Key == n) return o.Value;
            return "Not set";
        }

        // Highest driving licence code held (0 = learner's only / not set)
        public static int HeldCode(string status)
        {
            switch (Normalize(status))
            {
                case "Code 8": return 8;
                case "Code 10": return 10;
                case "Code 14": return 14;
                default: return 0;
            }
        }

        public static bool IsRefresher(string packageName)
        {
            return (packageName ?? "").IndexOf("refresher", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The code lessons are for (0 = nothing left to learn)
        public static int TargetCode(string status, string packageName)
        {
            int held = HeldCode(status);
            if (IsRefresher(packageName)) return held;
            if (Normalize(status) == None) return LearnersLessons;
            if (Normalize(status) == Learner) return 8;
            if (held == 8) return 10;
            if (held == 10) return 14;
            return 0;
        }

        // Short text for the booking page / tables, e.g. "Lessons for Code 10 (you have Code 8)"
        public static string NextStepText(string status)
        {
            string n = Normalize(status);
            if (n == "") return "Your licence is not on file yet. Add it, with a PDF copy, on your Profile page.";
            if (n == None) return "You don't have a licence yet, so your lessons are for the learner's licence.";
            if (n == Learner) return "You have a learner's licence, so your lessons are for Code 8 (B).";
            if (n == "Code 14") return "You have Code 14, the highest licence. You can book the Refresher package.";
            return "You have " + n + ", so your lessons are for Code " + (HeldCode(n) == 8 ? "10 (C1)" : "14 (EC)")
                 + ". You can also book the Refresher package for " + n + ".";
        }

        // Is the licence the person says they have possible for their age? Returns an error, or null.
        public static string StatusError(string status, SaIdNumber id)
        {
            string n = Normalize(status);
            if (n == "") return "Choose which licence you have: none yet, a learner's licence, Code 8, Code 10 or Code 14.";
            if (id != null && id.IsValid && HeldCode(n) > 0 && !id.DrivingLicence)
                return "A driving licence is only issued from age 18. Choose \"Learner's licence\".";
            return null;
        }

        // Rules for booking. Returns an error message, or null when the booking is allowed.
        public static string BookingError(string status, string packageName, bool hasDocument)
        {
            string n = Normalize(status);
            if (n == "")
                return "We don't have this learner's licence on file yet. Add it, with a PDF copy, on the Profile page.";
            if (n == None)
                return IsRefresher(packageName)
                    ? "The Refresher package is for people who already have a driving licence. Choose another package."
                    : null;   // learner's licence lessons: no licence PDF needed
            if (!hasDocument)
                return "A PDF copy of the " + (n == Learner ? "learner's licence" : n + " driving licence") + " is needed before booking. Upload it on the Profile page.";

            if (IsRefresher(packageName))
                return HeldCode(n) == 0
                    ? "The Refresher package is for people who already have a driving licence. Choose another package."
                    : null;

            if (TargetCode(n, packageName) == 0)
                return "You already have Code 14, the highest licence. Choose the Refresher package.";
            return null;
        }

        // ---------- database: columns ----------

        private static bool _columnsChecked;

        // Adds dbo.Student1.LicenceStatus, dbo.BOOKING11.LicenceCode and the documents table the first time they are needed
        public static void EnsureColumns(SqlConnection con)
        {
            if (_columnsChecked) return;
            using (var cmd = new SqlCommand(
                @"IF COL_LENGTH('dbo.Student1', 'LicenceStatus') IS NULL ALTER TABLE dbo.Student1 ADD LicenceStatus VARCHAR(20) NULL;
                  IF COL_LENGTH('dbo.BOOKING11', 'LicenceCode') IS NULL ALTER TABLE dbo.BOOKING11 ADD LicenceCode VARCHAR(10) NULL;
                  IF OBJECT_ID('dbo.StudentDocuments', 'U') IS NULL
                  CREATE TABLE dbo.StudentDocuments(
                      DocID       INT IDENTITY(1,1) PRIMARY KEY,
                      StudentID   INT            NOT NULL,
                      DocType     VARCHAR(20)    NOT NULL,
                      FileName    NVARCHAR(200)  NOT NULL,
                      FileData    VARBINARY(MAX) NOT NULL,
                      UploadedOn  DATETIME       NOT NULL DEFAULT GETDATE()
                  );", con))
            {
                cmd.ExecuteNonQuery();
            }
            _columnsChecked = true;
        }

        public static string ForStudent(SqlConnection con, int studentId)
        {
            EnsureColumns(con);
            using (var cmd = new SqlCommand("SELECT ISNULL(LicenceStatus, '') FROM dbo.Student1 WHERE StudentID = @id", con))
            {
                cmd.Parameters.AddWithValue("@id", studentId);
                object o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? "" : o.ToString().Trim();
            }
        }

        // Saves which code a booking is for
        public static void SetBookingCode(SqlConnection con, SqlTransaction tx, int bookingId, int code)
        {
            using (var cmd = new SqlCommand("UPDATE dbo.BOOKING11 SET LicenceCode = @c WHERE BookingID = @b", con, tx))
            {
                cmd.Parameters.AddWithValue("@c", code > 0 ? "Code " + code : code == LearnersLessons ? "Learner's" : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@b", bookingId);
                cmd.ExecuteNonQuery();
            }
        }

        // ---------- documents (PDF copies of the licence) ----------

        public const int MaxPdfBytes = 3 * 1024 * 1024;   // 3 MB (the site's upload limit is 4 MB)

        // Checks an uploaded file is a real PDF. Returns an error, or null. The bytes are returned in data.
        public static string ReadPdf(HttpPostedFileBase file, out byte[] data, out string fileName)
        {
            return ReadPdf(file, out data, out fileName, "your licence");
        }

        // "what" names the document in the error, e.g. "your ID document"
        public static string ReadPdf(HttpPostedFileBase file, out byte[] data, out string fileName, string what)
        {
            data = null;
            fileName = null;
            if (file == null || file.ContentLength == 0)
                return "Please upload a PDF copy of " + what + ".";
            if (file.ContentLength > MaxPdfBytes)
                return "The PDF is too big. The maximum is 3 MB.";
            fileName = Path.GetFileName(file.FileName ?? "document.pdf");
            if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return "Only PDF files can be uploaded.";

            using (var ms = new MemoryStream())
            {
                file.InputStream.CopyTo(ms);
                data = ms.ToArray();
            }
            // Every PDF starts with "%PDF"
            if (data.Length < 5 || data[0] != '%' || data[1] != 'P' || data[2] != 'D' || data[3] != 'F')
            {
                data = null;
                return "That file is not a valid PDF.";
            }
            if (fileName.Length > 200) fileName = fileName.Substring(fileName.Length - 200);
            return null;
        }

        public static void SaveDocument(SqlConnection con, SqlTransaction tx, int studentId, string status, string fileName, byte[] data)
        {
            using (var cmd = new SqlCommand(
                "INSERT INTO dbo.StudentDocuments (StudentID, DocType, FileName, FileData) VALUES (@s, @t, @f, @d)", con, tx))
            {
                cmd.Parameters.AddWithValue("@s", studentId);
                cmd.Parameters.AddWithValue("@t", Normalize(status));
                cmd.Parameters.AddWithValue("@f", fileName);
                cmd.Parameters.Add("@d", System.Data.SqlDbType.VarBinary, -1).Value = data;
                cmd.ExecuteNonQuery();
            }
        }

        // Newest document for the licence the student has now (0 = none uploaded)
        public static int CurrentDocumentId(SqlConnection con, int studentId)
        {
            EnsureColumns(con);
            using (var cmd = new SqlCommand(
                @"SELECT TOP 1 d.DocID FROM dbo.StudentDocuments d
                  JOIN dbo.Student1 s ON s.StudentID = d.StudentID
                  WHERE d.StudentID = @s
                    AND (d.DocType = s.LicenceStatus OR (d.DocType = 'Learner' AND s.LicenceStatus LIKE 'Learner%'))
                  ORDER BY d.DocID DESC", con))
            {
                cmd.Parameters.AddWithValue("@s", studentId);
                object o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? 0 : Convert.ToInt32(o);
            }
        }

        // ---------- ID document (PDF copy of the learner's South African ID) ----------

        public const string IdDocType = "ID";
        public const string IdDocWhat = "your ID document";

        public static void SaveIdDocument(SqlConnection con, SqlTransaction tx, int studentId, string fileName, byte[] data)
        {
            using (var cmd = new SqlCommand(
                "INSERT INTO dbo.StudentDocuments (StudentID, DocType, FileName, FileData) VALUES (@s, @t, @f, @d)", con, tx))
            {
                cmd.Parameters.AddWithValue("@s", studentId);
                cmd.Parameters.AddWithValue("@t", IdDocType);
                cmd.Parameters.AddWithValue("@f", fileName);
                cmd.Parameters.Add("@d", System.Data.SqlDbType.VarBinary, -1).Value = data;
                cmd.ExecuteNonQuery();
            }
        }

        // Newest ID document of a student (0 = none uploaded)
        public static int CurrentIdDocumentId(SqlConnection con, int studentId)
        {
            EnsureColumns(con);
            using (var cmd = new SqlCommand(
                "SELECT TOP 1 DocID FROM dbo.StudentDocuments WHERE StudentID = @s AND DocType = 'ID' ORDER BY DocID DESC", con))
            {
                cmd.Parameters.AddWithValue("@s", studentId);
                object o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? 0 : Convert.ToInt32(o);
            }
        }

        // SQL snippet for lists: the newest ID document of student "s" (use as a column)
        public const string CurrentIdDocSql =
            @"(SELECT TOP 1 d.DocID FROM dbo.StudentDocuments d
               WHERE d.StudentID = s.StudentID AND d.DocType = 'ID'
               ORDER BY d.DocID DESC)";

        // SQL snippet for lists: the newest current document of student "s" (use as a column)
        public const string CurrentDocSql =
            @"(SELECT TOP 1 d.DocID FROM dbo.StudentDocuments d
               WHERE d.StudentID = s.StudentID
                 AND (d.DocType = s.LicenceStatus OR (d.DocType = 'Learner' AND s.LicenceStatus LIKE 'Learner%'))
               ORDER BY d.DocID DESC)";
    }
}
