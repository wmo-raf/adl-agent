using AdlAgent.Windows;

// The Windows head. Everything it registers -- and the reasoning behind
// registering it here and nowhere else -- is in WindowsAgentHost.
var host = WindowsAgentHost.CreateBuilder(args).Build();

host.Run();
