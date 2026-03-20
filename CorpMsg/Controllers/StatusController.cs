using Microsoft.AspNetCore.Mvc;

namespace CorpMsg.Controllers
{
    public class StatusController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
