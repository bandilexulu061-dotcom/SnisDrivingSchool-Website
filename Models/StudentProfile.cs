using System;
using System.ComponentModel.DataAnnotations;

namespace SnisDrivingSchool.Models
{
    public class StudentProfile
    {
        public int StudentId { get; set; }
        public string UserName { get; set; }

        [Required(ErrorMessage = "Name is required.")]
        [StringLength(50)]
        public string Name { get; set; }

        [Required(ErrorMessage = "Surname is required.")]
        [StringLength(50)]
        public string Surname { get; set; }

        [Required(ErrorMessage = "Phone number is required.")]
        [StringLength(20)]
        [Display(Name = "Phone number")]
        public string PhoneNumber { get; set; }

        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; }

        public string Address { get; set; }

        [StringLength(50, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters.")]
        [Display(Name = "New password")]
        public string NewPassword { get; set; }

        public string Status { get; set; }

        // South African ID number (13 digits). Can be added or corrected until it is valid, then it is locked.
        [Display(Name = "ID number")]
        public string IdNumber { get; set; }

        // Licence the learner has now (see Infrastructure/Licence.cs)
        [Display(Name = "Current licence")]
        public string LicenceStatus { get; set; }

        // Newest PDF for the current licence (0 = not uploaded yet)
        public int DocumentId { get; set; }
        public int IdDocumentId { get; set; }
    }
}