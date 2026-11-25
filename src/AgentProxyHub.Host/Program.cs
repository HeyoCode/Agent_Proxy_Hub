using AgentProxyHub.Host;

var app = HubHostFactory.BuildApp(args);
await app.RunAsync();
