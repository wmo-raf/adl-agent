using AdlAgent.Windows;

// The Windows head. Everything it registers -- and the reasoning behind
// registering it here and nowhere else -- is in WindowsAgentHost.
//
// Two verbs come first: `adl-agent pair <code>` and `adl-agent status` talk
// to the service that is already running, rather than starting another one.
if (AgentCli.Handles(args))
{
    return await AgentCli.RunAsync(args, Console.Out);
}

var host = WindowsAgentHost.CreateBuilder(args).Build();

host.Run();

return 0;
