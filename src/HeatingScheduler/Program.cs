using HeatingScheduler.Control;
using HeatingScheduler.Octopus;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Wavin;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSystemd();

builder.Services.AddOptions<OctopusOptions>().BindConfiguration(OctopusOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ScheduleOptions>().BindConfiguration(ScheduleOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<WavinOptions>().BindConfiguration(WavinOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<HeatingOptions>().BindConfiguration(HeatingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HeatingSchedule>();

// Octopus Agile prices (public API, no auth).
builder.Services
    .AddHttpClient<IOctopusAgileClient, OctopusAgileClient>((sp, client) =>
        client.BaseAddress = sp.GetRequiredService<IOptions<OctopusOptions>>().Value.BaseUrl)
    .AddStandardResilienceHandler();

// Wavin Sentio cloud.
builder.Services.AddSingleton<WavinAuthenticator>();
builder.Services.AddTransient<WavinAuthHandler>();
builder.Services.AddHttpClient(WavinAuthenticator.HttpClientName).AddStandardResilienceHandler();
var wavinHttp = builder.Services.AddHttpClient<IWavinClient, WavinClient>((sp, client) =>
    client.BaseAddress = sp.GetRequiredService<IOptions<WavinOptions>>().Value.DeviceServiceBaseUrl);
wavinHttp.AddStandardResilienceHandler();          // outer: retries/timeouts
wavinHttp.AddHttpMessageHandler<WavinAuthHandler>(); // inner: fresh token on every attempt

builder.Services.AddHostedService<PriceSchedulerService>();
builder.Services.AddHostedService<HeatingControlService>();

builder.Build().Run();
