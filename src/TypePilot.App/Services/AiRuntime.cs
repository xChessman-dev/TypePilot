using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using TypePilot.Core;

namespace TypePilot.App;

public sealed class AiRuntime : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
    private readonly Timer _idle;
    private Process? _process;
    private ProcessJob? _job;
    private DateTime _lastUse = DateTime.UtcNow;
    private bool _disposed;
    public bool IsLoaded => _process is { HasExited: false };
    public long MemoryMb
    {
        get
        {
            try { if (_process is null || _process.HasExited) return 0; _process.Refresh(); return _process.WorkingSet64 / 1048576; }
            catch (InvalidOperationException) { return 0; }
        }
    }
    public event Action<string>? StatusChanged;
    public AiRuntime() => _idle = new Timer(_ => TryIdleUnload(), null, 5000, 5000);
    public static bool IsInstalled(string root) => File.Exists(Path.Combine(root, "runtime", "llama-server.exe")) && File.Exists(Path.Combine(root, "models", "Qwen3-4B-Q4_K_M.gguf"));
    public async Task<string> RewriteAsync(PilotSettings settings, string text, RewriteStyle style, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var endpoint = new Uri(settings.AiEndpoint);
            _ = new RewriteClient(endpoint, _http); // Validate before starting a process or sending text.
            if (!IsLoaded) await StartAsync(settings, endpoint, token);
            StatusChanged?.Invoke("ИИ переформулирует текст…");
            return await new RewriteClient(endpoint, _http).RewriteAsync(text, style, token);
        }
        catch (OperationCanceledException) { StopProcess(); throw; }
        finally { _lastUse = DateTime.UtcNow; _gate.Release(); }
    }
    private async Task StartAsync(PilotSettings settings, Uri endpoint, CancellationToken token)
    {
        StopProcess();
        if (!IsInstalled(settings.AiRoot)) throw new FileNotFoundException("Локальная модель не установлена. Выполни tools/setup-ai.ps1 (файлы на F).");
        using (var listener = new TcpListener(System.Net.IPAddress.Parse(endpoint.Host), endpoint.Port))
        {
            try { listener.Start(); } catch (SocketException) { throw new InvalidOperationException("Порт ИИ уже занят. TypePilot не подключается к неизвестному процессу."); }
            listener.Stop();
        }
        StatusChanged?.Invoke("Загружаю Qwen3 4B на GPU…");
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var start = new ProcessStartInfo(Path.Combine(settings.AiRoot, "runtime", "llama-server.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.Combine(settings.AiRoot, "runtime")
        };
        foreach (var arg in new[] { "--model", Path.Combine(settings.AiRoot, "models", "Qwen3-4B-Q4_K_M.gguf"), "--host", endpoint.Host, "--port", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--api-key", key, "--ctx-size", "4096", "--threads", "2", "--threads-batch", "2", "--threads-http", "2", "--n-gpu-layers", "99", "--batch-size", "256", "--ubatch-size", "128", "--parallel", "1", "--no-webui", "--log-disable", "--reasoning", "off" }) start.ArgumentList.Add(arg);
        _process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить локальный ИИ.");
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        _job = new ProcessJob(_process);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new InvalidOperationException("ИИ завершился при загрузке. Проверь Vulkan-драйвер видеокарты и модель.");
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                probe.CancelAfter(1000);
                try
                {
                    using var response = await _http.GetAsync(new Uri(endpoint, "health"), probe.Token);
                    if (response.IsSuccessStatusCode) { StatusChanged?.Invoke("Qwen3 4B готова · локально"); return; }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Delay(200, token);
            }
            throw new TimeoutException("Модель не загрузилась за минуту. Обычный Т9 продолжает работать.");
        }
        catch { StopProcess(); throw; }
    }
    public async Task UnloadAsync()
    {
        await _gate.WaitAsync();
        try { StopProcess(); StatusChanged?.Invoke("ИИ выгружен · память освобождена"); }
        finally { _gate.Release(); }
    }
    private void TryIdleUnload()
    {
        if (_disposed || !_gate.Wait(0)) return;
        try
        {
            if (IsLoaded && DateTime.UtcNow - _lastUse > TimeSpan.FromSeconds(30))
            { StopProcess(); StatusChanged?.Invoke("ИИ выгружен после простоя · Т9 активен"); }
        }
        finally { _gate.Release(); }
    }
    private void StopProcess()
    {
        _job?.Dispose(); _job = null;
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(true); } catch (InvalidOperationException) { }
            _process.Dispose(); _process = null;
        }
        _http.DefaultRequestHeaders.Authorization = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _idle.Dispose(); StopProcess(); _http.Dispose();
    }
}
