using System;

namespace SnisDrivingSchool.Models
{
    // One row from dbo.Vehicle3 (password columns are deliberately not loaded)
    public class VehicleRow
    {
        public int VehicleID { get; set; }
        public string RegistractionNo { get; set; }
        public string Model { get; set; }
        public int? LicenseCode { get; set; }
        public DateTime DiskExpiryDate { get; set; }
        public string VehicleStatus { get; set; }
        public bool Archived { get; set; }
    }
}
