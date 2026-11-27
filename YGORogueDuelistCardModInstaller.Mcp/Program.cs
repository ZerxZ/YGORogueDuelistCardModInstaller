using YGORogueDuelistCardModInstaller.Core.Mcp;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await McpProtocolHandler.RunStdioLoopAsync(hooks: null, ct: cts.Token);
