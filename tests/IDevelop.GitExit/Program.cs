using System.Diagnostics;
using IDevelop.Git;

// A Git call that iDevelop leaves by exiting: git status runs in args[0] on another thread, and once the fake git has
// started its hook child, whose pid file is args[1], this process exits as a crash or a kill would end it.
var call = new Thread(() => GitRepository.Run(["status"], args[0], GitOperation.Metadata,
    new GitLimits(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)))) { IsBackground = true };
call.Start();
var limit = Stopwatch.StartNew();
while (!File.Exists(args[1]))
{
    if (limit.Elapsed >= TimeSpan.FromSeconds(60)) throw new TimeoutException("The fake git did not start its hook child.");
    Thread.Sleep(5);
}

Console.WriteLine("exiting");
Console.Out.Flush();
Environment.Exit(73);
