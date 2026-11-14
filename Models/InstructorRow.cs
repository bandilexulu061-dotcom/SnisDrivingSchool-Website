using System;

namespace SnisDrivingSchool.Models
{
    // One row from dbo.INSTRUCTORNEW (password columns are deliberately not loaded)
    public class InstructorRow
    {
        public int InstructorID { get; set; }
        public string Name { get; set; }
        public string Surname { get; set; }
        public string PhoneNo { get; set; }
        public string Email { get; set; }
        public string UserName { get; set; }
        public string LicenseCode { get; set; }
        public int YearsOfExperience { get; set; }
        public string Status { get; set; }
    }
}
