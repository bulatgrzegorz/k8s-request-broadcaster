using System.Buffers;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

app.Map("/{serviceName}/{targetPort:int}/{*rest}", async (string serviceName, int targetPort, string? rest, HttpContext httpContext, HttpRequest httpRequest, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
{
    var addresses = await GetHostAddressesAsync(serviceName, cancellationToken);
    if (addresses.Length == 0)
    {
        return Results.NotFound();
    }

    var requestBody = await httpContext.GetRequestBodyBytesAsync();
    
    IEnumerable<Task<HttpResponseMessage>> CallAddresses(HttpClient client)
    {
        foreach (var address in addresses)
        {
            yield return httpRequest.Forward(client, new HostString(address.ToString(), targetPort), $"/{rest}", requestBody, cancellationToken);
        }
    }

    var httpClient = httpClientFactory.CreateClient();
    var calls = CallAddresses(httpClient).ToList();
    
    await Task.WhenAll(calls);
    
    var result = new
    {
        Responses = calls.Select(x => new
        {
            x.Result.StatusCode,
            Address = x.Result.RequestMessage?.RequestUri
        })
    };

    var isSuccessFromAllCalls = calls.TrueForAll(x => x.Result.IsSuccessStatusCode);
    return isSuccessFromAllCalls ? Results.Ok(result) : Results.BadRequest(result);
});

app.Run();

static async Task<IPAddress[]> GetHostAddressesAsync(string serviceName, CancellationToken cancellationToken)
{
    try
    {
        var addresses = await Dns.GetHostAddressesAsync(serviceName, cancellationToken);
        if (addresses.Length != 0)
        {
            return addresses;
        }
        
        Console.WriteLine($"Could not find host: {serviceName}");
        return Array.Empty<IPAddress>();

    }
    catch (SocketException e)
    {
        Console.WriteLine($"Socket exception occured while trying to find host. Error message: {e.Message}, socket error code: {e.SocketErrorCode}");
        return Array.Empty<IPAddress>();
    }
}

public static class HttpContextExtensions
{
    private static readonly ImmutableHashSet<string> HeadersToExclude = ImmutableHashSet.Create<string>("Host");
    
    public static async Task<HttpResponseMessage> Forward(this HttpRequest httpRequest, HttpClient httpClient, HostString host, string path, byte[]? requestBody, CancellationToken cancellationToken)
    {
        var uri = new Uri(UriHelper.BuildAbsolute(
            httpRequest.Scheme,
            host,
            path: path,
            query: httpRequest.QueryString));

        var request = httpRequest.CreateProxyHttpRequest(uri, requestBody);

        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
    
    public static async Task<byte[]?> GetRequestBodyBytesAsync(this HttpContext httpContext)
    {
        if (httpContext.Request.ContentLength is null or 0 && !httpContext.Request.Headers.ContainsKey(HeaderNames.TransferEncoding))
        {
            return null;
        }

        var method = httpContext.Request.Method;

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsDelete(method) || HttpMethods.IsConnect(method) || HttpMethods.IsTrace(method))
        {
            return null;
        }
    
        while (!httpContext.RequestAborted.IsCancellationRequested)
        {
            var readResult = await httpContext.Request.BodyReader.ReadAsync(httpContext.RequestAborted);
            if (readResult.IsCompleted || readResult.IsCanceled)
            {
                return readResult.Buffer.ToArray();
            }

            httpContext.Request.BodyReader.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.End);
        }

        httpContext.RequestAborted.ThrowIfCancellationRequested();

        return null;
    }

    private static HttpRequestMessage CreateProxyHttpRequest(this HttpRequest request, Uri uri, byte[]? requestBody)
    {
        var requestMessage = new HttpRequestMessage()
        {
            RequestUri = uri,
            Headers = { Host = uri.Authority }
        };

        if (requestBody is not null)
        {
            requestMessage.Content = new ByteArrayContent(requestBody);
        }

        foreach (var header in request.Headers)
        {
            if (HeadersToExclude.Contains(header.Key))
            {
                continue;
            }
            
            if (header.Value.Count == 1)
            {
                string value = header.Value!;
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, value))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, value);
                }
            }
            else
            {
                string[] values = header.Value!;
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, values))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, values);
                }
            }
        }

        requestMessage.Method = new HttpMethod(request.Method);

        return requestMessage;
    }
}