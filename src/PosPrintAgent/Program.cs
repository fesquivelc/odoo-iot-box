using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("POS_PRINT_AGENT_PORT") ?? "18181";
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Host.UseWindowsService(options => options.ServiceName = "POS Print Agent");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var origin = Environment.GetEnvironmentVariable("POS_PRINT_AGENT_ORIGIN");
    if (string.IsNullOrWhiteSpace(origin)) policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    else policy.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod();
}));
builder.Services.AddSingleton<AgentState>();
builder.Services.AddSingleton<PrintQueue>();
builder.Services.AddHostedService<PrintWorker>();
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();

app.MapGet("/api/status", (AgentState state) => Results.Ok(state.PublicView()));
app.MapGet("/api/printers", (AgentState state) => Results.Ok(state.Printers.Values));
app.MapPost("/api/printers", (PrinterConfig printer, AgentState state) =>
{
    if (string.IsNullOrWhiteSpace(printer.Name) || string.IsNullOrWhiteSpace(printer.Host) ||
        printer.Port is < 1 or > 65535)
        return Results.BadRequest(new { error = "Nombre, host y puerto son obligatorios." });
    state.Printers[printer.Name] = printer;
    state.Save();
    return Results.Ok(printer);
});
app.MapDelete("/api/printers/{name}", (string name, AgentState state) =>
{
    if (!state.Printers.TryRemove(name, out _)) return Results.NotFound();
    state.Save();
    return Results.NoContent();
});

app.MapPost("/hw_proxy/default_printer_action", async (HttpRequest request, PrintQueue queue, AgentState state) =>
{
    var action = await JsonSerializer.DeserializeAsync<PrinterAction>(request.Body,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (action is null || action.Action != "print_receipt" || string.IsNullOrWhiteSpace(action.Receipt))
        return Results.BadRequest(new { result = false, message = "Acción de impresión inválida." });
    if (!state.TryAuthorize(request, action.Token))
        return Results.Unauthorized();
    var printerName = action.PrinterName ?? state.DefaultPrinter;
    if (printerName is null || !state.Printers.TryGetValue(printerName, out var printer))
        return Results.NotFound(new { result = false, message = "No se encontró la impresora configurada." });
    var job = await queue.EnqueueAsync(new PrintJob(action.JobId ?? Guid.NewGuid().ToString("N"), printer, action.Receipt));
    return Results.Ok(new { result = job.Success, job_id = job.JobId, message = job.Message });
});

app.MapPost("/api/test-print", async (TestPrintRequest request, PrintQueue queue, AgentState state) =>
{
    if (!state.TryAuthorize(request.Token)) return Results.Unauthorized();
    if (!state.Printers.TryGetValue(request.PrinterName, out var printer)) return Results.NotFound();
    var job = await queue.EnqueueAsync(new PrintJob(Guid.NewGuid().ToString("N"), printer,
        EscPos.Text("POS Print Agent\nPrueba de impresion\n") + EscPos.Cut(), true));
    return Results.Ok(job);
});

app.MapFallbackToFile("index.html");
await app.RunAsync();

public sealed record PrinterConfig(string Name, string Host, int Port = 9100, bool Enabled = true, int PaperWidth = 576);
public sealed record PrinterAction(string Action, string? Receipt, string? PrinterName = null,
    string? JobId = null, string? Token = null);
public sealed record TestPrintRequest(string PrinterName, string? Token);
public sealed record PrintJob(string JobId, PrinterConfig Printer, string Payload, bool IsEscPos = false);
public sealed record PrintResult(string JobId, bool Success, string Message);

public sealed class AgentState
{
    public ConcurrentDictionary<string, PrinterConfig> Printers { get; } = new();
    public string? DefaultPrinter { get; set; }
    public string Token { get; private set; }
    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PosPrintAgent", "settings.json");
    public AgentState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        if (File.Exists(_file))
        {
            var saved = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_file));
            Token = saved?.Token ?? NewToken();
            DefaultPrinter = saved?.DefaultPrinter;
            foreach (var printer in saved?.Printers ?? []) Printers[printer.Name] = printer;
        }
        else Token = Environment.GetEnvironmentVariable("POS_PRINT_AGENT_TOKEN") ?? NewToken();
        Save();
    }
    private static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    public void Save() => File.WriteAllText(_file, JsonSerializer.Serialize(
        new PersistedState(Token, DefaultPrinter, Printers.Values), new JsonSerializerOptions { WriteIndented = true }));
    public bool TryAuthorize(HttpRequest request, string? supplied = null) =>
        string.Equals(request.Headers["X-Print-Agent-Token"].FirstOrDefault() ?? supplied, Token,
            StringComparison.Ordinal);
    public bool TryAuthorize(string? supplied) => string.Equals(supplied, Token, StringComparison.Ordinal);
    public object PublicView() => new { defaultPrinter = DefaultPrinter, token = Token, printers = Printers.Count };
}
public sealed record PersistedState(string Token, string? DefaultPrinter, IEnumerable<PrinterConfig> Printers);

public sealed class PrintQueue
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PrintResult>> _seen = new();
    private readonly Channel<PrintJob> _jobs = Channel.CreateUnbounded<PrintJob>();
    public async Task<PrintResult> EnqueueAsync(PrintJob job)
    {
        var candidate = new TaskCompletionSource<PrintResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = _seen.GetOrAdd(job.JobId, candidate);
        if (ReferenceEquals(completion, candidate)) await _jobs.Writer.WriteAsync(job);
        return await completion.Task;
    }
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync(cancellationToken))
        {
            var result = await TcpPrinter.PrintAsync(job);
            if (_seen.TryGetValue(job.JobId, out var completion)) completion.TrySetResult(result);
        }
    }
}

public sealed class PrintWorker(PrintQueue queue) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => queue.RunAsync(stoppingToken);
}

public static class TcpPrinter
{
    public static async Task<PrintResult> PrintAsync(PrintJob job)
    {
        try
        {
            var payload = job.IsEscPos ? Encoding.ASCII.GetBytes(job.Payload) : await RasterEscPos.FromDataUrlAsync(job.Payload, job.Printer.PaperWidth);
            using var client = new TcpClient();
            await client.ConnectAsync(job.Printer.Host, job.Printer.Port);
            await client.GetStream().WriteAsync(payload);
            return new(job.JobId, true, "Impresión enviada.");
        }
        catch (Exception ex) { return new(job.JobId, false, ex.Message); }
    }
}

public static class RasterEscPos
{
    public static async Task<byte[]> FromDataUrlAsync(string dataUrl, int configuredWidth)
    {
        var comma = dataUrl.IndexOf(',');
        var bytes = Convert.FromBase64String(comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl);
        using var image = await Image.LoadAsync<Rgba32>(new MemoryStream(bytes));
        var width = Math.Min(image.Width, configuredWidth is 384 or 576 ? configuredWidth : 576);
        var height = Math.Max(1, (int)Math.Round(image.Height * (double)width / image.Width));
        image.Mutate(ctx => ctx.Resize(width, height));
        var rowBytes = (width + 7) / 8;
        var output = new List<byte>(height * rowBytes + 16) { 0x1b, 0x40 };
        for (var y = 0; y < height; y++)
        {
            output.AddRange(new byte[] { 0x1d, 0x76, 0x30, 0x00,
                (byte)(rowBytes & 0xff), (byte)(rowBytes >> 8), 1, 0 });
            var rowStart = output.Count;
            output.AddRange(new byte[rowBytes]);
            for (var x = 0; x < width; x++)
            {
                var pixel = image[x, y];
                var luminance = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
                if (luminance < 160) output[rowStart + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        output.AddRange(new byte[] { 0x0a, 0x0a, 0x1d, 0x56, 0x00 });
        return output.ToArray();
    }
}

public static class EscPos
{
    public static string Text(string value) => "\x1b@" + value;
    public static string Cut() => "\n\n\x1dV\x00";
}
