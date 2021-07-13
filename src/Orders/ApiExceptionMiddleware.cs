namespace Orders;
public sealed class ApiExceptionMiddleware(RequestDelegate next, ILoggerFactory logs)
{
    private readonly ILogger logger = logs.CreateLogger("Orders.ApiExceptionMiddleware");
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Cache-Control"] = "no-store";
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = 499;
        }
        catch (InvalidOrder error) { await ApiProblems.Write(context, 400, "Invalid order or Idempotency-Key", "invalid_order", error.Errors); }
        catch (IdempotencyConflict) { await ApiProblems.Write(context, 409, "Idempotency-Key already used for different content", "idempotency_conflict"); }
        catch (LedgerCapacity) { await ApiProblems.Write(context, 503, "Order capacity reached; contact the operator", "capacity_exceeded"); }
        catch (LedgerBusy) { context.Response.Headers.RetryAfter = "1"; await ApiProblems.Write(context, 503, "State is busy; retry with the same Idempotency-Key", "state_busy"); }
        catch (HttpRequestException) { await ApiProblems.Write(context, 503, "Dependency unavailable; retry with the same Idempotency-Key", "dependency_unavailable"); }
        catch (OperationCanceledException) { await ApiProblems.Write(context, 503, "Dependency timed out; retry with the same Idempotency-Key", "dependency_timeout"); }
        catch (Exception exception)
        {
            logger.LogError("Unexpected request failure type {FailureType}", exception.GetType().Name);
            await ApiProblems.Write(context, 500, "The request could not be completed", "unexpected_failure");
        }
    }
}
