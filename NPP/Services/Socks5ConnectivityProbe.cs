using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Netch.App.Services;

public sealed record ProxyEndpoint(string Host, ushort Port)
{
    public string Display => new UriBuilder("socks5", Host, Port).Uri.Authority;

    public static bool TryCreate(string host, string port, out ProxyEndpoint? endpoint)
    {
        endpoint = null;
        host = host.Trim().Trim('[', ']');
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            !ushort.TryParse(port.Trim(), out var number) || number == 0)
            return false;
        endpoint = new ProxyEndpoint(host.ToLowerInvariant(), number);
        return true;
    }
}

public sealed record ConnectivityResult(bool Connected, string Message, long? ElapsedMilliseconds = null);

/// <summary>Checks SOCKS5 negotiation and a real HTTP response through that proxy.</summary>
public static class Socks5ConnectivityProbe
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private static readonly Uri[] DefaultTargets =
    [
        new("https://www.gstatic.com/generate_204"),
        new("https://cp.cloudflare.com/generate_204")
    ];

    internal static async Task<ConnectivityResult> CheckWithRetriesAsync(
        ProxyEndpoint endpoint,
        TimeSpan duration,
        CancellationToken cancellationToken,
        IReadOnlyList<Uri>? targets = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        var result = new ConnectivityResult(false, "启动自检超时，可点击“检测连通”重试");
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                result = await CheckAsync(endpoint, deadline.Token, targets);
                if (result.Connected)
                    return result;

                await Task.Delay(RetryInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return result;
        }
    }

    public static async Task<ConnectivityResult> CheckAsync(
        ProxyEndpoint endpoint,
        CancellationToken cancellationToken = default,
        IReadOnlyList<Uri>? targets = null)
    {
        targets ??= DefaultTargets;
        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var socket = new TcpClient();
            await socket.ConnectAsync(endpoint.Host, endpoint.Port, handshakeTimeout.Token);
            var stream = socket.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, handshakeTimeout.Token);
            var response = new byte[2];
            await stream.ReadExactlyAsync(response, handshakeTimeout.Token);
            if (response[0] != 5 || response[1] != 0)
                return new(false, "SOCKS5 握手失败：端口不支持无认证 SOCKS5");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "连接 SOCKS5 超时");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return new(false, "无法连接 SOCKS5，请检查地址、端口及节点是否已启动");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(new UriBuilder("socks5", endpoint.Host, endpoint.Port).Uri),
            UseProxy = true,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4)
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        string failure = "SOCKS5 可用，但无法访问测试地址";
        foreach (var target in targets)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    return new(true, $"已连通 · {elapsed} ms", elapsed);
                }
                failure = $"测试地址返回 HTTP {(int)response.StatusCode}，未确认连通";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = "SOCKS5 可用，但访问测试地址超时";
            }
            catch (HttpRequestException)
            {
                failure = "SOCKS5 可用，但 HTTPS 测试失败，请检查节点或网络";
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(false, failure);
    }
}
