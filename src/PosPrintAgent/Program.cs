using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
app.MapGet("/api/devices", async (HttpRequest request, AgentState state) =>
{
    if (!state.TryAuthorize(request)) return Results.Unauthorized();
    return Results.Ok(await PrinterDiscovery.FindAsync());
});
app.MapPost("/api/printers", (PrinterConfig printer, AgentState state) =>
{
    if (string.IsNullOrWhiteSpace(printer.Name) ||
        (printer.Protocol == "tcp" && (string.IsNullOrWhiteSpace(printer.Host) || printer.Port is < 1 or > 65535)) ||
        (printer.Protocol is "windows-spooler" or "cups" && string.IsNullOrWhiteSpace(printer.DeviceName)))
        return Results.BadRequest(new { error = "Nombre, host y puerto son obligatorios." });
    state.Printers[printer.Name] = printer;
    state.Save();
    return Results.Ok(printer);
});
app.MapPost("/api/printers/from-device", (DeviceRegistration registration, AgentState state) =>
{
    if (string.IsNullOrWhiteSpace(registration.Name)) return Results.BadRequest();
    var protocol = registration.IsUsb
        ? (OperatingSystem.IsWindows() ? "windows-spooler" : "cups")
        : "tcp";
    var printer = new PrinterConfig(registration.Name, registration.Host ?? "127.0.0.1",
        registration.Port, true, registration.PaperWidthMm, protocol,
        registration.IsUsb ? registration.Name : null);
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

public sealed record PrinterConfig(string Name, string Host, int Port = 9100, bool Enabled = true,
    int PaperWidthMm = 80, string Protocol = "tcp", string? DeviceName = null);
public sealed record PrinterDevice(string Name, string Connection, string? Driver = null, bool IsUsb = false);
public sealed record DeviceRegistration(string Name, string? Host, int Port = 9100, int PaperWidthMm = 80, bool IsUsb = false);
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
            var payload = job.IsEscPos ? Encoding.ASCII.GetBytes(job.Payload) : await RasterEscPos.FromDataUrlAsync(job.Payload, job.Printer.PaperWidthMm);
            if (job.Printer.Protocol == "windows-spooler")
                return new(job.JobId, WindowsRawPrinter.Print(job.Printer.DeviceName!, payload), "Trabajo enviado al spooler de Windows.");
            if (job.Printer.Protocol == "cups")
                return await CupsPrinter.PrintAsync(job, payload);
            using var client = new TcpClient();
            await client.ConnectAsync(job.Printer.Host, job.Printer.Port);
            await client.GetStream().WriteAsync(payload);
            await client.GetStream().FlushAsync();
            await Task.Delay(500);
            return new(job.JobId, true, "Impresión enviada.");
        }
        catch (Exception ex) { return new(job.JobId, false, ex.Message); }
    }
}

public static class WindowsRawPrinter
{
    public static bool Print(string printerName, byte[] payload)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows spooler no disponible.");
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
            throw new InvalidOperationException($"No se pudo abrir la impresora: {Marshal.GetLastWin32Error()}");
        try
        {
            var document = new DocInfo { DocName = "POS Receipt", DataType = "RAW" };
            if (StartDocPrinter(handle, 1, ref document) == 0 || !StartPagePrinter(handle))
                throw new InvalidOperationException($"No se pudo iniciar el trabajo: {Marshal.GetLastWin32Error()}");
            try
            {
                if (!WritePrinter(handle, payload, payload.Length, out var written, IntPtr.Zero) || written != payload.Length)
                    throw new InvalidOperationException($"No se pudo escribir el trabajo: {Marshal.GetLastWin32Error()}");
            }
            finally { EndPagePrinter(handle); EndDocPrinter(handle); }
            return true;
        }
        finally { ClosePrinter(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo { public string DocName; public string OutputFile; public string DataType; }
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int StartDocPrinter(IntPtr handle, int level, ref DocInfo info);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndDocPrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool StartPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool WritePrinter(IntPtr handle, byte[] data, int count, out int written, IntPtr reserved);
}

public static class CupsPrinter
{
    public static async Task<PrintResult> PrintAsync(PrintJob job, byte[] payload)
    {
        var startInfo = new ProcessStartInfo("lp", $"-d \"{job.Printer.DeviceName}\" -")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo iniciar CUPS.");
        await process.StandardInput.BaseStream.WriteAsync(payload);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return new(job.JobId, process.ExitCode == 0, process.ExitCode == 0 ? "Trabajo enviado a CUPS." : "CUPS rechazó el trabajo.");
    }
}

public static class RasterEscPos
{
    public static async Task<byte[]> FromDataUrlAsync(string dataUrl, int configuredWidth)
    {
        var comma = dataUrl.IndexOf(',');
        var bytes = Convert.FromBase64String(comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl);
        using var image = await Image.LoadAsync<Rgba32>(new MemoryStream(bytes));
        var targetWidth = configuredWidth switch { 58 => 384, 80 => 576, _ => 576 };
        var width = Math.Min(image.Width, targetWidth);
        var height = Math.Max(1, (int)Math.Round(image.Height * (double)width / image.Width));
        image.Mutate(ctx => ctx.Resize(width, height));
        var rowBytes = (width + 7) / 8;
        var output = new List<byte>(height * rowBytes + 16)
        {
            0x1b, 0x40, 0x1d, 0x76, 0x30, 0x00,
            (byte)(rowBytes & 0xff), (byte)(rowBytes >> 8),
            (byte)(height & 0xff), (byte)(height >> 8),
        };
        var imageStart = output.Count;
        output.AddRange(new byte[height * rowBytes]);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = image[x, y];
                var luminance = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
                if (luminance < 160) output[imageStart + y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        // Leave enough physical margin for printers whose cutter is offset
        // from the print head, especially for the final POS signature.
        output.AddRange(new byte[] { 0x0a, 0x0a, 0x0a, 0x0a, 0x0a, 0x0a, 0x1d, 0x56, 0x00 });
        return output.ToArray();
    }
}

public static class PrinterDiscovery
{
    public static async Task<IReadOnlyList<PrinterDevice>> FindAsync() =>
        OperatingSystem.IsWindows() ? await FindWindowsAsync() : await FindUnixAsync();

    private static async Task<IReadOnlyList<PrinterDevice>> FindWindowsAsync()
    {
        const string command = "Get-Printer | Select Name,PortName,DriverName | ConvertTo-Json -Compress";
        var json = await RunAsync("powershell", $"-NoProfile -NonInteractive -Command \"{command}\"");
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json);
        IEnumerable<JsonElement> rows = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
            : new[] { document.RootElement };
        return rows.Select(row =>
        {
            var name = row.GetProperty("Name").GetString() ?? "";
            var port = row.TryGetProperty("PortName", out var portValue) ? portValue.GetString() ?? "" : "";
            var driver = row.TryGetProperty("DriverName", out var driverValue) ? driverValue.GetString() : null;
            return new PrinterDevice(name, port, driver, port.StartsWith("USB", StringComparison.OrdinalIgnoreCase));
        }).Where(device => !string.IsNullOrWhiteSpace(device.Name)).ToList();
    }

    private static async Task<IReadOnlyList<PrinterDevice>> FindUnixAsync()
    {
        var output = await RunAsync("lpstat", "-v");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())
            .Where(line => line.StartsWith("device for ", StringComparison.OrdinalIgnoreCase)).Select(line =>
            {
                var separator = line.IndexOf(':');
                var name = line[11..separator].Trim();
                var connection = line[(separator + 1)..].Trim();
                return new PrinterDevice(name, connection, IsUsb: connection.StartsWith("usb", StringComparison.OrdinalIgnoreCase));
            }).ToList();
    }

    private static async Task<string> RunAsync(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            return process is null ? "" : await process.StandardOutput.ReadToEndAsync();
        }
        catch { return ""; }
    }
}

public static class EscPos
{
    public static string Text(string value) => "\x1b@" + value;
    public static string Cut() => "\n\n\x1dV\x00";
}
