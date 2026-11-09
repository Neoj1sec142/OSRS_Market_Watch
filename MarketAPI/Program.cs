using MarketAPI.Extensions.System;

var app = AppBuilderExtensions.BuildMarketWatch();
app.UseMarketWatch();
await app.InitializeMarketWatchAsync();
await app.RunAsync();