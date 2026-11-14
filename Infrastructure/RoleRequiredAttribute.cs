using System;
using System.Linq;
using System.Web.Mvc;
using System.Web.Routing;

namespace SnisDrivingSchool
{
    // Put [RoleRequired("Manager")] on a controller (or action) to allow only that role.
    // Several roles: [RoleRequired("Manager", "Receptionist")].
    // Anyone else is sent to the login page, so no action needs its own "if (role != ...)" check.
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public class RoleRequiredAttribute : ActionFilterAttribute
    {
        private readonly string[] _roles;

        public RoleRequiredAttribute(params string[] roles)
        {
            _roles = roles;
        }

        // Not logged in at all: go to Account/RequireLogin (remembers the page) instead of Account/Login.
        // Used by the student pages so guests are sent back where they were going after logging in.
        public bool RememberPageForGuests { get; set; }

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            var session = filterContext.HttpContext.Session;
            string role = session == null ? null : session["Role"] as string;
            bool loggedIn = role != null && session["UserId"] != null;

            if (loggedIn && _roles.Contains(role))
            {
                base.OnActionExecuting(filterContext);
                return;
            }

            if (!loggedIn && RememberPageForGuests)
            {
                filterContext.Result = new RedirectToRouteResult(new RouteValueDictionary
                {
                    { "controller", "Account" },
                    { "action", "RequireLogin" },
                    { "returnUrl", filterContext.HttpContext.Request.Url.PathAndQuery }
                });
                return;
            }

            filterContext.Result = new RedirectToRouteResult(new RouteValueDictionary
            {
                { "controller", "Account" },
                { "action", "Login" }
            });
        }
    }
}
