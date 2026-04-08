using Azure.Core;

namespace Manufaktura.Orders.Functions.Tests;

internal sealed record CapturedRequest(HttpMethod Method, string Url, string? Body, string? ContentRange = null);

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    public List<CapturedRequest> SentRequests { get; } = [];

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var contentRange = request.Content?.Headers.ContentRange?.ToString();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        SentRequests.Add(new(request.Method, url, body, contentRange));
        return _responses.Count == 0
            ? throw new InvalidOperationException($"No more queued responses for {request.Method} {url}.")
            : _responses.Dequeue();
    }
}

internal sealed class FakeTokenCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new("fake-token", DateTimeOffset.MaxValue);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.MaxValue));
}
