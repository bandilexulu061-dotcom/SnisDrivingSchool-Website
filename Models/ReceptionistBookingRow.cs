using System;

namespace SnisDrivingSchool.Models
{
    // Receptionist view of one row from dbo.BOOKING11 (plus the instructor's name from INSTRUCTORNEW)
    public class ReceptionistBookingRow
    {
        public int BookingID { get; set; }
        public int? AdminStaffID { get; set; }
        public int StudentID { get; set; }
        public int PackageID { get; set; }
        public int TSID { get; set; }
        public DateTime BookingDate { get; set; }
        public string BookingStatus { get; set; }
        public string CancelReason { get; set; }
        public string ProposedName { get; set; }   // new instructor proposed, waiting for the student
        public int? InstructorID { get; set; }
        public string InstructorName { get; set; }
        public string StudentFirstName { get; set; }
        public string StudentLastName { get; set; }
        public string PaymentStatus { get; set; }
        public string PackageName { get; set; }
        public string TimeText { get; set; }
    }
}
