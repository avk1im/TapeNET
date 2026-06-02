# Remote Backend Test Setup Guide

This guide explains how to run `RemoteHostBackendTests` against a live `TapeServiceNET`
instance, and how `LocalHostBackendTests` work without any setup at all.

---

## Local host tests (no setup required)

`LocalHostBackendTests` spin up an **in-process** gRPC server (via `LocalHostTapeServiceFixture`)
on a random localhost port before the test run and tear it down afterwards. They always run and
require no external service, no configuration file, and no firewall rules. The 54 test cases
under this class are the baseline that proves the remote backend plumbing is correct.

---

## Remote host tests

`RemoteHostBackendTests` run the **same 54 test cases** through an actual network connection to
a separately deployed `TapeServiceNET` process. When no host is configured, or when the service
is unreachable, every test in the class skips gracefully — no failures, no crashes.

S. below on how to generate the development-time certificate.

> **Note on `TapeNET.runsettings`:** The solution-level runsettings file excludes
> `LargeFileTests` from routine runs, but remote tests are **not** excluded by default (the
> filter line is commented out). This means remote tests always run when you click
> "Run All Tests" in Visual Studio — they just skip immediately if the service isn't up.
> To exclude them from VS Test Explorer runs, uncomment the combined `TestCaseFilter` line in
> `TapeNET.runsettings`.

---

### Step 1 — Deploy and run the gRPC service

The published binary is named **`tapesvc`** (set via `<AssemblyName>tapesvc</AssemblyName>`
in the project file). Assuming the target machine's IP is `192.168.178.142`:

```powershell
# On your dev machine — publish a self-contained release build
dotnet publish TapeServiceNET -c Release -o D:\TapeService

# Copy the output to the target machine (or publish directly there)
```

```powershell
# On 192.168.178.142 — run the service
cd D:\TapeService
dotnet tapesvc.dll
```

The service listens on the port configured in `appsettings.json` (default: **50551**). The
relevant section looks like this:

```json
"Kestrel": {
  "Endpoints": {
    "Grpc": {
      "Url": "http://*:50551",
      "Protocols": "Http2"
    }
  }
}
```

The `*` wildcard binds to all network interfaces, which is required for remote access. If you
change this to `127.0.0.1`, the service will only accept local connections.

The `TapeSession` section controls idle-session cleanup:

```json
"TapeSession": {
  "IdleTimeout": "00:30:00",
  "ReaperInterval": "00:05:00"
}
```

Sessions idle for longer than `IdleTimeout` are reaped by the background reaper service (see
primer for details). The client-side keepalive ping fires every 10 minutes by default and resets
`LastActivity`, so interactive sessions are not reaped prematurely.

---

### Step 2 — Open the firewall on the remote machine

```powershell
# On 192.168.178.142 (elevated PowerShell)
New-NetFirewallRule -DisplayName "TapeServiceNET gRPC" `
    -Direction Inbound -Protocol TCP -LocalPort 50551 -Action Allow
```

---

### Step 3 — Verify connectivity from your dev machine

```powershell
# On your dev machine
Test-NetConnection -ComputerName 192.168.178.142 -Port 50551
```

You should see `TcpTestSucceeded : True`.

---

### Step 4 — Create the test settings file

```powershell
cd D:\Documents.DEV\Projects\TapeNET\TapeLibNET.Tests
Copy-Item remote-test-settings.template.json remote-test-settings.json
```

Edit `remote-test-settings.json` and fill in the host:

```json
{
  "RemoteHost": "192.168.178.142",
  "RemotePort": 50551,
  "UseTls": false
}
```

This file is `.gitignore`d and will not be committed.

**Where to place the file:** `RemoteHostTapeServiceFixture` searches two locations
automatically, in this order:

1. Next to the test assembly — e.g.  
   `TapeLibNET.Tests\bin\Debug\net8.0-windows\remote-test-settings.json`
2. In the test project directory —  
   `TapeLibNET.Tests\remote-test-settings.json`

For **Visual Studio** runs, placing the file directly in the `TapeLibNET.Tests\` folder is
sufficient — the fixture walks up from the assembly location to find the project directory.

For **`dotnet test` CLI** runs, the safest approach is to copy the file into the build output:

```powershell
Copy-Item remote-test-settings.json bin\Debug\net8.0-windows\
```

---

### Step 5 — Alternatively, use environment variables

Environment variables take precedence over the JSON file and are useful for CI pipelines:

```powershell
$env:TAPE_REMOTE_HOST = "192.168.178.142"
$env:TAPE_REMOTE_PORT = "50551"   # optional, default: 50551
$env:TAPE_REMOTE_TLS  = "false"   # optional, default: false
```

When `TAPE_REMOTE_HOST` is unset and no JSON file exists, all 54 `RemoteHostBackendTests`
skip gracefully.

---

### Step 6 — Run the tests

**From the command line:**

```powershell
cd D:\Documents.DEV\Projects\TapeNET

# Run only remote host tests
dotnet test TapeLibNET.Tests --filter "FullyQualifiedName~RemoteHostBackendTests"

# Run everything (remote tests execute if the service is up, skip otherwise)
dotnet test TapeLibNET.Tests
```

**From Visual Studio:**

Open **Test Explorer** → filter by class name `RemoteHostBackendTests` → **Run**. Tests will
execute against `192.168.178.142:50551` instead of skipping.

To exclude remote tests from VS routine runs, uncomment the combined `TestCaseFilter` line in
`TapeNET.runsettings` (at the solution root) and re-select the file via the Test Explorer gear
icon.

---

### Step 7 — Verify results

You should see **54 passed** tests under `RemoteHostBackendTests`, mirroring the
`LocalHostBackendTests` results but running through the actual network to the remote machine.

---

## Quick reference — Environment variables (for CI)

| Variable           | Example           | Required                    |
|--------------------|-------------------|-----------------------------|
| `TAPE_REMOTE_HOST` | `192.168.178.142` | ✅ Yes                       |
| `TAPE_REMOTE_PORT` | `50551`           | No (default: `50551`)       |
| `TAPE_REMOTE_TLS`  | `false`           | No (default: `false`)       |

---

## Stopping the service

If you started the service in a terminal with `dotnet tapesvc.dll`, press **Ctrl+C**. ASP.NET
Core will gracefully drain in-flight requests, dispose all registered sessions (you'll see
`Session closed: <id>` log entries), and exit.

To run as a Windows Service instead:

```powershell
# Install (elevated PowerShell, on 192.168.178.142)
sc.exe create tapesvc binPath="C:\TapeService\tapesvc.exe" start=auto
sc.exe start tapesvc

# Stop and remove
sc.exe stop tapesvc
sc.exe delete tapesvc
```

## How to Generate the Development HTTPS Certificate (tapesvc.pfx)

You only need one command to create and export the certificate, and optionally one more to trust it on the machine.

Below is the canonical sequence.

1. Create & Export the Developer Certificate
Run this on the machine hosting TapeServiceNET (or run locally and copy the file):
powershell
dotnet dev-certs https -ep D:\TapeService\certs\tapesvc.pfx -p "YourPasswordHere"
Important notes
•	The directory must exist (D:\TapeService\certs\).
•	If your password contains $, &, (, ), etc., quote it:
powershell
-p "MySecretPassword$001"
•	If you prefer escaping instead of quoting:
powershell
-p MySecretPassword`$001
This produces:
Code
D:\TapeService\certs\tapesvc.pfx

2. Trust the certificate (optional but recommended for local dev)
powershell
dotnet dev-certs https --trust
This adds the dev certificate to the Windows trust store so Kestrel can serve HTTPS without browser warnings.

3. (Optional) Clean and regenerate everything
If you want a completely fresh certificate:
powershell
dotnet dev-certs https --clean
dotnet dev-certs https --trust
dotnet dev-certs https -ep D:\TapeService\certs\tapesvc.pfx -p "YourPasswordHere"

4. Configure TapeServiceNET to use the certificate
In appsettings.json:
json
"Kestrel": {
  "Endpoints": {
    "Grpc": {
      "Url": "https://0.0.0.0:50551",
      "Protocols": "Http2",
      "Certificate": {
        "Path": "certs/tapesvc.pfx",
        "Password": "YourPasswordHere"
      }
    }
  }
}
Or keep HTTP for now and only use the certificate later — your earlier setup used plain HTTP (UseTls: false), so this step is optional unless you want encrypted transport.
5. Verify the certificate exists
powershell
Test-Path D:\TapeService\certs\tapesvc.pfx
Should return:
True


