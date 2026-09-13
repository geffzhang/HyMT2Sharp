using System.Text.Json;

namespace Sdcb.HyMT2Sharp.Server;

public sealed class SseResult(IAsyncEnumerable<ChatCompletionChunk> chunks, JsonSerializerOptions json) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        HttpResponse response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(httpContext.RequestAborted);

        try
        {
            await foreach (ChatCompletionChunk chunk in chunks.WithCancellation(httpContext.RequestAborted))
            {
                string payload = JsonSerializer.Serialize(chunk, json);
                await response.WriteAsync("data: " + payload + "\n\n", httpContext.RequestAborted);
                await response.Body.FlushAsync(httpContext.RequestAborted);
            }

            await response.WriteAsync("data: [DONE]\n\n", httpContext.RequestAborted);
            await response.Body.FlushAsync(httpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
        }
        catch (ArgumentException ex)
        {
            if (!response.HasStarted)
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ContentType = "application/json; charset=utf-8";
            }

            ErrorResponse error = new() { Error = new ErrorBody { Message = ex.Message } };
            await response.WriteAsync("data: " + JsonSerializer.Serialize(error, json) + "\n\n", CancellationToken.None);
        }
    }
}
