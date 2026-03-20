using Microsoft.AspNetCore.Mvc;

namespace CorpMsg.Controllers
{
    public class CompanyController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
