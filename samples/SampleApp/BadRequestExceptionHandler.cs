using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SampleApp;

/// <summary>
/// Answers a request whose body the binder could not read with the status ASP.NET Core already
/// chose for it — <c>400</c> — rather than letting it become a <c>500</c>.
/// <para>
/// Worth having, because the default is misleading. A malformed JSON body makes the minimal-API
/// binder throw <see cref="BadHttpRequestException"/>, which carries <c>StatusCode = 400</c>.
/// <c>UseExceptionHandler()</c> offers it to each registered handler, and rethrows when none claims
/// it. In Production that rethrow ends as a bare 400, which is right; in Development the developer
/// exception page catches it first and renders a 500 with a stack trace — so the same request
/// answers 400 on the server and 500 on the machine it was written on, and the developer goes
/// looking for a fault that is in their own request body.
/// </para>
/// <para>
/// Registered before <c>MediaExceptionHandler</c>. Handlers run in registration order, and this one
/// claims only what that one ignores.
/// </para>
/// </summary>
/// <param name="problemDetails">Writes the response when <c>AddProblemDetails()</c> is registered.</param>
public sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not BadHttpRequestException badRequest)
            return false;

        httpContext.Response.StatusCode = badRequest.StatusCode;

        // The innermost message is the useful one — "The JSON value could not be converted to
        // System.Guid. Path: $.mediaFileIds[0]" says exactly which field is wrong, where the
        // outer one only says the body could not be read.
        var detail = (badRequest.InnerException ?? badRequest).Message;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext    = httpContext,
            Exception      = exception,
            ProblemDetails = new ProblemDetails
            {
                Title  = "Malformed request body",
                Status = badRequest.StatusCode,
                Detail = detail
            }
        });
    }
}
