using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// A stand-in for an agent client, so tests drive a real child process without any client installed.
//
//   IDevelop.FakeAgent --rules <rules.json> -- <the arguments the real client gets>
//   IDevelop.FakeAgent --sleep-forever
//   IDevelop.FakeAgent --spawn-sleeper <file>   start a sleeping copy, write its pid, and exit, as spawnThroughCmd runs it
//
// The rules file is {"rules": [{"when": ["auth", "status"], "has": ["--resume", "id"], "steps": [{"print": "..."}, {"exit": 0}]}]}.
// Optional top-level "launches" names a folder for <pid>-<guid>.json argument markers, written before protocol input.
// The first rule whose "when" is a prefix of the client arguments after any leading "-c key=value" pairs, as Git takes
// them, and whose optional "has" appears among them in that order without gaps, runs its steps in order. Steps:
//   recordArguments <file>        write the client arguments as a JSON array
//   recordWorkingDirectory <file> write the current folder
//   captureStdin <file>           copy stdin to the file until it closes
//                                interactive clients capture their decoded first prompt
//   capturePrompt <file>          record the decoded prompt, including stream-json one-shot input
//   readLine <file>               read and record one stdin frame
//   waitForLine <pattern>         wait for a stdin frame containing the pattern
//   echoId <line>                 print the line with $id replaced by the last matched frame's id
//   closeStdin                   close this process's pipe readers, so host writes fail
//   waitForStdinEnd               read stdin until it closes
//   print <line>, stderr <line>   write one line
//   replay <file>                 write a recorded stream line by line
//   sleep <milliseconds>
//   lockFile <file>               open the file with no sharing and hold it until exit, as another iDevelop holds run.lock
//   waitForFile <file>            wait until the file exists, so a test decides when the client goes on. Exit 97 if its
//                                 folder is deleted, because the test that owned it has ended
//   spawnSleepingChild <file>     start a copy with --sleep-forever that shares the pipes, and write its pid
//   spawnThroughCmd <file>        Windows only: run a copy through cmd.exe /c that starts a sleeping copy, writes its
//                                 pid, and exits, so the sleeper shares the pipes and its parent is gone
//   hang                          wait until killed
//   write [<file>, <text>]        write the text to the file, relative to the current folder
//   scripted <folder>             count this call in <folder>/count, copy stdin to <folder>/<n>.stdin, and run the steps in
//                                 <folder>/<n>.json, so each turn of a conversation can answer differently
//   exit <code>

if (args is ["--sleep-forever"])
{
    Thread.Sleep(Timeout.Infinite);
}

if (args is ["--spawn-sleeper", var sleeperFile])
{
    File.WriteAllText(sleeperFile, StartSleeper().Id.ToString());
    return 0;
}

string rulesFile;
string[] clientArguments;
if (args is ["--rules", var explicitRules, "--", .. var explicitArguments])
{
    rulesFile = explicitRules;
    clientArguments = explicitArguments;
}
else
{
    rulesFile = Environment.ProcessPath + ".rules.json";
    clientArguments = args;
}

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true, NewLine = "\n" };
using var rules = JsonDocument.Parse(File.ReadAllBytes(rulesFile));
if (rules.RootElement.TryGetProperty("launches", out var launches))
{
    var folder = Directory.CreateDirectory(launches.GetString()!).FullName;
    using var marker = new FileStream(Path.Combine(folder, $"{Environment.ProcessId}-{Guid.NewGuid():N}.json"), FileMode.CreateNew);
    JsonSerializer.Serialize(marker, clientArguments);
}

using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
var appServer = clientArguments.Contains("app-server");
var claudeStream = clientArguments.Contains("--include-partial-messages");
string? prompt = null;
string? framesFile = null;
JsonElement? matchedLine = null;
JsonElement? threadRequest = null;
JsonElement? turnRequest = null;
List<FileStream> held = [];
if (appServer)
{
    var initialize = ReadInput();
    stdout.WriteLine(JsonSerializer.Serialize(new { id = initialize.GetProperty("id"), result = new { } }));
    ReadInput();
    threadRequest = ReadInput();
}
else if (claudeStream)
{
    var initialize = ReadInput();
    stdout.WriteLine(JsonSerializer.Serialize(new { type = "control_response", response = new { subtype = "success", request_id = initialize.GetProperty("request_id") } }));
    prompt = ReadInput().GetProperty("message").GetProperty("content").GetString();
}

var rule = rules.RootElement.GetProperty("rules").EnumerateArray()
    .Where(candidate => Matches(candidate.GetProperty("when"), clientArguments) && Has(candidate, clientArguments) && ThreadMatches(candidate))
    .Select(candidate => (JsonElement?)candidate).FirstOrDefault();
if (rule is not { } matched)
{
    stderr.WriteLine($"fake agent: no rule for {string.Join(' ', clientArguments)}");
    return 99;
}

if (threadRequest is { } thread)
{
    stdout.WriteLine(JsonSerializer.Serialize(new { id = thread.GetProperty("id"), result = new { thread = new { id = "" } } }));
    var turn = ReadInput();
    turnRequest = turn;
    prompt = turn.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString();
    if (matched.TryGetProperty("beforeTurnResponse", out var beforeTurn) && Run(beforeTurn) is { } code)
    {
        return code;
    }

    stdout.WriteLine(JsonSerializer.Serialize(new { id = turn.GetProperty("id"), result = new { turn = new { id = "turn-1" } } }));
}

bool ThreadMatches(JsonElement candidate)
{
    if (threadRequest is not { } request || !candidate.TryGetProperty("thread", out var method) || method.ValueKind == JsonValueKind.Null)
    {
        return true;
    }

    if (method.GetString() != request.GetProperty("method").GetString())
    {
        return false;
    }

    return !candidate.TryGetProperty("threadId", out var id) || id.ValueKind == JsonValueKind.Null
            || id.GetString() == request.GetProperty("params").GetProperty("threadId").GetString();
}

string? ReadWireLine()
{
    var line = stdin.ReadLine();
    if (line is not null && framesFile is not null)
    {
        File.AppendAllText(framesFile, line + "\n");
    }

    return line;
}

JsonElement ReadInput() => JsonDocument.Parse(ReadWireLine() ?? throw new IOException("stdin ended before the next frame.")).RootElement.Clone();

return Run(matched.GetProperty("steps")) ?? 0;

int? Run(JsonElement steps)
{
    foreach (var step in steps.EnumerateArray())
    {
        var (name, value) = step.EnumerateObject().Select(property => (property.Name, property.Value)).Single();
        switch (name)
        {
            case "recordArguments":
                File.WriteAllText(value.GetString()!, JsonSerializer.Serialize(clientArguments));
                if (threadRequest is { } threadFrame)
                {
                    File.WriteAllText(value.GetString()! + ".thread.json", threadFrame.GetRawText());
                }

                if (turnRequest is { } turnFrame)
                {
                    File.WriteAllText(value.GetString()! + ".turn.json", turnFrame.GetRawText());
                }

                break;
            case "recordWorkingDirectory":
                File.WriteAllText(value.GetString()!, Environment.CurrentDirectory);
                break;
            case "captureStdin":
                File.WriteAllText(value.GetString()!, prompt ?? stdin.ReadToEnd());
                break;
            case "capturePrompt":
                var captured = prompt ?? stdin.ReadToEnd();
                if (prompt is null && clientArguments.Contains("stream-json"))
                {
                    captured = JsonDocument.Parse(captured).RootElement.GetProperty("message").GetProperty("content").GetString()!;
                }

                File.WriteAllText(value.GetString()!, captured);
                break;
            case "waitForStdinEnd":
                while (ReadWireLine() is not null)
                {
                }

                break;
            case "recordFrames":
                framesFile = value.GetString();
                File.WriteAllText(framesFile!, "");
                break;
            case "readLine":
                var inputLine = ReadWireLine() ?? throw new IOException("stdin ended before the next line.");
                File.WriteAllText(value.GetString()!, inputLine);
                matchedLine = JsonDocument.Parse(inputLine).RootElement.Clone();
                break;
            case "waitForLine":
                var pattern = value.GetString()!;
                matchedLine = null;
                while (ReadWireLine() is { } read)
                {
                    if (!read.Contains(pattern, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matchedLine = JsonDocument.Parse(read).RootElement.Clone();
                    break;
                }

                if (matchedLine is null)
                {
                    return 96;
                }

                break;
            case "echoId":
                var last = matchedLine ?? throw new IOException("No input frame matched.");
                var lastId = last.TryGetProperty("id", out var rpcId) ? rpcId : last.GetProperty("request_id");
                stdout.WriteLine(value.GetString()!.Replace("$id", lastId.GetRawText(), StringComparison.Ordinal));
                break;
            case "closeStdin":
                stdin.Dispose();
                NativePipes.CloseInput();
                break;
            case "print":
                Print(value.GetString()!);
                break;
            case "stderr":
                stderr.WriteLine(value.GetString());
                break;
            case "replay":
                foreach (var line in File.ReadLines(value.GetString()!))
                {
                    Print(line);
                }

                break;
            case "sleep":
                Thread.Sleep(value.GetInt32());
                break;
            case "lockFile":
                held.Add(new FileStream(value.GetString()!, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                break;
            case "waitForFile":
                var gate = value.GetString()!;
                while (!File.Exists(gate))
                {
                    if (!Directory.Exists(Path.GetDirectoryName(gate)))
                    {
                        return 97;
                    }

                    Thread.Sleep(20);
                }

                break;
            case "spawnSleepingChild":
                File.WriteAllText(value.GetString()!, StartSleeper().Id.ToString());
                break;
            case "spawnThroughCmd":
                // /s makes cmd.exe strip only the outer quotes, so each quoted part reaches the copy intact.
                string[] parts = [Environment.ProcessPath!, .. HostArguments(), "--spawn-sleeper", value.GetString()!];
                var copy = string.Join(' ', parts.Select(part => $"\"{part}\""));
                Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")!, $"/s /c \"{copy}\"") { UseShellExecute = false })!.Dispose();
                break;
            case "hang":
                Thread.Sleep(Timeout.Infinite);
                break;
            case "write":
                File.WriteAllText(value[0].GetString()!, value[1].GetString());
                break;
            case "scripted":
                var folder = value.GetString()!;
                var counter = Path.Combine(folder, "count");
                var turn = (File.Exists(counter) ? int.Parse(File.ReadAllText(counter)) : 0) + 1;
                File.WriteAllText(counter, turn.ToString());
                File.WriteAllText(Path.Combine(folder, $"{turn}.stdin"), prompt ?? stdin.ReadToEnd());

                using (var script = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, $"{turn}.json"))))
                {
                    if (Run(script.RootElement) is { } code)
                    {
                        return code;
                    }
                }

                break;
            case "exit":
                return value.GetInt32();
            default:
                stderr.WriteLine($"fake agent: unknown step {name}");
                return 98;
        }
    }

    return null;
}

void Print(string line)
{
    if (!appServer)
    {
        stdout.WriteLine(line);
        return;
    }

    using var parsed = JsonDocument.Parse(line);
    var root = parsed.RootElement;
    if (!root.TryGetProperty("type", out var type))
    {
        stdout.WriteLine(line);
        return;
    }

    var item = root.TryGetProperty("item", out var found) ? found : (JsonElement?)null;
    object? translated = type.GetString() switch
    {
        "thread.started" => new { method = "thread/started", @params = new { thread = new { id = root.GetProperty("thread_id").GetString() } } },
        "item.started" when item?.GetProperty("type").GetString() == "command_execution" => new { method = "item/started", @params = new { item = new { type = "commandExecution", command = item?.GetProperty("command").GetString() } } },
        "item.completed" when item?.GetProperty("type").GetString() == "agent_message" => new { method = "item/completed", @params = new { item = new { type = "agentMessage", id = item?.GetProperty("id").GetString(), text = item?.GetProperty("text").GetString() } } },
        "item.completed" when item?.GetProperty("type").GetString() == "error" => new { method = "error", @params = new { error = new { message = item?.GetProperty("message").GetString() } } },
        "error" => new { method = "error", @params = new { error = new { message = root.GetProperty("message").GetString() } } },
        "turn.completed" => new { method = "turn/completed", @params = new { turn = new { id = "turn-1", status = "completed" } } },
        "turn.failed" => new { method = "turn/completed", @params = new { turn = new { id = "turn-1", status = "failed", error = root.GetProperty("error") } } },
        _ => null,
    };
    if (translated is not null)
    {
        stdout.WriteLine(JsonSerializer.Serialize(translated));
    }
}

static bool Matches(JsonElement when, string[] arguments)
{
    string[] prefix = [.. when.EnumerateArray().Select(part => part.GetString()!)];
    var command = arguments;
    while (command is ["-c", var setting, .. var rest] && setting.Contains('='))
    {
        command = rest;
    }

    return prefix.Length <= command.Length && prefix.SequenceEqual(command[..prefix.Length]);
}

static bool Has(JsonElement rule, string[] arguments)
{
    if (!rule.TryGetProperty("has", out var has))
    {
        return true;
    }

    string[] run = [.. has.EnumerateArray().Select(part => part.GetString()!)];
    return Enumerable.Range(0, Math.Max(0, arguments.Length - run.Length + 1)).Any(start => run.SequenceEqual(arguments[start..(start + run.Length)]));
}

static Process StartSleeper() =>
    Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [.. HostArguments(), "--sleep-forever"]) { UseShellExecute = false })!;

// Run through "dotnet IDevelop.FakeAgent.dll", a copy needs the assembly path. Run through its apphost, it needs nothing.
static string[] HostArguments() =>
    Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? [typeof(Program).Assembly.Location] : [];

internal static class NativePipes
{
    public static void CloseInput()
    {
        if (OperatingSystem.IsWindows())
        {
            var input = GetStdHandle(-10);
            if (input == 0 || input == -1 || !CloseHandle(input))
            {
                throw new IOException("The fake could not close stdin.");
            }

            SetStdHandle(-10, 0);
            return;
        }

        // Console keeps a duplicate of the inherited descriptor. Every descriptor with stdin's device and inode is a
        // reader of the same pipe, and only once all are closed does the host's write fail. The first 16 bytes of
        // struct stat hold that identity on Linux and Darwin.
        var descriptors = OperatingSystem.IsLinux() ? "/proc/self/fd" : "/dev/fd";
        var status = Marshal.AllocHGlobal(512);
        try
        {
            if (Stat(0, status) != 0)
            {
                throw new IOException("The fake could not inspect stdin.");
            }

            var identity = (Marshal.ReadInt64(status), Marshal.ReadInt64(status, 8));
            var readers = Directory.EnumerateFiles(descriptors).Select(path => int.Parse(Path.GetFileName(path)))
                .Where(descriptor => Stat(descriptor, status) == 0 && (Marshal.ReadInt64(status), Marshal.ReadInt64(status, 8)) == identity)
                .ToArray();
            foreach (var descriptor in readers)
            {
                Close(descriptor);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(status);
        }
    }

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);

    [DllImport("libc", EntryPoint = "fstat")]
    private static extern int Stat(int descriptor, nint status);

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int standardHandle, nint handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
