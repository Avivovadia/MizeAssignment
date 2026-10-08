using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using SupplierFeed.Api.Data;
using SupplierFeed.Api.Domain;
using SupplierFeed.Api.Services;

namespace SupplierFeed.Api.Controllers;

/// <summary>
/// Translates between HTTP and the orchestrator; every decision is made below this layer.
/// Deliberately not an [ApiController] and the ingest action has no body parameter: automatic model binding
/// and validation would answer a bad body with a 400 before the throttle ever saw the supplier.
/// </summary>
[Route("api/reservations")]
public sealed class ReservationsController : ControllerBase
{
    private const string ServerErrorTitle = "The request could not be processed.";

    private readonly IngestOrchestrator _orchestrator;
    private readonly StatsService _statsService;

    public ReservationsController(IngestOrchestrator orchestrator, StatsService statsService)
    {
        _orchestrator = orchestrator;
        _statsService = statsService;
    }

    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest()
    {
        // The Content-Type is ignored on purpose: whatever the header says, the body is read and parsed ourselves.
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        return Map(_orchestrator.Ingest(body));
    }

    [HttpGet("stats/{supplierId}")]
    public IActionResult Stats(string supplierId) =>
        JsonResponse(StatusCodes.Status200OK, _statsService.Get(supplierId));

    private IActionResult Map(IngestResult result) => result switch
    {
        IngestResult.Processed processed => JsonResponse(
            processed.Detail == IngestDetail.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK,
            new IngestResponse(processed.Status, processed.Detail)),

        IngestResult.Throttled throttled => ThrottledResponse(throttled.Decision),

        IngestResult.Invalid invalid => JsonResponse(StatusCodes.Status400BadRequest, new ErrorResponse(invalid.Errors)),

        IngestResult.Unattributable unattributable => JsonResponse(StatusCodes.Status400BadRequest, new ErrorResponse(unattributable.Errors)),

        IngestResult.Failed => Problem(statusCode: StatusCodes.Status500InternalServerError, title: ServerErrorTitle),

        _ => throw new InvalidOperationException($"Unhandled ingest result {result.GetType().Name}."),
    };

    private IActionResult ThrottledResponse(ThrottleDecision.Throttled decision)
    {
        // The header can only carry whole seconds, so it is rounded up (a client honoring it never retries
        // early); the body keeps the exact milliseconds.
        var retryAfterSeconds = (long)Math.Ceiling(TimeSpan.FromMilliseconds(decision.RetryAfterMs).TotalSeconds);
        Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        return JsonResponse(
            StatusCodes.Status429TooManyRequests,
            new IngestResponse(IngestStatus.Throttled, RetryAfterMs: decision.RetryAfterMs, Limit: decision.Limit, WindowSeconds: decision.WindowSeconds));
    }

    private static JsonResult JsonResponse(int statusCode, object value) =>
        new(value, IngestJson.Options) { StatusCode = statusCode };
}
