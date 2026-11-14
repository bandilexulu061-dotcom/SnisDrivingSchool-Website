using System;

namespace SnisDrivingSchool.Models
{
    // One row from dbo.Student1 (password columns are deliberately not loaded)
    public class StudentRow
    {
        public int StudentID { get; set; }
        public string StudentFirstName { get; set; }
        public string StudentLastName { get; set; }
        public string StudentPhoneNo { get; set; }
        public string StudentEmailAdress { get; set; }
        public string StudentAdress { get; set; }
        public string StudentUserName { get; set; }
        public string Status { get; set; }
        public string IdNumber { get; set; }
        public string LicenceStatus { get; set; }
        public int DocumentId { get; set; }
        public int IdDocumentId { get; set; }
        public string Stage { get; set; }   // Verify / Waiting / Approved / Rejected (see StudentApproval)
        public string ApprovalNote { get; set; }
    }
}
