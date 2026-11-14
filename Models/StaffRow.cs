using System;

namespace SnisDrivingSchool.Models
{
    // One row from dbo.AdminStaff11 (password columns are deliberately not loaded)
    public class StaffRow
    {
        public int AdminStaffID { get; set; }
        public string Name { get; set; }
        public string Surname { get; set; }
        public string AdminStaffRole { get; set; }
        public string AdminStaffNo { get; set; }
        public string AdminStaffEmail { get; set; }
        public string UserName { get; set; }
        public string OfficeNo { get; set; }
    }
}
