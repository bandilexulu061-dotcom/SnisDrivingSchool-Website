using System.Web.Mvc;

namespace SnisDrivingSchool.Controllers
{
    // Managers and receptionists only
    [RoleRequired("Manager", "Receptionist")]
    public class AdminStaffController : Controller
    {
        public ActionResult Index()
        {
            return View();
        }
    }
}