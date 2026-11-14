using System;

namespace SnisDrivingSchool.Models
{
    // One row from dbo.BOOKING11 (plus the instructor's name from INSTRUCTORNEW)
    public class BookingRow
    {
        public int BookingId { get; set; }
        public int PackageId { get; set; }
        public string PackageName { get; set; }
        public decimal Price { get; set; }
        public int TsId { get; set; }
        public string TimeText { get; set; }
        public int? InstructorId { get; set; }
        public string InstructorName { get; set; }
        public DateTime BookingDate { get; set; }
        public DateTime? EndDate { get; set; }   // last lesson day of the package
        public string DateRange { get { return SnisDrivingSchool.LessonPlan.RangeText(BookingDate, EndDate); } }
        public int LessonCount { get { return SnisDrivingSchool.LessonPlan.LessonsBetween(BookingDate, EndDate); } }
        public string Status { get; set; }

        public int? PaymentId { get; set; }
        public decimal? PaidAmount { get; set; }
        public DateTime? PaidOn { get; set; }
        public string PaymentMethod { get; set; }

        // BOOKING11.PaymentStatus: "Pending" or "Paid" ("Sent" is an old value from before payments were approved immediately)
        public string BookingPaymentStatus { get; set; }

        // A payment only counts once reception has confirmed it (that creates the PAYMENT11 row)
        public bool Paid { get { return PaymentId.HasValue; } }

        // Cancellation (see Infrastructure/Cancellation.cs)
        public string CancelledBy { get; set; }
        public string CancelReason { get; set; }
        public DateTime? CancelledOn { get; set; }
        public decimal RefundAmount { get; set; }
        public bool Refunded { get { return RefundAmount > 0; } }

        // The instructor couldn't take the lesson and another one is proposed: the student must accept or decline
        public string ProposedInstructor { get; set; }
        public string ReplaceReason { get; set; }
        public bool NeedsAnswer { get { return !string.IsNullOrEmpty(ProposedInstructor) && !IsCancelled; } }

        // Cancelled bookings can't be cancelled again; past and completed ones can't be cancelled at all
        public bool CanCancel { get { return !IsCancelled && !IsCompleted && BookingDate.Date >= DateTime.Today; } }

        // The student clicked "Confirm EFT Payment" and is waiting for reception to check the bank
        // Payments are approved by the "bank" the moment the student pays, so nothing waits for confirmation any more.
        // (Kept so older pages and reports still compile; always false.)
        public bool AwaitingConfirmation { get { return false; } }

        // Text for the "Payment" column
        public string PaymentText
        {
            get
            {
                if (Refunded) return "Refunded R" + RefundAmount.ToString("N2");
                if (Paid) return PaymentMethod;
                if (IsCancelled) return "N/A";
                return AwaitingConfirmation ? "Awaiting confirmation" : "Unpaid";
            }
        }

        public bool IsCancelled
        {
            get { return string.Equals(Status, "Cancelled", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsCompleted
        {
            get { return string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase); }
        }

        // An unpaid booking is never shown as "Booked": it stays Pending until paid
        public string DisplayStatus
        {
            get
            {
                if (IsCancelled) return string.IsNullOrEmpty(CancelledBy) ? "Cancelled"
                                      : CancelledBy == "Student" ? "Cancelled by you" : "Cancelled by " + CancelledBy.ToLowerInvariant();
                if (IsCompleted) return "Completed";
                return Paid ? "Booked" : "Pending";
            }
        }
    }
}
