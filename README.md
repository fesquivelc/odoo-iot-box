# POS Print Agent

Cross-platform local print agent for Odoo Community POS. The agent exposes a
small HTTP API compatible with the Community `HWPrinter` flow and a vanilla
JavaScript configuration page.

## Run

```bash
dotnet run --project src/PosPrintAgent
```

Open `http://127.0.0.1:18181`. The first run creates a local API token, which
is persisted in the operating system application-data directory. The current
MVP supports ESC/POS over TCP (port 9100); printer adapters for CUPS
and the Windows spooler can be added without changing the API.

The agent deliberately binds to loopback by default. Do not expose the port
to the public internet.

## Windows service

Run PowerShell as a normal user from this directory. The script requests
administrator privileges through UAC, publishes the application, registers
`PosPrintAgent` as an automatic Windows Service, and starts it:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install-windows.ps1
```

The configuration page is served by ASP.NET Core static files at
`http://127.0.0.1:18181`. It is vanilla JavaScript, not Razor: this keeps the
configuration UI independent of server-side rendering while remaining part of
the same executable. To remove the service, run `.\uninstall-windows.ps1`.

## GitHub artifacts

The GitHub Actions workflows create one self-contained ZIP per runtime:

- `pos-print-agent-win-x64.zip`
- `pos-print-agent-linux-x64.zip`
- `pos-print-agent-osx-arm64.zip`

The release workflow attaches all three ZIP files to tags matching `v*`.
