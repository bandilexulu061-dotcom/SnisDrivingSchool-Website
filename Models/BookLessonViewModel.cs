using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SnisDrivingSchool.Models
{
    // Same number style as the Packages grid in the desktop system: 1400,00
    public static class Fmt
    {
        public static string Amount(decimal d)
        {
            return d.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
        }
        public static string Rand(decimal d) { return "R" + Amount(d); }
    }

    public class SaBank
    {
        public string Name { get; set; }
        public string BranchCode { get; set; }
        public int MinDigits { get; set; }
        public int MaxDigits { get; set; }
        public string Rule
        {
            get { return MinDigits == MaxDigits ? MinDigits + " digits" : MinDigits + " to " + MaxDigits + " digits"; }
        }
    }

    // Universal branch codes and typical account-number lengths.
    // Edit this one list if a code or length needs changing.
    public static class SaBanks
    {
        public static readonly List<SaBank> All = new List<SaBank>
        {
            new SaBank { Name = "ABSA Bank",                    BranchCode = "632005", MinDigits = 9,  MaxDigits = 10 },
            new SaBank { Name = "African Bank",                 BranchCode = "430000", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "Albaraka Bank",                BranchCode = "800000", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "Bank Zero",                    BranchCode = "888000", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "Bidvest Bank",                 BranchCode = "462005", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "Capitec Bank",                 BranchCode = "470010", MinDigits = 10, MaxDigits = 10 },
            new SaBank { Name = "Discovery Bank",               BranchCode = "679000", MinDigits = 10, MaxDigits = 11 },
            new SaBank { Name = "Finbond Mutual Bank",          BranchCode = "589000", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "First National Bank (FNB)",    BranchCode = "250655", MinDigits = 10, MaxDigits = 11 },
            new SaBank { Name = "GBS Mutual Bank",              BranchCode = "585001", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "Grindrod Bank",                BranchCode = "223626", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "Habib Overseas Bank",          BranchCode = "280001", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "HBZ Bank",                     BranchCode = "587000", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "Investec Bank",                BranchCode = "580105", MinDigits = 10, MaxDigits = 11 },
            new SaBank { Name = "Mercantile Bank",              BranchCode = "450105", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "Nedbank",                      BranchCode = "198765", MinDigits = 10, MaxDigits = 11 },
            new SaBank { Name = "Postbank (SA Post Office)",    BranchCode = "460005", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "Sasfin Bank",                  BranchCode = "683000", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "Standard Bank",                BranchCode = "051001", MinDigits = 9,  MaxDigits = 11 },
            new SaBank { Name = "TymeBank",                     BranchCode = "678910", MinDigits = 11, MaxDigits = 11 },
            new SaBank { Name = "Ubank",                        BranchCode = "431010", MinDigits = 9,  MaxDigits = 11 }
        };

        public static SaBank Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return All.FirstOrDefault(b => string.Equals(b.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public class SelectItem
    {
        public int Id { get; set; }
        public string Text { get; set; }
    }

    public class BookLessonViewModel
    {
        public BookLessonViewModel()
        {
            Packages = new List<SelectItem>();
            Instructors = new List<SelectItem>();
            TimeSlots = new List<SelectItem>();
            MinDate = SnisDrivingSchool.Cancellation.EarliestBookingDate.ToString("yyyy-MM-dd");   // 3 days ahead
            MaxDate = DateTime.Today.AddMonths(2).ToString("yyyy-MM-dd");
            SelectedDate = "";
            BookedMonths = new List<string>();
            PackageLessons = new Dictionary<int, int>();
            LessonDays = new List<string>();
        }

        // Months ("yyyy-MM") that already have a booking: only one booking per month is allowed
        public List<string> BookedMonths { get; set; }

        // Number of lessons in each package (package id -> lessons) and the days the student already has lessons ("yyyy-MM-dd")
        public Dictionary<int, int> PackageLessons { get; set; }
        public List<string> LessonDays { get; set; }

        public string StudentFirstName { get; set; }
        public string StudentLastName { get; set; }

        public List<SelectItem> Packages { get; set; }
        public List<SelectItem> Instructors { get; set; }
        public List<SelectItem> TimeSlots { get; set; }

        public int SelectedPackageId { get; set; }
        public string LicenceStatus { get; set; }
        public int SelectedInstructorId { get; set; }
        public int SelectedTsId { get; set; }
        public string SelectedDate { get; set; }

        public string MinDate { get; set; }
        public string MaxDate { get; set; }
    }

   

    public class MyBookingsViewModel
    {
        public MyBookingsViewModel() { Bookings = new List<BookingRow>(); }
        public string Search { get; set; }
        public List<BookingRow> Bookings { get; set; }
    }

    public class PaymentViewModel
    {
        public BookingRow Booking { get; set; }
        public string StudentName { get; set; }
        public string Reference { get; set; }

        public string Method { get; set; }      // "Card" or "EFT"
        public string CardName { get; set; }    // name on the card (the number and CVC are never kept)
        public string SelectedBank { get; set; }
        public string AccountName { get; set; }
        public string AccountNumber { get; set; }
    }

    public class SummaryReportViewModel
    {
        public SummaryReportViewModel() { Bookings = new List<BookingRow>(); }
        public StudentProfile Student { get; set; }
        public DateTime Generated { get; set; }
        public List<BookingRow> Bookings { get; set; }
        public int PaymentCount { get; set; }
        public decimal TotalPaid { get; set; }
    }

    public class BookingHistoryViewModel
    {
        public BookingHistoryViewModel() { Bookings = new List<BookingRow>(); }
        public StudentProfile Student { get; set; }
        public DateTime Generated { get; set; }
        public List<BookingRow> Bookings { get; set; }
        public int PaidCount { get; set; }
        public int UnpaidCount { get; set; }
        public int CancelledCount { get; set; }
        public bool AutoPrint { get; set; }
    }
}