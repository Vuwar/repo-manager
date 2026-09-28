// Test double for a dev server. Options:
//   --port N          listen on 127.0.0.1:N; answers every request with 200 (or 500 when unhealthy)
//   --port-env NAME   read the port from environment variable NAME
//   --unhealthy-file P   answer 500 while file P exists
//   --delay-listen S  wait S seconds before listening
//   --lines N         print N numbered lines at start (half of them to stderr)
//   --crash-after S   exit after S seconds with --exit-code (default 3)
//   --exit-code C
//   --child           spawn a child FakeServer (no port) that just sleeps, like npm spawning node
//   --pid-file P      write "<own pid> <child pid>" to P
//   --print-env NAME  print NAME=<value>
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

int? port = null;
string? unhealthyFile = null, pidFile = null;
double delayListen = 0, crashAfter = -1;
int lines = 0, exitCode = 3;
bool child = false;
var printEnv = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    string Next() => args[++i];
    switch (args[i])
    {
        case "--port": port = int.Parse(Next()); break;
        case "--port-env": port = int.Parse(Environment.GetEnvironmentVariable(Next()) ?? "0"); break;
        case "--unhealthy-file": unhealthyFile = Next(); break;
        case "--delay-listen": delayListen = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
        case "--lines": lines = int.Parse(Next()); break;
        case "--crash-after": crashAfter = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
        case "--exit-code": exitCode = int.Parse(Next()); break;
        case "--child": child = true; break;
        case "--pid-file": pidFile = Next(); break;
        case "--print-env": printEnv.Add(Next()); break;
    }
}

Console.Out.Flush();
Console.WriteLine($"fake server pid {Environment.ProcessId} starting");
foreach (var n in printEnv) Console.WriteLine($"{n}={Environment.GetEnvironmentVariable(n)}");
for (var i = 1; i <= lines; i++)
{
    if (i % 2 == 0) Console.Error.WriteLine($"line {i} (stderr)");
    else Console.WriteLine($"line {i}");
}

int childPid = 0;
if (child)
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!, "--lines 1") { UseShellExecute = false };
    var c = Process.Start(psi)!;
    childPid = c.Id;
    Console.WriteLine($"child pid {childPid}");
}
if (pidFile != null) File.WriteAllText(pidFile, $"{Environment.ProcessId} {childPid}");

if (crashAfter >= 0)
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(crashAfter));
        Console.Error.WriteLine($"crashing with code {exitCode}");
        Environment.Exit(exitCode);
    });
}

if (port is > 0)
{
    if (delayListen > 0) await Task.Delay(TimeSpan.FromSeconds(delayListen));
    var listener = new TcpListener(IPAddress.Loopback, port.Value);
    listener.Start();
    Console.WriteLine($"listening on http://127.0.0.1:{port}");
    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = Task.Run(async () =>
        {
            using (client)
            {
                var stream = client.GetStream();
                // A request can arrive in several reads: read until the end of its headers (or buffer full / EOF).
                var buf = new byte[4096];
                var read = 0;
                try
                {
                    while (read < buf.Length && buf.AsSpan(0, read).IndexOf("\r\n\r\n"u8) < 0)
                    {
                        var n = await stream.ReadAsync(buf.AsMemory(read));
                        if (n == 0) break;
                        read += n;
                    }
                }
                catch { return; }
                var bad = unhealthyFile != null && File.Exists(unhealthyFile);
                var body = bad ? "unhealthy" : "ok";
                var resp = $"HTTP/1.1 {(bad ? "500 Internal Server Error" : "200 OK")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                try { await stream.WriteAsync(Encoding.ASCII.GetBytes(resp)); } catch { }
            }
        });
    }
}

// No port: run until killed.
await Task.Delay(Timeout.Infinite);
