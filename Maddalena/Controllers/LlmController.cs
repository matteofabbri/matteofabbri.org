using Maddalena.Llm;
using Maddalena.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maddalena.Controllers;

// L'LLM di predictive coding del sito (singleton LlmService, caricato e
// trainato dal LlmWorker) esposto come API JSON: lo stato per chiunque,
// l'interrogazione per chiunque, l'ISTRUZIONE solo per gli account loggati —
// i pesi si aggiornano davvero, e un modello che chiunque puo' istruire
// e' un modello che chiunque puo' avvelenare.
[ApiController]
[Route("api/llm")]
public sealed class LlmController(LlmService llm, LlmCrawlStore crawlStore) : ControllerBase
{
    // Dove sta l'LLM adesso: stato, salvataggi, corpus dell'ultimo training,
    // odometro e coda del crawler. E' la chiamata con cui la SPA decide cosa mostrare.
    [HttpGet("status")]
    [ProducesResponseType<LlmStatusResponse>(StatusCodes.Status200OK)]
    public async Task<LlmStatusResponse> Status(CancellationToken cancellationToken)
    {
        var numbers = llm.Numbers();
        var response = new LlmStatusResponse
        {
            State = llm.State.ToString().ToLowerInvariant(),
            Backend = PredictiveCoding.Device.DeviceRegistry.CudaEnabled ? "cuda" : "cpu",
            Resumed = llm.Resumed,
            LastSavedAt = llm.LastSavedAt,
            CorpusSummary = llm.CorpusSummary,
            LastError = llm.LastError,
            ParameterCount = numbers?.ParameterCount,
            TokensTrained = numbers is { } n1 ? Digits(n1.Trained) : null,
            TokensAbsorbed = numbers is { } n2 ? Digits(n2.Absorbed) : null,
            TokensPrompted = numbers is { } n3 ? Digits(n3.Prompted) : null,
            TokensGenerated = numbers is { } n4 ? Digits(n4.Generated) : null,
            PagesLearned = llm.PagesLearned,
        };

        // La coda vive su Postgres: se la tabella non c'e' ancora (crawler
        // spento, primo avvio) i contatori restano null, non e' un errore.
        try
        {
            response.QueuePending = await crawlStore.PendingCountAsync(cancellationToken);
            response.QueueKnown = await crawlStore.KnownCountAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // tabella assente o DB momentaneamente giu': lo status resta utile.
        }

        return response;
    }

    // L'odometro da solo, per chi vuole solo i numeri di percorrenza.
    [HttpGet("odometer")]
    [ProducesResponseType<LlmOdometerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Odometer()
    {
        var numbers = llm.Numbers();
        if (numbers is null)
            return NotReady(new LlmNotReadyException(llm.State));

        var n = numbers.Value;
        // Niente Trained: l'addestramento d'avvio una-tantum non e' "strada percorsa",
        // fuori dall'odometro. Il totale e' la somma delle sole voci mostrate.
        var total = n.Absorbed + n.Evaluated + n.Prompted + n.Generated;

        return Ok(new LlmOdometerResponse
        {
            TotalTokens = Digits(total),
            TotalHuman = Human(total),
            Counters =
            [
                Entry("absorbed", "Imparati dal vivo (crawler + teach)", n.Absorbed, live: true,
                    "Token letti e imparati mentre gira: pagine dal crawler e testi insegnati."),
                Entry("evaluated", "Valutati (misura di qualità)", n.Evaluated, live: true,
                    "Token usati per misurare quanto è bravo, prima e dopo ogni lezione."),
                Entry("prompted", "Letti come richiesta (generazione)", n.Prompted, live: true,
                    "Token dei prompt che legge quando generi una risposta."),
                Entry("generated", "Prodotti (generazione)", n.Generated, live: true,
                    "Token che scrive generando una risposta."),
            ],
        });
    }

    // BigInteger -> cifre decimali pure, cultura invariante (niente separatori).
    private static string Digits(System.Numerics.BigInteger value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Costruisce una voce dell'odometro autoesplicativa (valore preciso + leggibile).
    private static LlmOdometerEntry Entry(
        string key, string label, System.Numerics.BigInteger tokens, bool live, string explanation) =>
        new()
        {
            Key = key,
            Label = label,
            Tokens = Digits(tokens),
            TokensHuman = Human(tokens),
            Live = live,
            Explanation = explanation,
        };

    // BigInteger -> stringa breve per l'occhio: "1.234", "45,6 mila", "101,6 mln", "1,2 mld".
    private static string Human(System.Numerics.BigInteger v)
    {
        (long Div, string Unit)[] scales =
        [
            (1_000_000_000_000L, "bln"),
            (1_000_000_000L,     "mld"),
            (1_000_000L,         "mln"),
            (1_000L,             "mila"),
        ];
        foreach (var (div, unit) in scales)
            if (v >= div)
                return $"{(double)v / div:0.0} {unit}".Replace('.', ',');
        return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Interroga: prompt dentro, continuazione fuori (decodifica greedy,
    // self-speculative quando il modello ha la testa t+2).
    [HttpPost("generate")]
    [ProducesResponseType<LlmGenerateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Generate(LlmGenerateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await llm.GenerateAsync(request.Prompt, request.MaxNewTokens, cancellationToken);
            return Ok(new LlmGenerateResponse
            {
                Text = result.Text,
                Forwards = result.Forwards,
                Drafted = result.Drafted,
                Accepted = result.Accepted,
            });
        }
        catch (LlmNotReadyException ex)
        {
            return NotReady(ex);
        }
        catch (ArgumentException ex)
        {
            // Il prompt non sta nella finestra del modello: colpa della richiesta.
            return BadRequest(new ErrorResponse { Message = ex.Message });
        }
    }

    // Istruisci: il modello assorbe il testo ADESSO (apprendimento online) e lo
    // stato va subito su disco. La risposta dice quanto era sorprendente il testo
    // prima e dopo: se la seconda e' piu' bassa, la lezione e' entrata.
    [HttpPost("teach")]
    [Authorize]
    [ProducesResponseType<LlmTeachResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Teach(LlmTeachRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var report = await llm.TeachAsync(request.Text, cancellationToken);
            return Ok(new LlmTeachResponse
            {
                SurpriseBefore = report.SurpriseBefore,
                SurpriseAfter = report.SurpriseAfter,
                Steps = report.Steps,
                SavedAt = llm.LastSavedAt,
            });
        }
        catch (LlmNotReadyException ex)
        {
            return NotReady(ex);
        }
    }

    private ObjectResult NotReady(LlmNotReadyException ex) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponse { Message = ex.Message });
}
