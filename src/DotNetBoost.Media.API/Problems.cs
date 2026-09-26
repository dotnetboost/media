using DotNetBoost.Media.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DotNetBoost.Media.API;

/// <summary>Maps <see cref="MediaException"/> subtypes onto RFC 7807 problem details.</summary>
public static class MediaProblems
{
    /// <summary>The HTTP status for <paramref name="exception"/>.</summary>
    public static int StatusCodeFor(MediaException exception) => exception switch
    {
        MediaNotFoundException        => StatusCodes.Status404NotFound,
        // The row is there, the bytes are not. A 404 to the caller — the resource genuinely cannot
        // be served — even though it is the server that has some tidying to do.
        MediaContentMissingException  => StatusCodes.Status404NotFound,
        MediaTooLargeException        => StatusCodes.Status413PayloadTooLarge,
        // Before its sibling below: a file whose bytes contradict its declared type is refused for
        // the same reason an unsupported type is.
        MediaContentMismatchException => StatusCodes.Status415UnsupportedMediaType,
        UnsupportedMediaTypeException => StatusCodes.Status415UnsupportedMediaType,
        MediaConflictException        => StatusCodes.Status409Conflict,
        // The application reached its storage backend and the backend is what failed. Not the
        // caller's doing, and not a plain 500 either: 502 says which side of the boundary broke.
        MediaStorageException         => StatusCodes.Status502BadGateway,
        InvalidMediaException         => StatusCodes.Status400BadRequest,
        _                             => StatusCodes.Status400BadRequest
    };

    /// <summary>Problem details describing <paramref name="exception"/>.</summary>
    public static ProblemDetails ToProblemDetails(MediaException exception, HttpContext? httpContext = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var status = StatusCodeFor(exception);

        var title = exception switch
        {
            MediaNotFoundException        => "Media file not found",
            MediaContentMissingException  => "Media content missing",
            MediaTooLargeException        => "Media file too large",
            MediaContentMismatchException => "Media content does not match its type",
            UnsupportedMediaTypeException => "Unsupported media type",
            MediaConflictException        => "Conflict",
            MediaStorageException         => "Media storage unavailable",
            _                             => "Invalid media request"
        };

        var problem = new ProblemDetails
        {
            Status   = status,
            Title    = title,
            // The library's messages are written for the caller — they say what was wrong and what
            // to do about it — with one exception below.
            Detail   = Detail(exception),
            Instance = httpContext?.Request.Path
        };

        // The few numbers a client can act on programmatically, so it does not have to parse the
        // message to show "max 25 MB" or to offer the right file types.
        switch (exception)
        {
            case MediaTooLargeException tooLarge:
                problem.Extensions["size"]  = tooLarge.Size;
                problem.Extensions["limit"] = tooLarge.Limit;
                break;

            case MediaContentMismatchException mismatch:
                problem.Extensions["declaredContentType"] = mismatch.DeclaredContentType;
                problem.Extensions["detectedContentType"] = mismatch.DetectedContentType;
                break;

            case UnsupportedMediaTypeException unsupported:
                if (unsupported.ContentType is { } contentType)
                    problem.Extensions["contentType"] = contentType;
                if (unsupported.Extension is { } extension)
                    problem.Extensions["extension"] = extension;
                break;

            case MediaNotFoundException notFound:
                problem.Extensions["mediaId"] = notFound.Id;
                break;
        }

        return problem;
    }

    /// <summary>
    /// The detail line. A storage failure is the one case where the exception's own message is
    /// held back: it names the container, the path and whatever the cloud SDK said, none of which
    /// is the caller's business and some of which is a map of the storage account.
    /// </summary>
    private static string Detail(MediaException exception) => exception switch
    {
        MediaStorageException => "The media storage backend could not be reached. Try again shortly.",
        _                     => exception.Message
    };
}

/// <summary>
/// Answers <see cref="MediaException"/>s thrown anywhere in the application — for example from
/// your own upload endpoint — with problem details. Everything else is left to the next handler.
/// Register it with <c>services.AddExceptionHandler&lt;MediaExceptionHandler&gt;()</c> and
/// <c>app.UseExceptionHandler()</c>.
/// </summary>
/// <param name="problemDetails">Writes the response when <c>AddProblemDetails()</c> is registered.</param>
public sealed class MediaExceptionHandler(IProblemDetailsService? problemDetails = null) : IExceptionHandler
{
    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not MediaException known)
            return false;

        var problem = MediaProblems.ToProblemDetails(known, httpContext);
        httpContext.Response.StatusCode = problem.Status!.Value;

        if (problemDetails is not null
            && await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext    = httpContext,
                ProblemDetails = problem,
                Exception      = exception
            }))
        {
            return true;
        }

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}

/// <summary>Turns <see cref="MediaException"/>s from the media endpoints into problem details.</summary>
internal sealed class MediaProblemFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (MediaException ex)
        {
            var problem = MediaProblems.ToProblemDetails(ex, context.HttpContext);
            return TypedResults.Problem(problem);
        }
    }
}
