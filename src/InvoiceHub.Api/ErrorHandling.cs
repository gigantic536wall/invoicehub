using InvoiceHub.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InvoiceHub.Api;

/// <summary>Turns business-rule violations into 422 and database conflicts into 409, as RFC 9457 problem details.</summary>
public sealed class ErrorHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        (int status, string title, string? detail) = exception switch
        {
            DomainException e => (StatusCodes.Status422UnprocessableEntity, "Business rule violated", e.Message),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (StatusCodes.Status409Conflict, "Conflict", "This would create a duplicate."),
            BadHttpRequestException e => (e.StatusCode, "Bad request", e.Message),
            _ => (0, "", null),
        };
        if (status == 0)
        {
            return false; // unexpected: default handler returns a generic 500 without internals
        }

        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }
}
