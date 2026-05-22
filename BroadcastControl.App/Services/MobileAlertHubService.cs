// 위험 상황을 같은 네트워크의 모바일 브라우저로 전달하는 작은 HTTP/SSE 서버 파일이다.
// 별도 앱 설치 없이 휴대폰에서 GUI PC 주소로 접속하면 최신 위험 이벤트, VLM 분석, 증거 이미지를 볼 수 있다.
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BroadcastControl.App.Services;

public sealed class MobileAlertHubService : IDisposable
{
    private const int DefaultPort = 8088;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConcurrentDictionary<string, byte[]> _evidenceImages = new();
    private readonly List<SseClient> _clients = new();
    private readonly object _clientsLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _acceptLoopTask;
    private MobileAlertEvent? _latestAlert;

    public int Port { get; private set; } = DefaultPort;

    public string LocalUrl => $"http://localhost:{Port}/";

    public string AccessHintUrls
    {
        get
        {
            var urls = Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Select(address => $"http://{address}:{Port}/")
                .Take(3)
                .ToArray();
            return urls.Length == 0 ? LocalUrl : string.Join(", ", urls);
        }
    }

    public bool Start(int port = DefaultPort)
    {
        if (_listener is not null)
        {
            return true;
        }

        try
        {
            // 별도 앱 설치 없이 같은 네트워크의 휴대폰 브라우저에서 접속할 수 있도록 TCP 서버를 연다.
            Port = port;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _cancellationTokenSource = new CancellationTokenSource();
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cancellationTokenSource.Token));
            return true;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    public async Task PublishAlertAsync(string title, string vlmAnalysis, string detectionSummary, string threatLevel, byte[]? evidencePng)
    {
        // 증거 이미지는 메모리에 잠시 보관하고, 모바일 페이지에는 /evidence/{id}.png URL로 제공한다.
        // SSE로 연결된 모든 모바일 클라이언트에 같은 알림 이벤트를 동시에 보낸다.
        var id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var evidenceUrl = string.Empty;
        if (evidencePng is { Length: > 0 })
        {
            _evidenceImages[id] = evidencePng;
            evidenceUrl = $"/evidence/{id}.png";
            TrimEvidenceImages();
        }

        var alert = new MobileAlertEvent(
            id,
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            title,
            vlmAnalysis,
            detectionSummary,
            threatLevel,
            evidenceUrl);
        _latestAlert = alert;

        var json = JsonSerializer.Serialize(alert, JsonOptions);
        List<SseClient> clients;
        lock (_clientsLock)
        {
            clients = _clients.ToList();
        }

        foreach (var client in clients)
        {
            try
            {
                await client.Writer.WriteAsync($"event: alert\ndata: {json}\n\n").ConfigureAwait(false);
                await client.Writer.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                RemoveClient(client);
            }
        }
    }

    public void Stop()
    {
        _cancellationTokenSource?.Cancel();

        try
        {
            _listener?.Stop();
        }
        catch
        {
        }

        _listener = null;

        try
        {
            _acceptLoopTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        _acceptLoopTask = null;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        lock (_clientsLock)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        // 접속한 모바일 브라우저마다 별도 작업을 만들어 처리한다.
        // 한 사용자의 느린 네트워크가 다른 사용자 알림 전송을 막지 않게 하기 위한 구조다.
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch
            {
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        // 매우 작은 내장 HTTP 라우터다.
        // /events는 SSE 연결, /latest는 최신 알림 JSON, /evidence/*.png는 증거 이미지, 나머지는 모바일 HTML을 반환한다.
        using var client = tcpClient;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }

            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
            {
            }

            var path = ParsePath(requestLine);
            if (path == "/events")
            {
                await HandleSseClientAsync(client, stream, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/latest")
            {
                var json = JsonSerializer.Serialize(_latestAlert, JsonOptions);
                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/evidence/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (_evidenceImages.TryGetValue(id, out var imageBytes))
                {
                    await WriteResponseAsync(stream, "image/png", imageBytes, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteNotFoundAsync(stream, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(stream, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(BuildMobileAppHtml()), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task HandleSseClientAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
    {
        // SSE 연결은 끊기지 않는 HTTP 응답으로 유지된다.
        // 새 알림이 PublishAlertAsync에서 발생하면 이 writer 목록으로 event: alert를 보낸다.
        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        await writer.WriteAsync(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream; charset=utf-8\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: keep-alive\r\n" +
            "Access-Control-Allow-Origin: *\r\n\r\n").ConfigureAwait(false);

        var sseClient = new SseClient(client, writer);
        lock (_clientsLock)
        {
            _clients.Add(sseClient);
        }

        try
        {
            if (_latestAlert is not null)
            {
                var json = JsonSerializer.Serialize(_latestAlert, JsonOptions);
                await writer.WriteAsync($"event: alert\ndata: {json}\n\n").ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(": keep-alive\n\n").ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            RemoveClient(sseClient);
        }
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteNotFoundAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes("Not found");
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 404 Not Found\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static string ParsePath(string requestLine)
    {
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return "/";
        }

        var path = parts[1];
        var queryStart = path.IndexOf('?', StringComparison.Ordinal);
        return queryStart >= 0 ? path[..queryStart] : path;
    }

    private void RemoveClient(SseClient client)
    {
        lock (_clientsLock)
        {
            _clients.Remove(client);
        }

        client.Dispose();
    }

    private void TrimEvidenceImages()
    {
        const int maxImages = 20;
        if (_evidenceImages.Count <= maxImages)
        {
            return;
        }

        var oldestKeys = _evidenceImages.Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .Take(Math.Max(0, _evidenceImages.Count - maxImages))
            .ToArray();

        foreach (var key in oldestKeys)
        {
            _evidenceImages.TryRemove(key, out _);
        }
    }

    private static string BuildMobileAppHtml() =>
        """
        <!doctype html>
        <html lang="ko">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
        <title>&#xC704;&#xD5D8; &#xC54C;&#xB9BC;</title>
        <style>
        :root { color-scheme: dark; font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #07090c; color: #f5f7fb; }
        body { margin: 0; min-height: 100vh; background: #07090c; }
        main { min-height: 100vh; display: grid; grid-template-rows: auto 1fr auto; }
        header { padding: 18px 18px 12px; border-bottom: 1px solid #222936; background: #111722; }
        h1 { margin: 0; font-size: 22px; letter-spacing: 0; }
        .status { margin-top: 6px; color: #9aa6b8; font-size: 13px; }
        .alert { padding: 18px; display: grid; gap: 14px; align-content: start; }
        .level { display: inline-flex; align-items: center; gap: 8px; color: #ff7777; font-weight: 800; font-size: 18px; }
        .dot { width: 12px; height: 12px; border-radius: 99px; background: #ff4d4f; box-shadow: 0 0 16px #ff4d4f; }
        .time { color: #b8c1cf; font-size: 13px; }
        .detail-grid { display: grid; gap: 12px; }
        .detail-card { padding: 14px; border: 1px solid #2f3d52; border-radius: 8px; background: #111722; }
        .detail-title { margin: 0 0 8px; color: #8fd3ff; font-size: 14px; font-weight: 800; letter-spacing: 0; }
        .detail-text { margin: 0; line-height: 1.55; color: #e8edf5; font-size: 16px; white-space: pre-wrap; }
        img { width: 100%; max-height: 58vh; object-fit: contain; background: #010204; border: 1px solid #263142; }
        button { width: calc(100% - 36px); margin: 0 18px 18px; height: 48px; border: 1px solid #42526a; background: #172131; color: #fff; font-size: 16px; font-weight: 700; border-radius: 6px; }
        .empty { color: #98a4b5; }
        </style>
        </head>
        <body>
        <main>
        <header>
        <h1>&#xC6B4;&#xC6A9;&#xD1B5;&#xC81C; &#xC704;&#xD5D8; &#xC54C;&#xB9BC;</h1>
        <div id="status" class="status">GUI &#xC54C;&#xB9BC; &#xB300;&#xAE30; &#xC911;</div>
        </header>
        <section id="alert" class="alert">
        <div class="empty">&#xC704;&#xD5D8; &#xC774;&#xBCA4;&#xD2B8;&#xAC00; &#xBC1C;&#xC0DD;&#xD558;&#xBA74; YOLO &#xBC14;&#xC6B4;&#xB529; &#xBC15;&#xC2A4; &#xD654;&#xBA74;&#xACFC; VLM &#xBD84;&#xC11D;, &#xD0D0;&#xC9C0; &#xB0B4;&#xC6A9;&#xC774; &#xBD84;&#xB9AC;&#xB418;&#xC5B4; &#xD45C;&#xC2DC;&#xB429;&#xB2C8;&#xB2E4;.</div>
        </section>
        <button id="enable">&#xC54C;&#xB9BC;&#xC74C; / &#xC9C4;&#xB3D9; &#xD65C;&#xC131;&#xD654;</button>
        </main>
        <script>
        let audioReady = false;
        const statusEl = document.getElementById("status");
        const alertEl = document.getElementById("alert");
        document.getElementById("enable").addEventListener("click", async () => {
          audioReady = true;
          if ("Notification" in window && Notification.permission === "default") await Notification.requestPermission();
          playAlarm();
          if (navigator.vibrate) navigator.vibrate([120, 60, 120]);
        });
        function playAlarm() {
          if (!audioReady) return;
          const ctx = new (window.AudioContext || window.webkitAudioContext)();
          const osc = ctx.createOscillator();
          const gain = ctx.createGain();
          osc.type = "square";
          osc.frequency.value = 880;
          gain.gain.setValueAtTime(0.0001, ctx.currentTime);
          gain.gain.exponentialRampToValueAtTime(0.25, ctx.currentTime + 0.03);
          gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.65);
          osc.connect(gain).connect(ctx.destination);
          osc.start();
          osc.stop(ctx.currentTime + 0.7);
        }
        function renderAlert(alert) {
          if (!alert) return;
          const vlmAnalysis = escapeHtml(alert.vlmAnalysis || alert.analysis || "");
          const detectionSummary = escapeHtml(alert.detectionSummary || "\uD0D0\uC9C0 \uB0B4\uC6A9\uC774 \uC5C6\uC2B5\uB2C8\uB2E4.");
          statusEl.textContent = "\uB9C8\uC9C0\uB9C9 \uC218\uC2E0: " + alert.createdAt;
          alertEl.innerHTML = `
            <div class="level"><span class="dot"></span>${escapeHtml(alert.threatLevel)} \uC704\uD5D8</div>
            <div class="time">${escapeHtml(alert.createdAt)}</div>
            ${alert.evidenceUrl ? `<img src="${alert.evidenceUrl}?t=${encodeURIComponent(alert.id)}" alt="\uC704\uD5D8 \uD654\uBA74">` : ""}
            <div class="detail-grid">
              <article class="detail-card">
                <h2 class="detail-title">VLM \uBD84\uC11D</h2>
                <p class="detail-text">${vlmAnalysis}</p>
              </article>
              <article class="detail-card">
                <h2 class="detail-title">\uD0D0\uC9C0 \uB0B4\uC6A9</h2>
                <p class="detail-text">${detectionSummary}</p>
              </article>
            </div>`;
          playAlarm();
          if (navigator.vibrate) navigator.vibrate([220, 90, 220, 90, 420]);
          if ("Notification" in window && Notification.permission === "granted") {
            new Notification(alert.title, { body: alert.vlmAnalysis || alert.analysis });
          }
        }
        function escapeHtml(value) {
          return String(value)
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#39;");
        }
        fetch("/latest").then(r => r.json()).then(renderAlert).catch(() => {});
        const events = new EventSource("/events");
        events.onopen = () => statusEl.textContent = "GUI\uC640 \uC5F0\uACB0\uB428";
        events.onerror = () => statusEl.textContent = "\uC5F0\uACB0 \uC7AC\uC2DC\uB3C4 \uC911";
        events.addEventListener("alert", event => renderAlert(JSON.parse(event.data)));
        </script>
        </body>
        </html>
        """;

    private static string BuildLegacyMobileAppHtml() =>
        """
        <!doctype html>
        <html lang="ko">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
        <title>위험 알림</title>
        <style>
        :root { color-scheme: dark; font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #07090c; color: #f5f7fb; }
        body { margin: 0; min-height: 100vh; background: #07090c; }
        main { min-height: 100vh; display: grid; grid-template-rows: auto 1fr auto; }
        header { padding: 18px 18px 12px; border-bottom: 1px solid #222936; background: #111722; }
        h1 { margin: 0; font-size: 22px; letter-spacing: 0; }
        .status { margin-top: 6px; color: #9aa6b8; font-size: 13px; }
        .alert { padding: 18px; display: grid; gap: 14px; align-content: start; }
        .level { display: inline-flex; align-items: center; gap: 8px; color: #ff7777; font-weight: 800; font-size: 18px; }
        .dot { width: 12px; height: 12px; border-radius: 99px; background: #ff4d4f; box-shadow: 0 0 16px #ff4d4f; }
        .time { color: #b8c1cf; font-size: 13px; }
        .detail-grid { display: grid; gap: 12px; }
        .detail-card { padding: 14px; border: 1px solid #2f3d52; border-radius: 8px; background: #111722; }
        .detail-title { margin: 0 0 8px; color: #8fd3ff; font-size: 14px; font-weight: 800; letter-spacing: 0; }
        .detail-text { margin: 0; line-height: 1.55; color: #e8edf5; font-size: 16px; white-space: pre-wrap; }
        img { width: 100%; max-height: 58vh; object-fit: contain; background: #010204; border: 1px solid #263142; }
        button { width: calc(100% - 36px); margin: 0 18px 18px; height: 48px; border: 1px solid #42526a; background: #172131; color: #fff; font-size: 16px; font-weight: 700; border-radius: 6px; }
        .empty { color: #98a4b5; }
        </style>
        </head>
        <body>
        <main>
        <header>
        <h1>운용통제 위험 알림</h1>
        <div id="status" class="status">GUI 알림 대기 중</div>
        </header>
        <section id="alert" class="alert">
        <div class="empty">위험 이벤트가 발생하면 YOLO 바운딩 박스 화면과 분석 결과가 표시됩니다.</div>
        </section>
        <button id="enable">알림음/진동 활성화</button>
        </main>
        <script>
        let audioReady = false;
        const statusEl = document.getElementById("status");
        const alertEl = document.getElementById("alert");
        document.getElementById("enable").addEventListener("click", async () => {
          audioReady = true;
          if ("Notification" in window && Notification.permission === "default") await Notification.requestPermission();
          playAlarm();
          if (navigator.vibrate) navigator.vibrate([120, 60, 120]);
        });
        function playAlarm() {
          if (!audioReady) return;
          const ctx = new (window.AudioContext || window.webkitAudioContext)();
          const osc = ctx.createOscillator();
          const gain = ctx.createGain();
          osc.type = "square";
          osc.frequency.value = 880;
          gain.gain.setValueAtTime(0.0001, ctx.currentTime);
          gain.gain.exponentialRampToValueAtTime(0.25, ctx.currentTime + 0.03);
          gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.65);
          osc.connect(gain).connect(ctx.destination);
          osc.start();
          osc.stop(ctx.currentTime + 0.7);
        }
        function renderAlert(alert) {
          if (!alert) return;
          statusEl.textContent = "마지막 수신: " + alert.createdAt;
          const vlmAnalysis = escapeHtml(alert.vlmAnalysis || alert.analysis || "");
          const detectionSummary = escapeHtml(alert.detectionSummary || "\uD0D0\uC9C0 \uB0B4\uC6A9\uC774 \uC5C6\uC2B5\uB2C8\uB2E4.");
          alertEl.innerHTML = `
            <div class="level"><span class="dot"></span>${alert.threatLevel} 위험</div>
            <div class="time">${alert.createdAt}</div>
            ${alert.evidenceUrl ? `<img src="${alert.evidenceUrl}?t=${encodeURIComponent(alert.id)}" alt="위험 화면">` : ""}
            <div class="detail-grid">
              <article class="detail-card">
                <h2 class="detail-title">VLM \uBD84\uC11D</h2>
                <p class="detail-text">${vlmAnalysis}</p>
              </article>
              <article class="detail-card">
                <h2 class="detail-title">\uD0D0\uC9C0 \uB0B4\uC6A9</h2>
                <p class="detail-text">${detectionSummary}</p>
              </article>
            </div>`;
          playAlarm();
          if (navigator.vibrate) navigator.vibrate([220, 90, 220, 90, 420]);
          if ("Notification" in window && Notification.permission === "granted") {
            new Notification(alert.title, { body: alert.vlmAnalysis || alert.analysis });
          }
        }
        function escapeHtml(value) {
          return String(value)
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#39;");
        }
        fetch("/latest").then(r => r.json()).then(renderAlert).catch(() => {});
        const events = new EventSource("/events");
        events.onopen = () => statusEl.textContent = "GUI와 연결됨";
        events.onerror = () => statusEl.textContent = "연결 재시도 중";
        events.addEventListener("alert", event => renderAlert(JSON.parse(event.data)));
        </script>
        </body>
        </html>
        """;

    public void Dispose()
    {
        Stop();
    }

    private sealed record SseClient(TcpClient TcpClient, StreamWriter Writer) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Writer.Dispose();
            }
            catch
            {
            }

            try
            {
                TcpClient.Dispose();
            }
            catch
            {
            }
        }
    }
}
