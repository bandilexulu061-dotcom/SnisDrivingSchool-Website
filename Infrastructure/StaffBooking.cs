using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using SnisDrivingSchool.Models;

namespace SnisDrivingSchool
{
    // Drop-down lists for the staff "New booking" form
    public class StaffBookingLists
    {
        public List<SelectItem> Students = new List<SelectItem>();
        public List<SelectItem> Instructors = new List<SelectItem>();
        public List<SelectItem> Packages = new List<SelectItem>();
        public List<SelectItem> TimeSlots = new List<SelectItem>();
    }

    // Everything the shared booking pop-up (Views/Shared/_StaffBookingDialog.cshtml) needs
    public class StaffBookingFormVm
    {
        public string Controller { get; set; }               // "Manager" or "Receptionist"
        public StaffBookingLists Lists { get; set; }
        public Dictionary<string, string> Form { get; set; } // what was typed, after an error
        public string Error { get; set; }
        public bool Open { get; set; }                       // reopen the pop-up (after an error)
    }

    // Booking a lesson on behalf of any learner (used by the manager and the receptionist)
    public static class StaffBooking
    {
        public static StaffBookingLists LoadLists(SqlConnection con)
        {
            var lists = new StaffBookingLists();
            SaIdNumber.EnsureColumn(con);
            Licence.EnsureColumns(con);

            // Learners, with their age / licence status so staff can see who may book
            using (var cmd = new SqlCommand(
                @"SELECT StudentID, StudentFirstName, StudentLastName, ISNULL(IdNumber, '') AS IdNumber,
                         ISNULL(LicenceStatus, '') AS LicenceStatus, " + Licence.CurrentDocSql + @" AS DocID
                  FROM dbo.Student1 s ORDER BY StudentFirstName, StudentLastName", con))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var id = SaIdNumber.Parse(Convert.ToString(r["IdNumber"]));
                    string note = !id.IsValid ? (id.Number == "" ? "no ID number" : "invalid ID")
                                : id.DrivingLicence ? id.Age + " yrs" : id.LearnerLight ? id.Age + " yrs, learner only" : id.Age + " yrs, too young";
                    lists.Students.Add(new SelectItem
                    {
                        Id = Convert.ToInt32(r["StudentID"]),
                        Text = (Convert.ToString(r["StudentFirstName"]) + " " + Convert.ToString(r["StudentLastName"])).Trim()
                             + " (#" + r["StudentID"] + ", " + note + ", "
                             + (!Licence.IsStatus(Convert.ToString(r["LicenceStatus"])) ? "licence not set"
                                : Licence.Normalize(Convert.ToString(r["LicenceStatus"])) == Licence.None ? "no licence yet"
                                : (Licence.Normalize(Convert.ToString(r["LicenceStatus"])) == Licence.Learner ? "learner's" : Licence.Normalize(Convert.ToString(r["LicenceStatus"])))
                                  + (r["DocID"] == DBNull.Value ? ", no PDF" : "")) + ")"
                    });
                }
            }

            using (var cmd = new SqlCommand(
                "SELECT InstructorID, Name, Surname FROM dbo.INSTRUCTORNEW WHERE [Status] = 'Approved' ORDER BY Name, Surname", con))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    lists.Instructors.Add(new SelectItem { Id = Convert.ToInt32(r["InstructorID"]), Text = (r["Name"] + " " + r["Surname"]).Trim() });
            InstructorLeave.MarkInList(con, lists.Instructors);

            using (var cmd = new SqlCommand("SELECT PackageID, PackageName, PackagePrice FROM dbo.PACKAGEN ORDER BY PackageID", con))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    lists.Packages.Add(new SelectItem
                    {
                        Id = Convert.ToInt32(r["PackageID"]),
                        Text = Convert.ToString(r["PackageName"])
                             + (r["PackagePrice"] == DBNull.Value ? "" : " - R " + Convert.ToDecimal(r["PackagePrice"]).ToString("N0", CultureInfo.InvariantCulture))
                    });

            using (var cmd = new SqlCommand(
                @"SELECT TSID, CONVERT(varchar(5), StartTime, 108) AS S, CONVERT(varchar(5), EndTime, 108) AS E
                  FROM dbo.TimeSlotNew ORDER BY StartTime", con))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    string s = Convert.ToString(r["S"]), e = Convert.ToString(r["E"]);
                    lists.TimeSlots.Add(new SelectItem { Id = Convert.ToInt32(r["TSID"]), Text = e == "" ? s : s + " - " + e });
                }

            return lists;
        }

        // Checks the form, then saves the booking as Pending (it becomes Booked once payment is confirmed).
        // Returns an error message, or null when the booking was saved.
        public static string Create(int staffId, int? studentId, int? instructorId, int? packageId, int? tsId, string bookingDate,
                                    out string studentName, out DateTime date)
        {
            studentName = "";
            date = DateTime.MinValue;
            bookingDate = (bookingDate ?? "").Trim();

            if (!studentId.HasValue || !instructorId.HasValue || !packageId.HasValue || !tsId.HasValue || bookingDate == "")
                return "Please choose a student, instructor, package, time and date.";
            if (!DateTime.TryParseExact(bookingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return "Please pick a valid date from the calendar.";
            if (date.Date < Cancellation.EarliestBookingDate)
                return Cancellation.TooSoonError();
            if (date.Date > DateTime.Today.AddMonths(2))
                return "Lessons can only be booked up to two months ahead (until " + DateTime.Today.AddMonths(2).ToString("yyyy/MM/dd") + ").";

            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();

                string fn = null, ln = null;
                using (var cmd = new SqlCommand("SELECT StudentFirstName, StudentLastName FROM dbo.Student1 WHERE StudentID = @s", con))
                {
                    cmd.Parameters.AddWithValue("@s", studentId.Value);
                    using (var r = cmd.ExecuteReader())
                        if (r.Read()) { fn = Convert.ToString(r["StudentFirstName"]).Trim(); ln = Convert.ToString(r["StudentLastName"]).Trim(); }
                }
                if (fn == null) return "That student could not be found.";
                studentName = (fn + " " + ln).Trim();

                using (var cmd = new SqlCommand(
                    @"SELECT (SELECT COUNT(*) FROM dbo.PACKAGEN WHERE PackageID = @p),
                             (SELECT COUNT(*) FROM dbo.INSTRUCTORNEW WHERE InstructorID = @i AND [Status] = 'Approved'),
                             (SELECT COUNT(*) FROM dbo.TimeSlotNew WHERE TSID = @t)", con))
                {
                    cmd.Parameters.AddWithValue("@p", packageId.Value);
                    cmd.Parameters.AddWithValue("@i", instructorId.Value);
                    cmd.Parameters.AddWithValue("@t", tsId.Value);
                    using (var r = cmd.ExecuteReader())
                    {
                        r.Read();
                        if (Convert.ToInt32(r[0]) == 0 || Convert.ToInt32(r[1]) == 0 || Convert.ToInt32(r[2]) == 0)
                            return "Please choose a valid instructor, package and time.";
                    }
                }

                string idNumber = SaIdNumber.ForStudent(con, studentId.Value);

                // South African rules: age (learner's 17+, test packages 18+) and the learner's current licence
                string package = SaIdNumber.PackageName(con, packageId.Value);
                string status = Licence.ForStudent(con, studentId.Value);
                string ageError = SaIdNumber.BookingError(idNumber, package)
                               ?? Licence.BookingError(status, package, Licence.CurrentDocumentId(con, studentId.Value) > 0);
                int licenceCode = Licence.TargetCode(status, package);   // learner's -> 8, Code 8 -> 10, Code 10 -> 14
                if (ageError != null)
                    return studentName + ": " + ageError.Replace("Add it on the Profile page", "Add it on the Students page or ask the learner to add it on their profile")
                                                         .Replace("correct it on the Profile page", "correct it on their profile")
                                                         .Replace("on the Profile page", "with the Upload button on the Students page")
                                                         .Replace("You already have", "They already have");

                string approvalError = StudentApproval.BookingError(con, studentId.Value, studentName);
                if (approvalError != null) return approvalError;

                string monthError = Cancellation.MonthLimitError(con, studentId.Value, date, studentName);
                if (monthError != null) return monthError;

                // The package's lessons: one a day from the chosen date, Sundays skipped
                Replacement.EnsureColumns(con);
                LessonPlan.EnsureColumns(con);
                if (date.DayOfWeek == DayOfWeek.Sunday) return "There are no lessons on Sundays. Choose another start date.";
                int lessons = LessonPlan.CountFor(con, null, packageId.Value);
                DateTime end = LessonPlan.End(date, lessons);
                string period = lessons == 1 ? date.ToString("d MMMM") : date.ToString("d MMMM") + " to " + end.ToString("d MMMM");
                if (InstructorLeave.IsOnLeave(con, null, instructorId.Value, date, end))
                    return "That instructor is on leave during " + period + ". Choose another instructor or date.";
                if (Clash(con, "InstructorID", instructorId.Value, tsId.Value, date, end) || Clash(con, "ProposedInstructorID", instructorId.Value, tsId.Value, date, end))
                    return "That instructor already has a lesson at that time during " + period + ".";
                if (Clash(con, "StudentID", studentId.Value, 0, date, end))
                    return studentName + " already has lessons booked during " + period + ".";

                DateTime day = date.Date;   // an "out" parameter can't be used inside the lambda below
                using (var tx = con.BeginTransaction())
                {
                    // Saved as Pending / Pending: it becomes Booked once payment is confirmed
                    int newId = Db.InsertRow(con, tx, "BOOKING11", "BookingID",
                        "AdminStaffID, StudentID, PackageID, TSID, BookingDate, BookingStatus, InstructorID, StudentFirstName, StudentLastName, PaymentStatus",
                        "@a, @s, @p, @t, @d, 'Pending', @i, @fn, @ln, 'Pending'", c =>
                        {
                            c.Parameters.AddWithValue("@a", staffId);
                            c.Parameters.AddWithValue("@s", studentId.Value);
                            c.Parameters.AddWithValue("@p", packageId.Value);
                            c.Parameters.AddWithValue("@t", tsId.Value);
                            c.Parameters.AddWithValue("@d", day);
                            c.Parameters.AddWithValue("@i", instructorId.Value);
                            c.Parameters.AddWithValue("@fn", fn);
                            c.Parameters.AddWithValue("@ln", ln);
                        });
                    Licence.SetBookingCode(con, tx, newId, licenceCode);
                    LessonPlan.SetEnd(con, tx, newId, end);
                    tx.Commit();
                }
            }
            return null;
        }

        // Is this instructor / student already booked during the lesson period? (tsId 0 = any time of day)
        private static bool Clash(SqlConnection con, string column, int id, int tsId, DateTime start, DateTime end)
        {
            using (var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.BOOKING11 x WHERE x." + column + " = @x" + (tsId > 0 ? " AND x.TSID = @t" : "")
                + " AND " + LessonPlan.OverlapSql + " AND ISNULL(x.BookingStatus, '') <> 'Cancelled'", con))
            {
                cmd.Parameters.AddWithValue("@x", id);
                cmd.Parameters.AddWithValue("@t", tsId);
                cmd.Parameters.AddWithValue("@d", start.Date);
                cmd.Parameters.AddWithValue("@e", end.Date);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }
    }
}
