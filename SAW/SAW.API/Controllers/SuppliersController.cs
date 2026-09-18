using Microsoft.AspNetCore.Mvc;

namespace SAW.API.Controllers
{
    public class SuppliersController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
