using System.ComponentModel.DataAnnotations;

namespace Maddalena.Model;

public sealed class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = "";

    [Required, MinLength(8)]
    public string NewPassword { get; set; } = "";
}
