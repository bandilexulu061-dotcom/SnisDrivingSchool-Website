using System;
using System.Data.SqlClient;
using System.Globalization;

namespace SnisDrivingSchool
{
    // Reads a South African ID number:  YYMMDD SSSS C A Z
    //   YYMMDD = date of birth, SSSS = 0000-4999 female / 5000-9999 male,
    //   C = 0 SA citizen, 1 permanent resident, 2 refugee, A = 8 or 9, Z = Luhn check digit.
    // and works out which licences the person is old enough for (National Road Traffic Act ages):
    //   learner's licence Code 1 (motorcycle) 17 (16 for up to 125cc), Code 2 (light vehicle) 17, Code 3 (heavy vehicle) 18;
    //   driving licence (Code B / 8, C1 / 10, EC / 14 ...) 18.
    public class SaIdNumber
    {
        public const int LearnerLightAge = 17;   // learner's licence Code 2 -> lessons for Code B (old Code 8)
        public const int LearnerHeavyAge = 18;   // learner's licence Code 3 -> lessons for Code C1 / EC (old Code 10 / 14)
        public const int DrivingLicenceAge = 18; // driving licence test

        public string Number { get; private set; }
        public bool IsValid { get; private set; }
        public string Error { get; private set; }
        public DateTime DateOfBirth { get; private set; }
        public string Gender { get; private set; }
        public string Citizenship { get; private set; }

        public int Age
        {
            get
            {
                DateTime today = DateTime.Today;
                int age = today.Year - DateOfBirth.Year;
                if (DateOfBirth.Date > today.AddYears(-age)) age--;
                return age;
            }
        }

        public bool LearnerLight { get { return IsValid && Age >= LearnerLightAge; } }
        public bool LearnerHeavy { get { return IsValid && Age >= LearnerHeavyAge; } }
        public bool DrivingLicence { get { return IsValid && Age >= DrivingLicenceAge; } }

        // Short label for tables: "Learner + licence", "Learner only", "Too young", "No ID"
        public string EligibilityLabel
        {
            get
            {
                if (!IsValid) return Number == "" ? "No ID number" : "Invalid ID";
                if (DrivingLicence) return "Learner + licence";
                if (LearnerLight) return "Learner only";
                return "Too young";
            }
        }

        // CSS badge class used on the manager / receptionist pages (m-ok, m-warn, m-bad)
        public string EligibilityCss
        {
            get
            {
                if (!IsValid) return "m-bad";
                if (DrivingLicence) return "m-ok";
                return LearnerLight ? "m-warn" : "m-bad";
            }
        }

        // One sentence for the student's profile page
        public string EligibilityText
        {
            get
            {
                if (!IsValid) return Number == "" ? "Add your ID number to book lessons." : Error;
                if (DrivingLicence)
                    return "Age " + Age + ": you qualify for a learner's licence (Code 2 and Code 3) and a driving licence test.";
                if (LearnerLight)
                {
                    DateTime eighteen = DateOfBirth.AddYears(DrivingLicenceAge);
                    return "Age " + Age + ": you qualify for a learner's licence (Code 2, light vehicles). "
                         + "You can take the driving licence test from " + eighteen.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) + ".";
                }
                DateTime seventeen = DateOfBirth.AddYears(LearnerLightAge);
                return "Age " + Age + ": you can apply for a learner's licence from "
                     + seventeen.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) + ".";
            }
        }

        public static SaIdNumber Parse(string input)
        {
            var id = new SaIdNumber { Number = (input ?? "").Replace(" ", "").Trim() };
            string n = id.Number;

            if (n == "") { id.Error = "Enter the 13-digit South African ID number."; return id; }
            if (n.Length != 13) { id.Error = "A South African ID number has exactly 13 digits."; return id; }
            foreach (char ch in n)
                if (ch < '0' || ch > '9') { id.Error = "The ID number can only contain digits."; return id; }

            // Date of birth: the two-digit year belongs to this century unless that would be in the future
            int yy = int.Parse(n.Substring(0, 2)), mm = int.Parse(n.Substring(2, 2)), dd = int.Parse(n.Substring(4, 2));
            int year = 2000 + yy;
            if (year > DateTime.Today.Year) year -= 100;
            DateTime dob;
            if (mm < 1 || mm > 12 || dd < 1 || dd > DateTime.DaysInMonth(year, mm)
                || !DateTime.TryParse(year + "-" + mm + "-" + dd, CultureInfo.InvariantCulture, DateTimeStyles.None, out dob)
                || dob > DateTime.Today)
            {
                id.Error = "The first 6 digits of the ID number are not a valid date of birth (YYMMDD).";
                return id;
            }

            char c = n[10];
            if (c != '0' && c != '1' && c != '2') { id.Error = "The 11th digit of the ID number must be 0, 1 or 2."; return id; }

            if (!LuhnOk(n)) { id.Error = "This ID number is not valid (the check digit does not match). Please check for typing mistakes."; return id; }

            id.DateOfBirth = dob;
            id.Gender = int.Parse(n.Substring(6, 4)) >= 5000 ? "Male" : "Female";
            id.Citizenship = c == '0' ? "SA citizen" : c == '1' ? "Permanent resident" : "Refugee";
            id.IsValid = true;
            return id;
        }

        // Luhn check over all 13 digits (the last digit is the check digit)
        private static bool LuhnOk(string n)
        {
            int sum = 0;
            bool doubleIt = false;
            for (int i = n.Length - 1; i >= 0; i--)
            {
                int d = n[i] - '0';
                if (doubleIt) { d *= 2; if (d > 9) d -= 9; }
                sum += d;
                doubleIt = !doubleIt;
            }
            return sum % 10 == 0;
        }

        // Rules for booking lessons. Returns an error message, or null when the student may book.
        //   - every lesson needs a valid ID and age 17+ (learner's licence for light vehicles)
        //   - a "test" package (driving test preparation) needs age 18+ (driving licence age)
        public static string BookingError(string idNumber, string packageName)
        {
            var id = Parse(idNumber);
            if (id.Number == "")
                return "An ID number is needed before booking. Add it on the Profile page.";
            if (!id.IsValid)
                return "The ID number on this account is not valid. Please correct it on the Profile page.";
            if (!id.LearnerLight)
                return "Lessons can only be booked from age " + LearnerLightAge + " (the minimum age for a learner's licence). "
                     + "This student turns " + LearnerLightAge + " on "
                     + id.DateOfBirth.AddYears(LearnerLightAge).ToString("d MMMM yyyy", CultureInfo.InvariantCulture) + ".";
            if ((packageName ?? "").IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0 && !id.DrivingLicence)
                return "The " + packageName.Trim() + " package prepares for the driving licence test, which needs age "
                     + DrivingLicenceAge + ". This student turns " + DrivingLicenceAge + " on "
                     + id.DateOfBirth.AddYears(DrivingLicenceAge).ToString("d MMMM yyyy", CultureInfo.InvariantCulture) + ".";
            return null;
        }

        // ---------- database helpers ----------

        private static bool _columnChecked;

        // Adds dbo.Student1.IdNumber the first time it is needed (empty for existing students, so nothing breaks)
        public static void EnsureColumn(SqlConnection con)
        {
            if (_columnChecked) return;
            using (var cmd = new SqlCommand(
                "IF COL_LENGTH('dbo.Student1', 'IdNumber') IS NULL ALTER TABLE dbo.Student1 ADD IdNumber VARCHAR(13) NULL;", con))
            {
                cmd.ExecuteNonQuery();
            }
            _columnChecked = true;
        }

        // True when another student (not excludeStudentId) already uses this ID number
        public static bool InUse(SqlConnection con, string idNumber, int excludeStudentId)
        {
            using (var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.Student1 WHERE IdNumber = @n AND StudentID <> @id", con))
            {
                cmd.Parameters.AddWithValue("@n", idNumber);
                cmd.Parameters.AddWithValue("@id", excludeStudentId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        // The ID number saved for a student ("" when none)
        public static string ForStudent(SqlConnection con, int studentId)
        {
            EnsureColumn(con);
            using (var cmd = new SqlCommand("SELECT ISNULL(IdNumber, '') FROM dbo.Student1 WHERE StudentID = @id", con))
            {
                cmd.Parameters.AddWithValue("@id", studentId);
                object o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? "" : o.ToString().Trim();
            }
        }

        public static string PackageName(SqlConnection con, int packageId)
        {
            using (var cmd = new SqlCommand("SELECT PackageName FROM dbo.PACKAGEN WHERE PackageID = @p", con))
            {
                cmd.Parameters.AddWithValue("@p", packageId);
                object o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? "" : o.ToString();
            }
        }
    }
}
