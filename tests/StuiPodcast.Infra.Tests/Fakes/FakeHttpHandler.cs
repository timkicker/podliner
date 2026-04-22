using System.Net;
using System.Text;

namespace StuiPodcast.Infra.Tests.Fakes;

sealed class FakeHttpHandler : HttpMessageHandler
{
    readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public void Enqueue(HttpResponseMessage r)
        => _queue.Enqueue(_ => r);

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> fn)
        => _queue.Enqueue(fn);

    public void EnqueueXml(string xml, HttpStatusCode status = HttpStatusCode.OK)
        => Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/rss+xml")
        });

    public void EnqueueStatus(HttpStatusCode status)
        => Enqueue(new HttpResponseMessage(status));

    public void EnqueueThrowing(Exception ex)
        => _queue.Enqueue(_ => throw ex);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Requests.Add(req);
        var fn = _queue.Count > 0 ? _queue.Dequeue() : _ => new HttpResponseMessage(HttpStatusCode.OK);
        return Task.FromResult(fn(req));
    }
}
