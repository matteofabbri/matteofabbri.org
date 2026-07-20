namespace Maddalena.Model;

public sealed class LlmGenerateResponse
{
    /// <summary>Il testo nuovo (senza il prompt).</summary>
    public string Text { get; set; } = "";

    /// <summary>Forward pagati per generarlo (con la decodifica self-speculative sono meno dei token).</summary>
    public int Forwards { get; set; }

    public int Drafted { get; set; }

    public int Accepted { get; set; }
}
