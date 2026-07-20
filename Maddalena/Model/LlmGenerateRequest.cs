using System.ComponentModel.DataAnnotations;

namespace Maddalena.Model;

public sealed class LlmGenerateRequest
{
    [Required, MaxLength(4000)]
    public string Prompt { get; set; } = "";

    /// <summary>Quanti token nuovi generare (la finestra del modello resta il tetto vero).</summary>
    [Range(1, 128)]
    public int MaxNewTokens { get; set; } = 48;
}
