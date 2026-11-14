using System;
using System.Collections.Generic;
using System.Linq;

namespace SnisDrivingSchool.Models
{
    // Student home page (dashboard)
    public class StudentHomeViewModel
    {
        public StudentHomeViewModel() { Bookings = new List<BookingRow>(); }

        public StudentProfile Profile { get; set; }
        public List<BookingRow> Bookings { get; set; }
        public int IdDocumentId { get; set; }
        public int LicenceDocumentId { get; set; }
        public int ToRateCount { get; set; }   // completed lessons not rated yet
        public SnisDrivingSchool.StudentApproval.State Account { get; set; }   // verify / approval status

        public List<BookingRow> Upcoming
        {
            get
            {
                return Bookings.Where(b => !b.IsCancelled && !b.IsCompleted && b.BookingDate.Date >= DateTime.Today)
                               .OrderBy(b => b.BookingDate).ThenBy(b => b.TimeText).ToList();
            }
        }

        public BookingRow NextLesson { get { return Upcoming.FirstOrDefault(); } }

        // Bookings still waiting for the student to pay (not paid, EFT not sent, not cancelled)
        public List<BookingRow> ToPay
        {
            get { return Bookings.Where(b => !b.Paid && !b.AwaitingConfirmation && !b.IsCancelled && !b.IsCompleted).ToList(); }
        }

        public int CompletedCount { get { return Bookings.Count(b => b.IsCompleted); } }
    }

    // Rate & review page
    public class StudentReviewsViewModel
    {
        public StudentReviewsViewModel()
        {
            ToRate = new List<BookingRow>();
            Mine = new List<SnisDrivingSchool.LessonReview>();
        }
        public List<BookingRow> ToRate { get; set; }                     // completed, not rated yet
        public List<SnisDrivingSchool.LessonReview> Mine { get; set; }   // already rated
    }

    // Shared look for the student pages: badge colours for booking and payment status
    public static class StudentUi
    {
        public static string StatusCss(BookingRow b)
        {
            if (b.IsCancelled) return "st-bad";
            if (b.IsCompleted) return "st-info";
            return b.Paid ? "st-ok" : "st-warn";
        }

        public static string PayCss(BookingRow b)
        {
            if (b.Refunded) return "st-info";
            if (b.Paid) return "st-ok";
            if (b.IsCancelled) return "st-muted";
            return b.AwaitingConfirmation ? "st-warn" : "st-bad";
        }

        public static string PayText(BookingRow b)
        {
            if (b.Refunded) return "Refunded";
            if (b.Paid) return "Paid";
            if (b.IsCancelled) return "Not needed";
            return b.AwaitingConfirmation ? "Being checked" : "Not paid";
        }

        // Which tab a booking belongs to on "My bookings"
        public static string Group(BookingRow b)
        {
            if (b.IsCancelled || b.IsCompleted || b.BookingDate.Date < DateTime.Today) return "past";
            return (!b.Paid && !b.AwaitingConfirmation) ? "topay upcoming" : "upcoming";
        }

        public static string Initials(string name, string surname)
        {
            string a = (name ?? "").Trim(), b = (surname ?? "").Trim();
            return ((a.Length > 0 ? a.Substring(0, 1) : "") + (b.Length > 0 ? b.Substring(0, 1) : "")).ToUpperInvariant();
        }

        public static string Greeting()
        {
            int h = DateTime.Now.Hour;
            return h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening";
        }
    }
}
