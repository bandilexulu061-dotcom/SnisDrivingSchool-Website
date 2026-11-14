using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Web.Mvc;
using SnisDrivingSchool.Models;

namespace SnisDrivingSchool.Controllers
{
    // The Reports page (Manager > Reports). Lives in its own file to keep ManagerController short.
    public partial class ManagerController
    {
        // Fixed colours so the same thing is always the same colour on every chart
        private static readonly string[] SeriesColors =
            { "#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767" };

        private static string StatusColor(string status)
        {
            switch ((status ?? "").Trim().ToLowerInvariant())
            {
                case "completed": return "#199e70";
                case "booked": return "#3987e5";
                case "pending": return "#c98500";
                case "cancelled": return "#d95926";
                case "cancelled by student": return "#d95926";
                case "cancelled by instructor": return "#d55181";
                case "request cancel": return "#9085e9";
                default: return "#8b97b5";
            }
        }

        // GET: /Manager/Reports?year=2026&month=0&instructorId=0&studentId=0&packageId=0&bookingStatus=&paymentStatus=&tab=overview
        public ActionResult Reports(int? year, int? month, int? instructorId, int? studentId, int? packageId,
                                    string bookingStatus, string paymentStatus, string tab, string cancelledBy = null)
        {
            var f = new ReportFilter
            {
                Year = year ?? DateTime.Today.Year,
                Month = month ?? 0,
                InstructorId = instructorId ?? 0,
                StudentId = studentId ?? 0,
                PackageId = packageId ?? 0,
                BookingStatus = Clean(bookingStatus),
                PaymentStatus = Clean(paymentStatus),
                CancelledBy = Clean(cancelledBy),
                Tab = string.IsNullOrWhiteSpace(tab) ? "overview" : tab.Trim().ToLowerInvariant()
            };

            var vm = new ReportVm { Filter = f };
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    Cancellation.EnsureColumns(con);
                    LoadReportLists(con, vm);
                    vm.Bookings = LoadReportBookings(con, f);
                    BuildOverview(vm);
                    BuildStudents(con, vm);
                    BuildInstructors(con, vm);
                    BuildVehicles(con, vm);
                    BuildPayments(vm);
                }
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Could not load the reports.", ex);
            }
            return View(vm);
        }

        // ---------- drop-down lists ----------

        private static void LoadReportLists(SqlConnection con, ReportVm vm)
        {
            vm.Years = new List<int>();
            using (var cmd = new SqlCommand("SELECT DISTINCT YEAR(BookingDate) FROM dbo.BOOKING11 WHERE BookingDate IS NOT NULL ORDER BY 1 DESC", con))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) vm.Years.Add(Convert.ToInt32(r[0]));
            if (!vm.Years.Contains(DateTime.Today.Year)) vm.Years.Insert(0, DateTime.Today.Year);

            vm.InstructorList = ReadList(con, "SELECT InstructorID, Name + ' ' + Surname FROM dbo.INSTRUCTORNEW ORDER BY Name, Surname");
            vm.StudentList = ReadList(con, "SELECT StudentID, StudentFirstName + ' ' + StudentLastName FROM dbo.Student1 ORDER BY StudentFirstName, StudentLastName");
            vm.PackageList = ReadList(con, "SELECT PackageID, PackageName FROM dbo.PACKAGEN ORDER BY PackageID");
            for (int i = 0; i < vm.PackageList.Count; i++)
                vm.PackageList[i].Color = SeriesColors[i % SeriesColors.Length];

            vm.StatusList = new List<string>();
            using (var cmd = new SqlCommand(
                "SELECT DISTINCT LTRIM(RTRIM(BookingStatus)) FROM dbo.BOOKING11 WHERE ISNULL(BookingStatus, '') <> '' ORDER BY 1", con))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) vm.StatusList.Add(r.GetString(0));
        }

        private static List<NameValue> ReadList(SqlConnection con, string sql)
        {
            var list = new List<NameValue>();
            using (var cmd = new SqlCommand(sql, con))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new NameValue(Convert.ToString(r[1]).Trim(), Convert.ToInt32(r[0])));
            return list;
        }

        // ---------- the filtered bookings (one query) ----------

        private static List<ReportBooking> LoadReportBookings(SqlConnection con, ReportFilter f)
        {
            var where = new List<string>();
            if (f.Year > 0) where.Add("YEAR(b.BookingDate) = @y");
            if (f.Month > 0) where.Add("MONTH(b.BookingDate) = @m");
            if (f.InstructorId > 0) where.Add("b.InstructorID = @i");
            if (f.StudentId > 0) where.Add("b.StudentID = @s");
            if (f.PackageId > 0) where.Add("b.PackageID = @p");
            if (f.BookingStatus != "") where.Add("LTRIM(RTRIM(b.BookingStatus)) = @bs");
            if (f.CancelledBy != "") where.Add("b.BookingStatus = 'Cancelled' AND b.CancelledBy = @cb");

            string sql = @"
                SELECT b.BookingID, b.BookingDate, LTRIM(RTRIM(ISNULL(b.BookingStatus, ''))) AS BookingStatus,
                       ISNULL(b.StudentID, 0) AS StudentID,
                       COALESCE(NULLIF(LTRIM(RTRIM(s.StudentFirstName + ' ' + s.StudentLastName)), ''),
                                LTRIM(RTRIM(ISNULL(b.StudentFirstName, '') + ' ' + ISNULL(b.StudentLastName, '')))) AS Student,
                       ISNULL(b.InstructorID, 0) AS InstructorID, i.Name + ' ' + i.Surname AS Instructor,
                       ISNULL(b.PackageID, 0) AS PackageID, p.PackageName, ISNULL(p.PackagePrice, 0) AS Price,
                       LTRIM(RTRIM(ISNULL(b.PaymentStatus, ''))) AS BookingPay,
                       pay.PaymentID, pay.Amount, pay.PaymentDate, pay.PaymentStatus AS Method,
                       b.CancelledBy, b.CancelReason, b.CancelledOn, ISNULL(b.RefundAmount, 0) AS RefundAmount
                FROM dbo.BOOKING11 b
                LEFT JOIN dbo.Student1 s      ON s.StudentID    = b.StudentID
                LEFT JOIN dbo.INSTRUCTORNEW i ON i.InstructorID = b.InstructorID
                LEFT JOIN dbo.PACKAGEN p      ON p.PackageID    = b.PackageID
                OUTER APPLY (SELECT TOP 1 x.PaymentID, x.Amount, x.PaymentDate, x.PaymentStatus
                             FROM dbo.PAYMENT11 x WHERE x.BookingID = b.BookingID ORDER BY x.PaymentID DESC) pay"
                + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                + " ORDER BY b.BookingDate DESC, b.BookingID DESC";

            var list = new List<ReportBooking>();
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@y", f.Year);
                cmd.Parameters.AddWithValue("@m", f.Month);
                cmd.Parameters.AddWithValue("@i", f.InstructorId);
                cmd.Parameters.AddWithValue("@s", f.StudentId);
                cmd.Parameters.AddWithValue("@p", f.PackageId);
                cmd.Parameters.AddWithValue("@bs", f.BookingStatus);
                cmd.Parameters.AddWithValue("@cb", f.CancelledBy);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        decimal refund = Convert.ToDecimal(r["RefundAmount"]);
                        bool paid = r["PaymentID"] != DBNull.Value && refund == 0;   // refunded money is not revenue
                        var b = new ReportBooking
                        {
                            BookingId = Convert.ToInt32(r["BookingID"]),
                            Date = r["BookingDate"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(r["BookingDate"]),
                            Status = Get(r, "BookingStatus"),
                            StudentId = Convert.ToInt32(r["StudentID"]),
                            Student = Get(r, "Student"),
                            InstructorId = Convert.ToInt32(r["InstructorID"]),
                            Instructor = Get(r, "Instructor"),
                            PackageId = Convert.ToInt32(r["PackageID"]),
                            Package = Get(r, "PackageName"),
                            Price = Convert.ToDecimal(r["Price"]),
                            Paid = paid,
                            Awaiting = false,   // payments are approved immediately (no "awaiting confirmation" any more)
                            PaidAmount = paid && r["Amount"] != DBNull.Value ? Convert.ToDecimal(r["Amount"]) : 0m,
                            PaidOn = paid && r["PaymentDate"] != DBNull.Value ? (DateTime?)Convert.ToDateTime(r["PaymentDate"]) : null,
                            Method = paid ? Get(r, "Method") : "",
                            CancelledBy = Get(r, "CancelledBy"),
                            CancelReason = Get(r, "CancelReason"),
                            CancelledOn = r["CancelledOn"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["CancelledOn"]),
                            RefundAmount = refund
                        };
                        if (b.Instructor == "") b.Instructor = "Unassigned";
                        if (b.Package == "") b.Package = "Unknown package";
                        if (b.Status == "") b.Status = "Pending";
                        list.Add(b);
                    }
                }
            }

            // Payment status filter (worked out from the payment row, so it is done here)
            if (f.PaymentStatus == "Paid") list = list.Where(b => b.Paid).ToList();
            else if (f.PaymentStatus == "Unpaid") list = list.Where(b => !b.Paid && !b.Awaiting && b.Status != "Cancelled").ToList();
            else if (f.PaymentStatus == "Refunded") list = list.Where(b => b.Refunded).ToList();
            return list;
        }

        // ---------- tabs ----------

        private static void BuildOverview(ReportVm vm)
        {
            var b = vm.Bookings;
            vm.BookingCount = b.Count;
            vm.Revenue = b.Sum(x => x.PaidAmount);
            vm.ActiveStudents = b.Select(x => x.StudentId).Where(id => id > 0).Distinct().Count();
            vm.AwaitingCount = b.Count(x => x.Awaiting);
            vm.AwaitingAmount = b.Where(x => x.Awaiting).Sum(x => x.Price);

            // Per month when one year is chosen; per year when "All years" is chosen
            vm.MonthLabels = new List<string>();
            vm.BookingsPerMonth = new List<int>();
            vm.RevenuePerMonth = new List<decimal>();
            if (vm.Filter.Year > 0)
            {
                // The current year stops at this month, so future months don't show as zero
                int lastMonth = vm.Filter.Year == DateTime.Today.Year ? DateTime.Today.Month : 12;
                for (int m = 1; m <= lastMonth; m++)
                {
                    vm.MonthLabels.Add(CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(m));
                    vm.BookingsPerMonth.Add(b.Count(x => x.Date.Month == m));
                    vm.RevenuePerMonth.Add(b.Where(x => (x.PaidOn ?? x.Date).Month == m).Sum(x => x.PaidAmount));
                }
            }
            else
            {
                foreach (var y in vm.Years.OrderBy(y => y))
                {
                    vm.MonthLabels.Add(y.ToString(CultureInfo.InvariantCulture));
                    vm.BookingsPerMonth.Add(b.Count(x => x.Date.Year == y));
                    vm.RevenuePerMonth.Add(b.Where(x => (x.PaidOn ?? x.Date).Year == y).Sum(x => x.PaidAmount));
                }
            }

            vm.ByStatus = b.GroupBy(x => x.StatusText)
                           .Select(g => new NameValue(g.Key, g.Count(), StatusColor(g.Key)))
                           .OrderByDescending(x => x.Value).ToList();

            vm.ByPackage = new List<NameValue>();
            foreach (var p in vm.PackageList)
            {
                int n = b.Count(x => x.PackageId == (int)p.Value);
                if (n > 0) vm.ByPackage.Add(new NameValue(p.Name, n, p.Color));
            }
        }

        private static void BuildStudents(SqlConnection con, ReportVm vm)
        {
            vm.StudentRows = vm.Bookings.Where(x => x.StudentId > 0).GroupBy(x => x.StudentId).Select(g => new ReportPerson
            {
                Id = g.Key,
                Name = g.First().Student,
                Bookings = g.Count(),
                Completed = g.Count(x => x.Status == "Completed"),
                Cancelled = g.Count(x => x.Status == "Cancelled"),
                Paid = g.Sum(x => x.PaidAmount),
                Owed = g.Where(x => !x.Paid && x.Status != "Cancelled").Sum(x => x.Price),
                LastBooking = g.Max(x => x.Date)
            }).OrderByDescending(x => x.Bookings).ThenBy(x => x.Name).ToList();

            vm.StudentsWithBookings = vm.StudentRows.Count;
            vm.OutstandingTotal = vm.StudentRows.Sum(x => x.Owed);

            vm.StudentStatus = new List<NameValue>();
            using (var cmd = new SqlCommand(
                "SELECT ISNULL(NULLIF(LTRIM(RTRIM([Status])), ''), 'Unknown') AS S, COUNT(*) FROM dbo.Student1 GROUP BY ISNULL(NULLIF(LTRIM(RTRIM([Status])), ''), 'Unknown') ORDER BY 2 DESC", con))
            using (var r = cmd.ExecuteReader())
            {
                int i = 0;
                while (r.Read())
                {
                    string s = r.GetString(0);
                    string color = s.Equals("Active", StringComparison.OrdinalIgnoreCase) ? "#199e70" : SeriesColors[(i + 1) % SeriesColors.Length];
                    vm.StudentStatus.Add(new NameValue(s, Convert.ToInt32(r[1]), color));
                    i++;
                }
            }
        }

        private static void BuildInstructors(SqlConnection con, ReportVm vm)
        {
            // Available slots in a week = time slots x 6 days (Mon-Sat)
            int slots;
            using (var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.TimeSlotNew", con))
                slots = Convert.ToInt32(cmd.ExecuteScalar());
            DateTime monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));

            var weekCounts = new Dictionary<int, int>();
            using (var cmd = new SqlCommand(
                @"SELECT InstructorID, COUNT(*) FROM dbo.BOOKING11
                  WHERE BookingDate >= @from AND BookingDate < @to AND ISNULL(BookingStatus, '') <> 'Cancelled'
                    AND InstructorID IS NOT NULL
                  GROUP BY InstructorID", con))
            {
                cmd.Parameters.AddWithValue("@from", monday);
                cmd.Parameters.AddWithValue("@to", monday.AddDays(7));
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) weekCounts[Convert.ToInt32(r[0])] = Convert.ToInt32(r[1]);
            }

            var ratings = Lessons.Averages(con);
            vm.ReviewCount = ratings.Values.Sum(x => x.Count);
            vm.AverageRating = vm.ReviewCount == 0 ? 0m : ratings.Values.Sum(x => x.Average * x.Count) / vm.ReviewCount;

            vm.InstructorRows = new List<ReportPerson>();
            using (var cmd = new SqlCommand(
                "SELECT InstructorID, Name + ' ' + Surname AS FullName, ISNULL([Status], '') AS St, ISNULL(LicenseCode, '') AS Lc FROM dbo.INSTRUCTORNEW ORDER BY Name, Surname", con))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    int id = Convert.ToInt32(r["InstructorID"]);
                    if (vm.Filter.InstructorId > 0 && vm.Filter.InstructorId != id) continue;
                    var mine = vm.Bookings.Where(x => x.InstructorId == id).ToList();
                    int week;
                    weekCounts.TryGetValue(id, out week);
                    RatingSummary rating;
                    ratings.TryGetValue(id, out rating);
                    vm.InstructorRows.Add(new ReportPerson
                    {
                        Id = id,
                        Name = Get(r, "FullName"),
                        Status = Get(r, "St"),
                        Extra = Get(r, "Lc"),
                        Bookings = mine.Count(x => x.Status != "Cancelled"),
                        Completed = mine.Count(x => x.Status == "Completed"),
                        Cancelled = mine.Count(x => x.Status == "Cancelled"),
                        CancelledByInstructor = mine.Count(x => x.Status == "Cancelled" && x.CancelledBy == Cancellation.ByInstructor),
                        Rating = rating == null ? 0m : rating.Average,
                        RatingCount = rating == null ? 0 : rating.Count,
                        Students = mine.Select(x => x.StudentId).Distinct().Count(),
                        Paid = mine.Sum(x => x.PaidAmount),
                        CapacityPct = slots > 0 ? Math.Min(100, (int)Math.Round(100.0 * week / (slots * 6))) : 0
                    });
                }
            }
            vm.InstructorRows = vm.InstructorRows.OrderByDescending(x => x.Bookings).ThenBy(x => x.Name).ToList();
            vm.InstructorsApproved = vm.InstructorRows.Count(x => x.Status.Equals("Approved", StringComparison.OrdinalIgnoreCase));
            vm.InstructorsPending = vm.InstructorRows.Count(x => x.Status.StartsWith("Pending", StringComparison.OrdinalIgnoreCase));
            vm.CancelledByInstructorCount = vm.Bookings.Count(x => x.Status == "Cancelled" && x.CancelledBy == Cancellation.ByInstructor);
            vm.CancelledByStudentCount = vm.Bookings.Count(x => x.Status == "Cancelled" && x.CancelledBy == Cancellation.ByStudent);
        }

        private static void BuildVehicles(SqlConnection con, ReportVm vm)
        {
            vm.Vehicles = LoadVehicles(con);
            DateTime today = DateTime.Today;
            vm.VehicleStatus = new List<NameValue>
            {
                new NameValue("Available", vm.Vehicles.Count(v => !IsExpired(v) && v.Status == "Available"), "#199e70"),
                new NameValue("In use", vm.Vehicles.Count(v => !IsExpired(v) && v.Status == "In Use"), "#3987e5"),
                new NameValue("Maintenance", vm.Vehicles.Count(v => !IsExpired(v) && v.Status == "Maintenance"), "#c98500"),
                new NameValue("Disk expired", vm.Vehicles.Count(IsExpired), "#d95926")
            }.Where(x => x.Value > 0).ToList();

            int i = 0;
            vm.VehicleCodes = vm.Vehicles.GroupBy(v => v.LicenseCode == "" ? "Unknown" : "Code " + v.LicenseCode)
                .OrderBy(g => g.Key)
                .Select(g => new NameValue(g.Key, g.Count(), SeriesColors[i++ % SeriesColors.Length])).ToList();
        }

        private static bool IsExpired(MgrVehicleRow v)
        {
            return v.DiskExpiry.HasValue && v.DiskExpiry.Value.Date < DateTime.Today;
        }

        private static void BuildPayments(ReportVm vm)
        {
            var b = vm.Bookings;
            vm.PaidCount = b.Count(x => x.Paid);
            var unpaid = b.Where(x => !x.Paid && !x.Awaiting && x.Status != "Cancelled").ToList();
            vm.UnpaidCount = unpaid.Count;
            vm.UnpaidAmount = unpaid.Sum(x => x.Price);
            vm.RefundCount = b.Count(x => x.Refunded);
            vm.RefundTotal = b.Sum(x => x.RefundAmount);

            int i = 0;
            vm.ByMethod = b.Where(x => x.Paid)
                .GroupBy(x => string.IsNullOrWhiteSpace(x.Method) ? "Other" : x.Method)
                .OrderByDescending(g => g.Count())
                .Select(g => new NameValue(g.Key, g.Count(), SeriesColors[i++ % SeriesColors.Length])).ToList();

            vm.PayState = new List<NameValue>
            {
                new NameValue("Paid", vm.PaidCount, "#199e70"),
                new NameValue("Unpaid", vm.UnpaidCount, "#d95926"),
                new NameValue("Refunded", vm.RefundCount, "#9085e9")
            }.Where(x => x.Value > 0).ToList();
        }
    }
}
