using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TheQult.Pages;

// Base di tutte le pagine: il membro collegato arriva dal cookie di sessione
// (claim NameIdentifier = Id del profilo su Sperone, claim Name = nome pubblico).
public abstract class TheQultPageModel : PageModel
{
    protected string? MemberId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    protected string? MemberName => User.FindFirstValue(ClaimTypes.Name);
}
