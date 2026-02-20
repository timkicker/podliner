using System.Net;
using System.Text;
using System.Text.Json;

namespace StuiPodcast.App.Tests.Fakes;

sealed class FakeHttpHandler : HttpMessageHandler
{
    readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public void Enqueue(HttpResponseMessage r)
        => _queue.Enqueue(_ => r);

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> fn)
        => _queue.Enqueue(fn);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Requests.Add(req);
        var fn = _queue.Count > 0 ? _queue.Dequeue() : _ => new HttpResponseMessage(HttpStatusCode.OK);
        return Task.FromResult(fn(req));
    }

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
