using Microsoft.AspNetCore.Mvc;

namespace Maddalena.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();
}
