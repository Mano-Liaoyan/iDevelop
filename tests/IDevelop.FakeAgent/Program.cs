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
//   captureStdin <file>           copy stdin to the file until it closes
//   waitForStdinEnd               read stdin until it closes
//   print <line>, stderr <line>   write one line
//   replay <file>                 write a recorded stream line by line
//   sleep <milliseconds>
//   waitForFile <file>            wait until the file exists, so a test decides when the client goes on
//   spawnSleepingChild <file>     start a copy with --sleep-forever that shares the pipes, and write its pid
//   hang                          wait until killed
//   exit <code>

if (args is ["--sleep-forever"])
{
    Thread.Sleep(Timeout.Infinite);
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
            while (!File.Exists(value.GetString()!))
            {
                Thread.Sleep(20);
            }

            break;
        case "spawnSleepingChild":
            var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [.. HostArguments(), "--sleep-forever"]) { UseShellExecute = false })!;
            File.WriteAllText(value.GetString()!, child.Id.ToString());
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

// Run through "dotnet IDevelop.FakeAgent.dll", a copy needs the assembly path. Run through its apphost, it needs nothing.
static string[] HostArguments() =>
    Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? [typeof(Program).Assembly.Location] : [];
