using System;
using System.Configuration;
using System.Net;
using System.Net.Mail;

namespace SnisDrivingSchool
{
    // Sends system emails using Gmail SMTP when Smtp.User and Smtp.Password are set;
    // otherwise Mailjet is used.
    public static class DemoMail
    {
        public static string Inbox
        {
            get
            {
                string inbox = ConfigurationManager.AppSettings["Reset.Inbox"];
                if (string.IsNullOrWhiteSpace(inbox)) inbox = ConfigurationManager.AppSettings["Smtp.User"];
                return (inbox ?? "").Trim();
            }
        }

        // Returns true when the email was sent. Never throws: the caller shows a fallback instead.
        public static bool Send(string toEmail, string subject, string text, string html)
        {
            try
            {
                string to = (toEmail ?? "").Trim();
                if (to == "") return false;
                // Only maZulu's Gmail SMTP (Smtp.User / Smtp.Password, same as the reset-password emails).
                // No Mailjet: if SMTP is not set up, the Verify page shows the code on screen instead.
                if (!SmtpConfigured()) return false;
                SendWithSmtp(to, subject, text, html);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("DemoMail: " + ex);
                return false;
            }
        }

        private static bool SmtpConfigured()
        {
            string user = ConfigurationManager.AppSettings["Smtp.User"];
            string pass = ConfigurationManager.AppSettings["Smtp.Password"];
            return !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass)
                && !user.StartsWith("YOUR_") && !pass.StartsWith("YOUR_");
        }

        private static void SendWithSmtp(string toEmail, string subject, string text, string html)
        {
            var cfg = ConfigurationManager.AppSettings;
            string user = cfg["Smtp.User"].Trim();
            string pass = cfg["Smtp.Password"].Replace(" ", "");
            string host = string.IsNullOrWhiteSpace(cfg["Smtp.Host"]) ? "smtp.gmail.com" : cfg["Smtp.Host"].Trim();
            int port;
            if (!int.TryParse(cfg["Smtp.Port"], out port)) port = 587;
            string fromName = string.IsNullOrWhiteSpace(cfg["Smtp.FromName"]) ? "Snis Driving Academy" : cfg["Smtp.FromName"].Trim();

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var mail = new MailMessage(new MailAddress(user, fromName), new MailAddress(toEmail)))
            using (var smtp = new SmtpClient(host, port))
            {
                mail.Subject = subject;
                mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(text, null, "text/plain"));
                mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, null, "text/html"));
                smtp.EnableSsl = true;
                smtp.UseDefaultCredentials = false;
                smtp.Credentials = new NetworkCredential(user, pass);
                smtp.Send(mail);
            }
        }
    }
}
