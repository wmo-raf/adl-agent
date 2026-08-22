using AdlAgent.Windows;
using Velopack;

// The Windows head. Everything it registers -- and the reasoning behind
// registering it here and nowhere else -- is in WindowsAgentHost.
//
// Velopack first, and before anything that could fail. On a per-user install
// this is where an update actually lands: the newly-installed copy is started
// with hook arguments, does its first-run or post-update work here, and exits
// without ever becoming an agent. On the service tier, and on a copy somebody
// unzipped, it finds no Velopack install and returns immediately -- so the
// call costs the MSI-installed fleet nothing but says, in one line, that this
// binary is both tiers' binary.
VelopackApp.Build().Run();

// Two verbs come next: `adl-agent pair <code>` and `adl-agent status` talk
// to the service that is already running, rather than starting another one.
if (AgentCli.Handles(args))
{
    return await AgentCli.RunAsync(args, Console.Out);
}

var host = WindowsAgentHost.CreateBuilder(args).Build();

host.Run();

return 0;
