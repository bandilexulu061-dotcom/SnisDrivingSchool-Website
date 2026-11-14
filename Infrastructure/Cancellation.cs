using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;

namespace SnisDrivingSchool
{
    // Cancelling a booking (by the student or the instructor) and the 3-day booking rule.
    //  - A reason is always required.
    //  - If the booking was paid (or the EFT was sent), the money is refunded first and the refund is recorded.
    //  - The booking becomes "Cancelled". That is final: it can't be paid, confirmed or cancelled again.
    //  - Who cancelled, why and when are saved on the booking (dbo.BOOKING11), so every page and report can show it.
    public static class Cancellation
    {
        public const string ByStudent = "Student";
        public const string ByInstructor = "Instructor";
        public const int MinReason = 10;
        public const int MaxReason = 500;

        // ---------- 3-day booking rule ----------

        public const int DaysAhead = 3;

        // Earliest date a lesson can be booked: today + 3 days (not today, not tomorrow, not the day after)
        public static DateTime EarliestBookingDate { get { return DateTime.Today.AddDays(DaysAhead); } }

        public static string TooSoonError()
        {
            return "Lessons must be booked at least " + DaysAhead + " days ahead. The earliest date you can choose is "
                 + EarliestBookingDate.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture) + ".";
        }

        // ---------- one booking per month ----------

        // A student may have only one booking per calendar month (counted by the lesson date).
        // Cancelled bookings don't count, so after cancelling they can book that month again.
        // who = "You" (student booking) or the student's name (staff booking for them).
        public static string MonthLimitError(SqlConnection con, int studentId, DateTime date, string who)
        {
            var from = new DateTime(date.Year, date.Month, 1);
            using (var cmd = new SqlCommand(
                @"SELECT TOP 1 BookingID, BookingDate FROM dbo.BOOKING11
                  WHERE StudentID = @s AND BookingDate >= @from AND BookingDate < @to
                    AND ISNULL(BookingStatus, '') <> 'Cancelled'
                  ORDER BY BookingDate", con))
            {
                cmd.Parameters.AddWithValue("@s", studentId);
                cmd.Parameters.AddWithValue("@from", from);
                cmd.Parameters.AddWithValue("@to", from.AddMonths(1));
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    string month = from.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
                    string existing = "booking #" + r["BookingID"] + " on " + Convert.ToDateTime(r["BookingDate"]).ToString("d MMMM", CultureInfo.InvariantCulture);
                    return (who == "You" ? "You already have" : who + " already has") + " a booking in " + month + " (" + existing + "). "
                         + "Only one booking per month is allowed. Choose a date in another month, or cancel that booking first.";
                }
            }
        }

        // Months (as "yyyy-MM") in which the student already has a booking that is not cancelled
        public static List<string> BookedMonths(SqlConnection con, int studentId)
        {
            var list = new List<string>();
            using (var cmd = new SqlCommand(
                @"SELECT DISTINCT CONVERT(char(7), BookingDate, 126) FROM dbo.BOOKING11
                  WHERE StudentID = @s AND BookingDate >= @from AND ISNULL(BookingStatus, '') <> 'Cancelled'", con))
            {
                cmd.Parameters.AddWithValue("@s", studentId);
                cmd.Parameters.AddWithValue("@from", new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) list.Add(r.GetString(0));
            }
            return list;
        }

        // ---------- database ----------

        private static bool _columnsChecked;

        public static void EnsureColumns(SqlConnection con)
        {
            if (_columnsChecked) return;
            using (var cmd = new SqlCommand(
                @"IF COL_LENGTH('dbo.BOOKING11', 'CancelledBy')  IS NULL ALTER TABLE dbo.BOOKING11 ADD CancelledBy  VARCHAR(20)   NULL;
                  IF COL_LENGTH('dbo.BOOKING11', 'CancelReason') IS NULL ALTER TABLE dbo.BOOKING11 ADD CancelReason NVARCHAR(500) NULL;
                  IF COL_LENGTH('dbo.BOOKING11', 'CancelledOn')  IS NULL ALTER TABLE dbo.BOOKING11 ADD CancelledOn  DATETIME      NULL;
                  IF COL_LENGTH('dbo.BOOKING11', 'RefundAmount') IS NULL ALTER TABLE dbo.BOOKING11 ADD RefundAmount DECIMAL(10,2) NULL;
                  IF COL_LENGTH('dbo.BOOKING11', 'RefundedOn')   IS NULL ALTER TABLE dbo.BOOKING11 ADD RefundedOn   DATETIME      NULL;", con))
            {
                cmd.ExecuteNonQuery();
            }
            _columnsChecked = true;
        }

        // Cancels one booking. "by" is ByStudent or ByInstructor and byId must own the booking.
        // Returns an error message, or null when it worked (then "done" says what happened, incl. any refund).
        public static string Cancel(int bookingId, string by, int byId, string reason, out string done)
        {
            done = null;
            reason = (reason ?? "").Trim();
            if (reason.Length < MinReason)
                return "Please give a reason for cancelling (at least " + MinReason + " characters).";
            if (reason.Length > MaxReason)
                return "The reason is too long (" + MaxReason + " characters at most).";

            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                EnsureColumns(con);
                using (var tx = con.BeginTransaction())
                {
                    // Load the booking (locked so two people can't cancel it at the same moment)
                    int studentId = 0, instructorId = 0;
                    string status = null, payStatus = "";
                    DateTime date = DateTime.MinValue;
                    decimal price = 0m, paid = 0m;
                    bool hasPayment = false;
                    using (var cmd = new SqlCommand(
                        @"SELECT b.StudentID, b.InstructorID, b.BookingDate, LTRIM(RTRIM(ISNULL(b.BookingStatus, ''))) AS BookingStatus,
                                 LTRIM(RTRIM(ISNULL(b.PaymentStatus, ''))) AS PaymentStatus, ISNULL(p.PackagePrice, 0) AS Price,
                                 pay.Amount, pay.PaymentID
                          FROM dbo.BOOKING11 b WITH (UPDLOCK)
                          LEFT JOIN dbo.PACKAGEN p ON p.PackageID = b.PackageID
                          OUTER APPLY (SELECT TOP 1 x.PaymentID, x.Amount FROM dbo.PAYMENT11 x
                                       WHERE x.BookingID = b.BookingID ORDER BY x.PaymentID DESC) pay
                          WHERE b.BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                studentId = r["StudentID"] == DBNull.Value ? 0 : Convert.ToInt32(r["StudentID"]);
                                instructorId = r["InstructorID"] == DBNull.Value ? 0 : Convert.ToInt32(r["InstructorID"]);
                                date = Convert.ToDateTime(r["BookingDate"]);
                                status = Convert.ToString(r["BookingStatus"]);
                                payStatus = Convert.ToString(r["PaymentStatus"]);
                                price = Convert.ToDecimal(r["Price"]);
                                hasPayment = r["PaymentID"] != DBNull.Value;
                                paid = hasPayment && r["Amount"] != DBNull.Value ? Convert.ToDecimal(r["Amount"]) : 0m;
                            }
                        }
                    }

                    if (status == null) return "Booking " + bookingId + " was not found.";
                    if (by == ByStudent && studentId != byId) return "You can only cancel your own bookings.";
                    if (by == ByInstructor && instructorId != byId) return "You can only cancel bookings that are assigned to you.";
                    if (string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                        return "This booking is already cancelled.";
                    if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
                        return "A completed lesson can't be cancelled.";
                    if (date.Date < DateTime.Today)
                        return "Past bookings can't be cancelled.";

                    // Money back first
                    decimal refund = hasPayment ? paid : 0m;   // only money that was really paid is refunded

                    using (var cmd = new SqlCommand(
                        @"UPDATE dbo.BOOKING11
                          SET BookingStatus = 'Cancelled', CancelledBy = @by, CancelReason = @why, CancelledOn = GETDATE(),
                              RefundAmount = @ra, RefundedOn = CASE WHEN @ra > 0 THEN GETDATE() ELSE NULL END
                          WHERE BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@by", by);
                        cmd.Parameters.AddWithValue("@why", reason);
                        cmd.Parameters.AddWithValue("@ra", refund);
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();

                    done = "Booking " + bookingId + " on " + date.ToString("yyyy/MM/dd") + " has been cancelled."
                         + (refund > 0 ? " " + SnisDrivingSchool.Models.Fmt.Rand(refund) + " has been refunded"
                                         + (by == ByStudent ? " to you." : " to the student.") : "");
                    return null;
                }
            }
        }

        // Short text for tables, e.g. "Cancelled by instructor"
        public static string StatusText(string bookingStatus, string cancelledBy)
        {
            if (!string.Equals((bookingStatus ?? "").Trim(), "Cancelled", StringComparison.OrdinalIgnoreCase)) return (bookingStatus ?? "").Trim();
            string by = (cancelledBy ?? "").Trim();
            return by == "" ? "Cancelled" : "Cancelled by " + by.ToLowerInvariant();
        }
    }
}
