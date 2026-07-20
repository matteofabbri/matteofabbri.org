using System.ComponentModel.DataAnnotations;

namespace Maddalena.Model;

public sealed class LlmTeachRequest
{
    [Required, MaxLength(100_000)]
    public string Text { get; set; } = "";
}
