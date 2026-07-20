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
        return Ok(new LlmOdometerResponse
        {
            ParameterCount = n.ParameterCount,
            Trained = Digits(n.Trained),
            Evaluated = Digits(n.Evaluated),
            Absorbed = Digits(n.Absorbed),
            Prompted = Digits(n.Prompted),
            Generated = Digits(n.Generated),
            Total = Digits(n.Trained + n.Evaluated + n.Absorbed + n.Prompted + n.Generated),
        });
    }

    // BigInteger -> cifre decimali pure, cultura invariante (niente separatori).
    private static string Digits(System.Numerics.BigInteger value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

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
