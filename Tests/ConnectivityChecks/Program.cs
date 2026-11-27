using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Netch.App.Services;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine($"PASS: {message}");
    passed++;
}

Check(!ProxyEndpoint.TryCreate("", "10808", out _) &&
      !ProxyEndpoint.TryCreate("http://localhost", "10808", out _) &&
      !ProxyEndpoint.TryCreate("127.0.0.1", "0", out _) &&
      !ProxyEndpoint.TryCreate("127.0.0.1", "65536", out _) &&
      ProxyEndpoint.TryCreate("[::1]", "10808", out _), "endpoint validation rejects invalid inputs and accepts IPv6");

var targets = new[] { new Uri("http://check.example/generate_204") };
await using (var proxy = new TestProxy())
{
    var result = await Socks5ConnectivityProbe.CheckAsync(proxy.Endpoint, targets: targets);
    Check(result.Connected && result.ElapsedMilliseconds is >= 0, "SOCKS5 plus HTTP 204 reports a measured success");
    Check(proxy.TargetHosts.Single() == "check.example", "destination DNS is sent through SOCKS5");
    var requests = proxy.HttpRequests;
    await Task.Delay(300);
    Check(proxy.HttpRequests == requests && requests == 1, "completed checks produce no periodic requests");
}

await using (var proxy = new TestProxy(failuresBeforeSuccess: 1))
{
    var result = await Socks5ConnectivityProbe.CheckWithRetriesAsync(
        proxy.Endpoint, TimeSpan.FromSeconds(8), CancellationToken.None, targets);
    Check(result.Connected && proxy.HttpRequests == 2,
        "startup check retries a transient failure and stops on the first success");
}

await using (var proxy = new TestProxy(mode: "stall"))
{
    var timer = Stopwatch.StartNew();
    var result = await Socks5ConnectivityProbe.CheckWithRetriesAsync(
        proxy.Endpoint, TimeSpan.FromMilliseconds(250), CancellationToken.None, targets);
    Check(!result.Connected && timer.Elapsed < TimeSpan.FromSeconds(2),
        "the startup deadline interrupts an in-flight probe");
}

foreach (var code in new[] { 200, 302, 503 })
{
    await using var proxy = new TestProxy(status: code);
    var result = await Socks5ConnectivityProbe.CheckAsync(proxy.Endpoint, targets: targets);
    Check(!result.Connected && proxy.HttpRequests == 1, $"HTTP {code} is not mistaken for connectivity or followed as a redirect");
}

await using (var proxy = new TestProxy(status: 503, successfulHost: "fallback.example"))
{
    var result = await Socks5ConnectivityProbe.CheckAsync(proxy.Endpoint, targets:
        [targets[0], new Uri("http://fallback.example/generate_204")]);
    Check(result.Connected && proxy.HttpRequests == 2, "a second test endpoint can recover from a target-specific failure");
}

foreach (var mode in new[] { "auth", "not-socks", "reject-connect" })
{
    await using var proxy = new TestProxy(mode: mode);
    var result = await Socks5ConnectivityProbe.CheckAsync(proxy.Endpoint, targets: targets);
    Check(!result.Connected && proxy.HttpRequests == 0, $"{mode} never produces a green result");
}

await using (var proxy = new TestProxy(mode: "stall"))
{
    using var cancellation = new CancellationTokenSource(150);
    var timer = Stopwatch.StartNew();
    var cancelled = false;
    try { await Socks5ConnectivityProbe.CheckAsync(proxy.Endpoint, cancellation.Token, targets); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && timer.Elapsed < TimeSpan.FromSeconds(2), "cancelling an obsolete check promptly releases the socket");
}

var unused = new TcpListener(IPAddress.Loopback, 0);
unused.Start();
var unusedPort = ((IPEndPoint)unused.LocalEndpoint).Port;
unused.Stop();
var closed = await Socks5ConnectivityProbe.CheckAsync(new ProxyEndpoint("127.0.0.1", (ushort)unusedPort), targets: targets);
Check(!closed.Connected, "a closed local proxy port reports a failure");
Console.WriteLine($"{passed} connectivity checks passed.");

sealed class TestProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _clients = [];
    private readonly string _mode;
    private readonly int _status;
    private readonly string? _successfulHost;
    private readonly int _failuresBeforeSuccess;
    private readonly Task _accept;
    private int _httpRequests;
    public List<string> TargetHosts { get; } = [];
    public int HttpRequests => Volatile.Read(ref _httpRequests);
    public ProxyEndpoint Endpoint { get; }

    public TestProxy(string mode = "normal", int status = 204, string? successfulHost = null, int failuresBeforeSuccess = 0)
    {
        _mode = mode;
        _status = status;
        _successfulHost = successfulHost;
        _failuresBeforeSuccess = failuresBeforeSuccess;
        _listener.Start();
        Endpoint = new("127.0.0.1", (ushort)((IPEndPoint)_listener.LocalEndpoint).Port);
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                _clients.Add(HandleAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(TcpClient socket)
    {
        using (socket)
        {
            try
            {
                var stream = socket.GetStream();
                var greeting = new byte[2];
                await stream.ReadExactlyAsync(greeting, _stop.Token);
                var methods = new byte[greeting[1]];
                await stream.ReadExactlyAsync(methods, _stop.Token);
                if (_mode == "stall") { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                await stream.WriteAsync(_mode switch
                {
                    "auth" => new byte[] { 5, 2 },
                    "not-socks" => new byte[] { 72, 84 },
                    _ => new byte[] { 5, 0 }
                }, _stop.Token);
                if (_mode is "auth" or "not-socks") return;
                var command = new byte[4];
                await stream.ReadExactlyAsync(command, _stop.Token);
                if (command[0] != 5 || command[1] != 1 || command[3] != 3)
                    throw new InvalidOperationException("Expected SOCKS5 CONNECT with remote DNS");
                var size = new byte[1];
                await stream.ReadExactlyAsync(size, _stop.Token);
                var address = new byte[size[0]];
                await stream.ReadExactlyAsync(address, _stop.Token);
                var port = new byte[2];
                await stream.ReadExactlyAsync(port, _stop.Token);
                var hostname = Encoding.ASCII.GetString(address);
                lock (TargetHosts) TargetHosts.Add(hostname);
                await stream.WriteAsync(new byte[] { 5, _mode == "reject-connect" ? (byte)5 : (byte)0, 0, 1, 0, 0, 0, 0, 0, 0 }, _stop.Token);
                if (_mode == "reject-connect") return;
                var request = new StringBuilder();
                var single = new byte[1];
                while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    await stream.ReadExactlyAsync(single, _stop.Token);
                    request.Append((char)single[0]);
                    if (request.Length > 16384) throw new InvalidOperationException("Unexpected request size");
                }
                var requestNumber = Interlocked.Increment(ref _httpRequests);
                var code = requestNumber <= _failuresBeforeSuccess ? 503 : hostname == _successfulHost ? 204 : _status;
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {code} Test\r\nContent-Length: 0\r\nLocation: http://redirect.example/\r\nConnection: close\r\n\r\n"), _stop.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_clients);
        _stop.Dispose();
    }
}
