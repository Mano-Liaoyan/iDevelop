using System.Diagnostics;
using System.Text;
using System.Text.Json;

// A stand-in for an agent client, so tests drive a real child process without any client installed.
//
//   IDevelop.FakeAgent --rules <rules.json> -- <the arguments the real client gets>
//   IDevelop.FakeAgent --sleep-forever
//
// The rules file is {"rules": [{"when": ["auth", "status"], "steps": [{"print": "..."}, {"exit": 0}]}]}.
// The first rule whose "when" is a prefix of the client arguments runs its steps in order. Steps:
//   recordArguments <file>        write the client arguments as a JSON array
//   recordWorkingDirectory <file> write the current folder
//   captureStdin <file>           copy stdin to the file until it closes
//   waitForStdinEnd               read stdin until it closes
//   print <line>, stderr <line>   write one line
//   replay <file>                 write a recorded stream line by line
//   sleep <milliseconds>
//   waitForFile <file>            wait until the file exists, so a test decides when the client goes on. Exit 97 if its
//                                 folder is deleted, because the test that owned it has ended
//   spawnSleepingChild <file>     start a copy with --sleep-forever that shares the pipes, and write its pid
//   spawnThroughCmd <file>        Windows only: run a copy through cmd.exe /c that starts a sleeping copy, writes its
//                                 pid, and exits, so the sleeper shares the pipes and its parent is gone
//   hang                          wait until killed
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

if (args is not ["--rules", var rulesFile, "--", .. var clientArguments])
{
    Console.Error.WriteLine("usage: IDevelop.FakeAgent --rules <rules.json> -- <arguments>");
    return 2;
}

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true, NewLine = "\n" };
using var rules = JsonDocument.Parse(File.ReadAllBytes(rulesFile));
var rule = rules.RootElement.GetProperty("rules").EnumerateArray()
    .Where(candidate => Matches(candidate.GetProperty("when"), clientArguments))
    .Select(candidate => (JsonElement?)candidate)
    .FirstOrDefault();
if (rule is not { } matched)
{
    stderr.WriteLine($"fake agent: no rule for {string.Join(' ', clientArguments)}");
    return 99;
}

foreach (var step in matched.GetProperty("steps").EnumerateArray())
{
    var (name, value) = step.EnumerateObject().Select(property => (property.Name, property.Value)).Single();
    switch (name)
    {
        case "recordArguments":
            File.WriteAllText(value.GetString()!, JsonSerializer.Serialize(clientArguments));
            break;
        case "recordWorkingDirectory":
            File.WriteAllText(value.GetString()!, Environment.CurrentDirectory);
            break;
        case "captureStdin":
            using (var input = Console.OpenStandardInput())
            using (var file = File.Create(value.GetString()!))
            {
                input.CopyTo(file);
            }

            break;
        case "waitForStdinEnd":
            using (var input = Console.OpenStandardInput())
            {
                input.CopyTo(Stream.Null);
            }

            break;
        case "print":
            stdout.WriteLine(value.GetString());
            break;
        case "stderr":
            stderr.WriteLine(value.GetString());
            break;
        case "replay":
            foreach (var line in File.ReadLines(value.GetString()!))
            {
                stdout.WriteLine(line);
            }

            break;
        case "sleep":
            Thread.Sleep(value.GetInt32());
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
        case "exit":
            return value.GetInt32();
        default:
            stderr.WriteLine($"fake agent: unknown step {name}");
            return 98;
    }
}

return 0;

static bool Matches(JsonElement when, string[] arguments)
{
    string[] prefix = [.. when.EnumerateArray().Select(part => part.GetString()!)];
    return prefix.Length <= arguments.Length && prefix.SequenceEqual(arguments[..prefix.Length]);
}

static Process StartSleeper() =>
    Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [.. HostArguments(), "--sleep-forever"]) { UseShellExecute = false })!;

// Run through "dotnet IDevelop.FakeAgent.dll", a copy needs the assembly path. Run through its apphost, it needs nothing.
static string[] HostArguments() =>
    Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? [typeof(Program).Assembly.Location] : [];
