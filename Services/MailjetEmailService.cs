using System;
using System.Configuration;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SnisDrivingSchool
{
    public static class MailjetEmailService
    {
        private const string SendUrl = "https://api.mailjet.com/v3.1/send";
        private static readonly HttpClient Client;

        static MailjetEmailService()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var apiKey = ConfigurationManager.AppSettings["Mailjet.ApiKey"];
            var secretKey = ConfigurationManager.AppSettings["Mailjet.SecretKey"];
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(apiKey + ":" + secretKey));

            Client = new HttpClient();
            Client.Timeout = TimeSpan.FromSeconds(30);
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        // ---------- Send to ANY recipient ----------
        public static async Task<bool> SendToAsync(
            string toEmail,
            string toName,
            string subject,
            string textBody,
            string htmlBody = null,
            string replyToEmail = null,
            string replyToName = null)
        {
            var settings = ConfigurationManager.AppSettings;

            object replyTo = null;
            if (!string.IsNullOrWhiteSpace(replyToEmail))
            {
                replyTo = new { Email = replyToEmail, Name = replyToName ?? replyToEmail };
            }

            var message = new
            {
                From = new { Email = settings["Mailjet.FromEmail"], Name = settings["Mailjet.FromName"] },
                To = new[] { new { Email = toEmail, Name = toName ?? toEmail } },
                ReplyTo = replyTo,
                Subject = subject,
                TextPart = textBody,
                HTMLPart = htmlBody ?? WebUtility.HtmlEncode(textBody).Replace("\n", "<br/>")
            };

            var payload = new { Messages = new[] { message } };
            var json = JsonConvert.SerializeObject(payload,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

            try
            {
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var response = await Client.PostAsync(SendUrl, content).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        Trace.TraceError("Mailjet error {0}: {1}", (int)response.StatusCode, body);
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("Mailjet exception: " + ex);
                return false;
            }
        }

        // ---------- Contact form: sends to Contact.To (admin inbox) ----------
        public static Task<bool> SendAsync(
            string replyToEmail,
            string replyToName,
            string subject,
            string textBody,
            string htmlBody = null)
        {
            var adminEmail = ConfigurationManager.AppSettings["Contact.To"];
            return SendToAsync(adminEmail, "Snis Driving Academy", subject, textBody, htmlBody, replyToEmail, replyToName);
        }

        // ---------- Password reset PIN email ----------
        public static Task<bool> SendPasswordResetPinAsync(
            string toEmail,
            string toName,
            string pin,
            int validMinutes = 15)
        {
            var subject = "Your Snis Driving Academy password reset PIN";

            var text =
                "Hi " + (toName ?? "there") + ",\n\n" +
                "Your password reset PIN is: " + pin + "\n\n" +
                "It is valid for " + validMinutes + " minutes. " +
                "If you did not request this, you can ignore this email.\n\n" +
                "Snis Driving Academy";

            var html =
                "<div style=\"font-family:Arial,sans-serif;max-width:480px;margin:auto;\">" +
                "<h2>Password reset</h2>" +
                "<p>Hi " + WebUtility.HtmlEncode(toName ?? "there") + ",</p>" +
                "<p>Your password reset PIN is:</p>" +
                "<p style=\"font-size:32px;letter-spacing:6px;font-weight:bold;\">" + WebUtility.HtmlEncode(pin) + "</p>" +
                "<p>It is valid for " + validMinutes + " minutes. If you did not request this, you can ignore this email.</p>" +
                "<p>Snis Driving Academy</p>" +
                "</div>";

            return SendToAsync(toEmail, toName, subject, text, html);
        }

        // ---------- Non-async helpers for controller actions that are not async ----------
        public static bool SendPasswordResetPin(string toEmail, string toName, string pin, int validMinutes = 15)
        {
            return Task.Run(() => SendPasswordResetPinAsync(toEmail, toName, pin, validMinutes))
                       .GetAwaiter().GetResult();
        }

        public static bool SendTo(string toEmail, string toName, string subject, string textBody, string htmlBody = null)
        {
            return Task.Run(() => SendToAsync(toEmail, toName, subject, textBody, htmlBody))
                       .GetAwaiter().GetResult();
        }
    }
}