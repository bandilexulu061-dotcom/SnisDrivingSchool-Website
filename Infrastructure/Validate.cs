using System.Text.RegularExpressions;

namespace SnisDrivingSchool
{
    // Field checks shared by every form (registration, staff forms, booking, payment).
    // Each method returns an error message, or null when the value is fine.
    public static class Validate
    {
        public static string Name(string value, string label)
        {
            value = (value ?? "").Trim();
            if (value == "") return label + " is required.";
            if (value.Length < 2 || value.Length > 50) return label + " must be 2 to 50 characters.";
            if (!Regex.IsMatch(value, @"^[\p{L}][\p{L} '\-]*$")) return label + " can only contain letters, spaces, hyphens and apostrophes.";
            return null;
        }

        public static string UserName(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length < 3 || value.Length > 50) return "The username must be 3 to 50 characters.";
            if (!Regex.IsMatch(value, @"^[A-Za-z0-9._\-]+$")) return "The username can only contain letters, numbers, dots, dashes and underscores (no spaces).";
            return null;
        }

        // South African phone number: 0821234567 or +27821234567 (spaces allowed)
        public static string Phone(string value)
        {
            string d = Regex.Replace(value ?? "", @"[\s\-()]", "");
            if (d == "") return "Phone number is required.";
            if (!Regex.IsMatch(d, @"^(0\d{9}|\+27\d{9})$")) return "Enter a valid South African phone number, e.g. 0821234567.";
            return null;
        }

        public static string CleanPhone(string value) { return Regex.Replace(value ?? "", @"[\s\-()]", ""); }

        public static string Email(string value)
        {
            value = (value ?? "").Trim();
            if (value == "") return "Email is required.";
            if (value.Length > 50) return "The email address is too long (50 characters at most).";
            if (!Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")) return "Enter a valid email address, e.g. name@example.com.";
            return null;
        }

        public static string Address(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length > 50) return "The address is too long (50 characters at most).";
            return null;
        }

        public static string Password(string value, string confirm)
        {
            value = value ?? "";
            if (value.Length < 6 || value.Length > 50) return "The password must be 6 to 50 characters.";
            if (!Regex.IsMatch(value, @"[A-Za-z]") || !Regex.IsMatch(value, @"\d")) return "The password must contain at least one letter and one number.";
            if (confirm != null && value != confirm) return "The passwords do not match.";
            return null;
        }

        // First error of a list of checks (null when all are fine)
        public static string First(params string[] errors)
        {
            foreach (var e in errors) if (e != null) return e;
            return null;
        }
    }
}
