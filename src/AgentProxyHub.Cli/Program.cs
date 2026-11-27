using AgentProxyHub.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;
return await AphCliRunner.RunAsync(args, Console.Out, Console.Error);
