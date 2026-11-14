using System;
using System.Data.SqlClient;
using System.Web;
using System.Web.Mvc;

namespace SnisDrivingSchool.Controllers
{
    // Licence and ID PDFs (dbo.StudentDocuments).
    // Manager and receptionist can open any learner's PDF and upload one for them;
    // a student can only open their own (they upload theirs on the Profile page).
    [RoleRequired("Manager", "Receptionist", "Student")]
    public class DocumentsController : Controller
    {
        private bool IsStaff
        {
            get { string r = Session["Role"] as string; return r == "Manager" || r == "Receptionist"; }
        }

        // GET /Documents/Open/5 : opens the PDF in the browser
        public ActionResult Open(int id)
        {
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    Licence.EnsureColumns(con);
                    using (var cmd = new SqlCommand("SELECT StudentID, FileName, FileData FROM dbo.StudentDocuments WHERE DocID = @id", con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) return HttpNotFound("Document not found.");
                            int owner = Convert.ToInt32(r["StudentID"]);
                            if (!IsStaff && owner != Convert.ToInt32(Session["UserId"]))
                                return new HttpStatusCodeResult(403, "You can only view your own documents.");

                            string name = Convert.ToString(r["FileName"]);
                            Response.AppendHeader("Content-Disposition", "inline; filename=\"" + name.Replace("\"", "") + "\"");
                            return File((byte[])r["FileData"], "application/pdf");
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                return Content(Db.Friendly("Could not open the document.", ex));
            }
        }

        // GET /Documents/Student/5 : the current licence PDF of a student
        public ActionResult Student(int id)
        {
            if (!IsStaff && id != Convert.ToInt32(Session["UserId"]))
                return new HttpStatusCodeResult(403, "You can only view your own documents.");
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    int docId = Licence.CurrentDocumentId(con, id);
                    if (docId == 0) return HttpNotFound("No licence document has been uploaded for this student.");
                    return RedirectToAction("Open", new { id = docId });
                }
            }
            catch (SqlException ex)
            {
                return Content(Db.Friendly("Could not open the document.", ex));
            }
        }

        // POST /Documents/Upload : staff record a learner's licence and its PDF (e.g. for students who registered earlier)
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RoleRequired("Manager", "Receptionist")]
        public ActionResult Upload(int studentId, string licenceStatus, HttpPostedFileBase document, string docType = null)
        {
            string back = (Session["Role"] as string) == "Manager" ? "Manager" : "Receptionist";
            if (docType == Licence.IdDocType) return UploadId(studentId, document, back);
            licenceStatus = Licence.Normalize(licenceStatus);

            byte[] data = null;
            string fileName = null;
            string error = Licence.NeedsDocument(licenceStatus) ? Licence.ReadPdf(document, out data, out fileName) : null;
            try
            {
                using (var con = new SqlConnection(Db.ConnStr))
                {
                    con.Open();
                    Licence.EnsureColumns(con);
                    if (error == null) error = Licence.StatusError(licenceStatus, SaIdNumber.Parse(SaIdNumber.ForStudent(con, studentId)));
                    if (error == null)
                    {
                        using (var tx = con.BeginTransaction())
                        {
                            using (var cmd = new SqlCommand("UPDATE dbo.Student1 SET LicenceStatus = @l WHERE StudentID = @id", con, tx))
                            {
                                cmd.Parameters.AddWithValue("@l", licenceStatus);
                                cmd.Parameters.AddWithValue("@id", studentId);
                                if (cmd.ExecuteNonQuery() == 0) error = "That student could not be found.";
                            }
                            if (error == null)
                            {
                                if (data != null) Licence.SaveDocument(con, tx, studentId, licenceStatus, fileName, data);
                                tx.Commit();
                            }
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                error = Db.Friendly("Could not save the document.", ex);
            }

            if (error != null) TempData["Error"] = "Student " + studentId + ": " + error;
            else TempData["Success"] = "Licence (" + Licence.StatusText(licenceStatus) + ")" + (data != null ? " and PDF" : "") + " saved for student " + studentId + ".";
            return RedirectToAction("Students", back);
        }

        // Staff upload (or replace) a learner's ID document
        private ActionResult UploadId(int studentId, HttpPostedFileBase document, string back)
        {
            byte[] data;
            string fileName;
            string error = Licence.ReadPdf(document, out data, out fileName, "the ID document");
            try
            {
                if (error == null)
                {
                    using (var con = new SqlConnection(Db.ConnStr))
                    {
                        con.Open();
                        Licence.EnsureColumns(con);
                        using (var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Student1 WHERE StudentID = @id", con))
                        {
                            cmd.Parameters.AddWithValue("@id", studentId);
                            if ((int)cmd.ExecuteScalar() == 0) error = "That student could not be found.";
                        }
                        if (error == null) Licence.SaveIdDocument(con, null, studentId, fileName, data);
                    }
                }
            }
            catch (SqlException ex)
            {
                error = Db.Friendly("Could not save the document.", ex);
            }

            if (error != null) TempData["Error"] = "Student " + studentId + ": " + error;
            else TempData["Success"] = "ID document saved for student " + studentId + ".";
            return RedirectToAction("Students", back);
        }
    }
}
