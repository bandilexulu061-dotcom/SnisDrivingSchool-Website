using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Web.Mvc;
using SnisDrivingSchool.Models;

namespace SnisDrivingSchool.Controllers
{
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            return View();
        }

        public ActionResult About()
        {
            return View();
        }

        public ActionResult Contact()
        {
            return View(new ContactViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Contact(ContactViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            model.Name = (model.Name ?? "").Trim();
            model.Email = (model.Email ?? "").Trim();
            model.Subject = (model.Subject ?? "").Trim();
            model.Message = (model.Message ?? "").Trim();

            if (model.Name.Length > 100 || model.Email.Length > 150 || model.Subject.Length > 200 || model.Message.Length > 4000)
            {
                ViewBag.Error = "One of the boxes is too long. Please shorten your message and try again.";
                return View(model);
            }

            // 1. Always keep a copy in the database so no message is lost
            try
            {
                SaveMessage(model);
            }
            catch (SqlException ex)
            {
                ViewBag.Error = Db.Friendly("Sorry, we could not send your message right now.", ex);
                return View(model);
            }

            // 2. Also email it to the school
            TrySendEmail(model);

            ViewBag.Success = "Thank you! Your message has been sent. We will get back to you soon.";
            ModelState.Clear();
            return View(new ContactViewModel());
        }

        private static void SaveMessage(ContactViewModel m)
        {
            using (var con = new SqlConnection(Db.ConnStr))
            {
                con.Open();

                // Creates the table the first time a message is sent
                using (var cmd = new SqlCommand(@"
                    IF OBJECT_ID('dbo.ContactMessages', 'U') IS NULL
                    CREATE TABLE dbo.ContactMessages(
                        MessageID   INT IDENTITY(1,1) PRIMARY KEY,
                        SenderName  VARCHAR(100)  NOT NULL,
                        SenderEmail VARCHAR(150)  NOT NULL,
                        Subject     VARCHAR(200)  NOT NULL,
                        Message     VARCHAR(4000) NOT NULL,
                        SentOn      DATETIME      NOT NULL DEFAULT GETDATE(),
                        IsRead      BIT           NOT NULL DEFAULT 0
                    );", con))
                {
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = new SqlCommand(
                    @"INSERT INTO dbo.ContactMessages (SenderName, SenderEmail, Subject, Message)
                      VALUES (@n, @e, @s, @m)", con))
                {
                    cmd.Parameters.AddWithValue("@n", m.Name);
                    cmd.Parameters.AddWithValue("@e", m.Email);
                    cmd.Parameters.AddWithValue("@s", m.Subject);
                    cmd.Parameters.AddWithValue("@m", m.Message);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // Emails the message to the school inbox (Contact.To in Web.config) through Mailjet.
        // If sending fails, the message is still safe in dbo.ContactMessages.
        private static void TrySendEmail(ContactViewModel m)
        {
            try
            {
                MailjetEmailService.SendAsync(m.Email, m.Name, "[Website] " + m.Subject,
                        "From: " + m.Name + " <" + m.Email + ">\n\n" + m.Message)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Email is a bonus; the saved database copy is the record.
            }
        }
    }
}
