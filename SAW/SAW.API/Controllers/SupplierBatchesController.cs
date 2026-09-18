using Microsoft.AspNetCore.Mvc;

namespace SAW.API.Controllers
{
    public class SupplierBatchesController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
