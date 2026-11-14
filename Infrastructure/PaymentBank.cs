using System;
using System.Data.SqlClient;
using System.Globalization;
using System.Text.RegularExpressions;
using SnisDrivingSchool.Models;

namespace SnisDrivingSchool
{
    // A pretend bank for the project: the student pays by card or EFT and, when the details are valid,
    // the payment is approved immediately (no receptionist confirmation). No real money moves.
    // The card number and CVC are only checked, never stored: only "Card" or "EFT" is saved with the payment.
    public static class PaymentBank
    {
        public const string Card = "Card";
        public const string Eft = "EFT";

        // ---------- checks (the "bank" declines with a reason) ----------

        public static string CardError(string holder, string number, string expiry, string cvc)
        {
            holder = (holder ?? "").Trim();
            string digits = Regex.Replace(number ?? "", @"[\s\-]", "");
            if (holder.Length < 2) return "enter the name on the card.";
            if (!Regex.IsMatch(digits, @"^\d{16}$")) return "the card number must have 16 digits.";
            if (!Luhn(digits)) return "the card number is not valid. Check it and try again.";

            var m = Regex.Match((expiry ?? "").Trim(), @"^(\d{2})\s*/\s*(\d{2})$");
            if (!m.Success) return "enter the expiry date as MM/YY, e.g. 08/28.";
            int month = int.Parse(m.Groups[1].Value), year = 2000 + int.Parse(m.Groups[2].Value);
            if (month < 1 || month > 12) return "the expiry month must be from 01 to 12.";
            var lastDay = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            if (lastDay < DateTime.Today) return "this card has expired.";

            if (!Regex.IsMatch((cvc ?? "").Trim(), @"^\d{3}$")) return "the CVC is the 3 digits on the back of the card.";
            return null;
        }

        public static string EftError(string bankName, string holder, string accountNumber)
        {
            var bank = SaBanks.Find(bankName);
            if (bank == null) return "choose your bank.";
            holder = (holder ?? "").Trim();
            if (holder.Length < 2) return "enter the account holder's name.";
            if (holder.Length > 100) return "the account holder's name is too long.";
            string no = (accountNumber ?? "").Trim();
            if (!Regex.IsMatch(no, @"^\d+$")) return "the account number must contain digits only.";
            if (no.Length < bank.MinDigits || no.Length > bank.MaxDigits)
                return bank.Name + " account numbers have " + bank.Rule + ".";
            return null;
        }

        // Standard card-number check digit (every real card number passes it)
        public static bool Luhn(string digits)
        {
            int sum = 0;
            bool dbl = false;
            for (int i = digits.Length - 1; i >= 0; i--)
            {
                int d = digits[i] - '0';
                if (dbl) { d *= 2; if (d > 9) d -= 9; }
                sum += d;
                dbl = !dbl;
            }
            return sum % 10 == 0;
        }

        public static string LastFour(string number)
        {
            string digits = Regex.Replace(number ?? "", @"\D", "");
            return digits.Length >= 4 ? digits.Substring(digits.Length - 4) : digits;
        }

        // ---------- recording the approved payment ----------

        // Records the payment and makes the booking Booked. Returns an error, or null when paid.
        public static string Pay(int bookingId, int studentId, string method, out decimal amount)
        {
            amount = 0m;
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();
                Cancellation.EnsureColumns(con);
                using (var tx = con.BeginTransaction())
                {
                    string status = null;
                    bool paid = false;
                    DateTime date = DateTime.MinValue;
                    using (var cmd = new SqlCommand(
                        @"SELECT LTRIM(RTRIM(ISNULL(b.BookingStatus, ''))) AS St, b.BookingDate, ISNULL(p.PackagePrice, 0) AS Price,
                                 CASE WHEN EXISTS (SELECT 1 FROM dbo.PAYMENT11 x WHERE x.BookingID = b.BookingID) THEN 1 ELSE 0 END AS Paid
                          FROM dbo.BOOKING11 b WITH (UPDLOCK)
                          LEFT JOIN dbo.PACKAGEN p ON p.PackageID = b.PackageID
                          WHERE b.BookingID = @b AND b.StudentID = @s", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        cmd.Parameters.AddWithValue("@s", studentId);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                status = Convert.ToString(r["St"]);
                                date = Convert.ToDateTime(r["BookingDate"]);
                                amount = Convert.ToDecimal(r["Price"]);
                                paid = Convert.ToInt32(r["Paid"]) == 1;
                            }
                        }
                    }

                    if (status == null) return "Booking not found.";
                    if (paid) return "This booking has already been paid.";
                    if (status == "Cancelled") return "This booking was cancelled, so it can't be paid.";
                    if (status == "Completed") return "This lesson is already completed.";
                    if (date.Date < DateTime.Today) return "This lesson date has passed, so it can't be paid any more.";
                    if (amount <= 0) return "This package has no price set. Please contact the school.";

                    decimal price = amount;
                    Db.InsertRow(con, tx, "PAYMENT11", "PaymentID",
                        "BookingID, Amount, PaymentDate, PaymentStatus",
                        "@b, @a, @d, @m",
                        cmd =>
                        {
                            cmd.Parameters.AddWithValue("@b", bookingId);
                            cmd.Parameters.AddWithValue("@a", price);
                            cmd.Parameters.AddWithValue("@d", DateTime.Now);
                            cmd.Parameters.AddWithValue("@m", method == Card ? Card : Eft);
                        });

                    using (var cmd = new SqlCommand(
                        "UPDATE dbo.BOOKING11 SET BookingStatus = 'Booked', PaymentStatus = 'Paid' WHERE BookingID = @b", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@b", bookingId);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    return null;
                }
            }
        }
    }
}
