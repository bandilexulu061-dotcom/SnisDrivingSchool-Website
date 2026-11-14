using System;
using System.Collections.Generic;

namespace SnisDrivingSchool.Models
{
    // What the manager picked in the Reports filter sidebar (all optional)
    public class ReportFilter
    {
        public int Year { get; set; }            // 0 = all years
        public int Month { get; set; }           // 0 = all months
        public int InstructorId { get; set; }    // 0 = all
        public int StudentId { get; set; }       // 0 = all
        public int PackageId { get; set; }       // 0 = all
        public string BookingStatus { get; set; } // "" = all
        public string PaymentStatus { get; set; } // "", "Paid", "Unpaid", "Refunded"
        public string CancelledBy { get; set; }   // "", "Student", "Instructor"
        public string Tab { get; set; }          // overview, students, instructors, vehicles, payments
    }

    // One booking with everything the reports need (loaded once, then grouped in C#)
    public class ReportBooking
    {
        public int BookingId { get; set; }
        public DateTime Date { get; set; }
        public string Status { get; set; }
        public int StudentId { get; set; }
        public string Student { get; set; }
        public int InstructorId { get; set; }
        public string Instructor { get; set; }
        public int PackageId { get; set; }
        public string Package { get; set; }
        public decimal Price { get; set; }
        public bool Paid { get; set; }
        public bool Awaiting { get; set; }
        public decimal PaidAmount { get; set; }
        public DateTime? PaidOn { get; set; }
        public string Method { get; set; }

        // Cancellation: who, why, and the money given back (a refunded booking is not counted as revenue)
        public string CancelledBy { get; set; }
        public string CancelReason { get; set; }
        public DateTime? CancelledOn { get; set; }
        public decimal RefundAmount { get; set; }
        public bool Refunded { get { return RefundAmount > 0; } }

        // "Cancelled by student" / "Cancelled by instructor" instead of just "Cancelled"
        public string StatusText { get { return SnisDrivingSchool.Cancellation.StatusText(Status, CancelledBy); } }

        public string PayText { get { return Refunded ? "Refunded" : Paid ? "Paid" : Awaiting ? "Awaiting confirmation" : (Status == "Cancelled" ? "N/A" : "Unpaid"); } }
    }

    // Numbers on the reception home page
    public class RecHome
    {
        public int ThisWeek { get; set; }
        public int Today { get; set; }
        public int Students { get; set; }
        public int Waiting { get; set; }
        public int Staff { get; set; }
        public int OnLeave { get; set; }
        public int Vehicles { get; set; }
        public int DisksDue { get; set; }
        public int Payments { get; set; }
        public decimal PaidThisMonth { get; set; }
    }

    // Receptionist "who paid" report
    public class ReceptionPaymentsReport
    {
        public ReceptionPaymentsReport() { Payments = new List<ReceptionPaymentRow>(); }
        public int Year { get; set; }
        public int Month { get; set; }   // 0 = whole year
        public List<ReceptionPaymentRow> Payments { get; set; }
        public int UnpaidCount { get; set; }
        public decimal UnpaidAmount { get; set; }
    }

    public class ReceptionPaymentRow
    {
        public int PaymentId { get; set; }
        public DateTime PaidOn { get; set; }
        public decimal Amount { get; set; }
        public string Method { get; set; }
        public int BookingId { get; set; }
        public DateTime LessonDate { get; set; }
        public decimal Refund { get; set; }
        public string Student { get; set; }
        public string Package { get; set; }
    }

    public class NameValue
    {
        public string Name { get; set; }
        public decimal Value { get; set; }
        public string Color { get; set; }
        public NameValue() { }
        public NameValue(string name, decimal value, string color = null) { Name = name; Value = value; Color = color; }
    }

    public class ReportPerson
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string Extra { get; set; }      // licence code for instructors
        public int Bookings { get; set; }
        public int Completed { get; set; }
        public int Cancelled { get; set; }
        public int CancelledByInstructor { get; set; }
        public decimal Rating { get; set; }        // instructors: average stars (all time)
        public int RatingCount { get; set; }
        public int Students { get; set; }
        public decimal Paid { get; set; }
        public decimal Owed { get; set; }
        public int CapacityPct { get; set; }   // instructors: this week's booked slots / available slots
        public DateTime? LastBooking { get; set; }
    }

    public class ReportVm
    {
        public ReportFilter Filter { get; set; }

        // drop-down lists
        public List<int> Years { get; set; }
        public List<NameValue> InstructorList { get; set; }   // Name, Value = id
        public List<NameValue> StudentList { get; set; }
        public List<NameValue> PackageList { get; set; }
        public List<string> StatusList { get; set; }

        public List<ReportBooking> Bookings { get; set; }

        // overview
        public int BookingCount { get; set; }
        public decimal Revenue { get; set; }
        public int ActiveStudents { get; set; }
        public int InstructorsApproved { get; set; }
        public int InstructorsPending { get; set; }
        public int AwaitingCount { get; set; }
        public decimal AwaitingAmount { get; set; }
        public List<string> MonthLabels { get; set; }
        public List<int> BookingsPerMonth { get; set; }
        public List<decimal> RevenuePerMonth { get; set; }
        public List<NameValue> ByStatus { get; set; }
        public List<NameValue> ByPackage { get; set; }

        // students
        public List<ReportPerson> StudentRows { get; set; }
        public List<NameValue> StudentStatus { get; set; }
        public int StudentsWithBookings { get; set; }
        public decimal OutstandingTotal { get; set; }

        // instructors
        public List<ReportPerson> InstructorRows { get; set; }
        public int CancelledByInstructorCount { get; set; }
        public decimal AverageRating { get; set; }   // all instructors, all time
        public int ReviewCount { get; set; }
        public int CancelledByStudentCount { get; set; }

        // vehicles
        public List<MgrVehicleRow> Vehicles { get; set; }
        public List<NameValue> VehicleStatus { get; set; }
        public List<NameValue> VehicleCodes { get; set; }

        // payments
        public int PaidCount { get; set; }
        public int UnpaidCount { get; set; }
        public decimal UnpaidAmount { get; set; }
        public List<NameValue> ByMethod { get; set; }
        public List<NameValue> PayState { get; set; }
        public int RefundCount { get; set; }
        public decimal RefundTotal { get; set; }
    }
}
