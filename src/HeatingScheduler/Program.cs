using HeatingScheduler.Control;
using HeatingScheduler.Octopus;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Tuning;
using HeatingScheduler.Wavin;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSystemd();

builder.Services.AddOptions<OctopusOptions>().BindConfiguration(OctopusOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ScheduleOptions>().BindConfiguration(ScheduleOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<WavinOptions>().BindConfiguration(WavinOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<HeatingOptions>().BindConfiguration(HeatingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<TuningOptions>().BindConfiguration(TuningOptions.SectionName).ValidateDataAnnotations()
    .Validate<IOptions<HeatingOptions>>(
        (tuning, heating) => !tuning.Enabled ||
            (tuning.MinTemperature > heating.Value.OffTemperature && tuning.MinTemperature < heating.Value.OnTemperature),
        "Tuning:MinTemperature must be between Heating:OffTemperature and Heating:OnTemperature.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HeatingSchedule>();
builder.Services.AddSingleton<IHeatingSchedule>(sp => sp.GetRequiredService<HeatingSchedule>());

// Octopus Agile prices (public API, no auth).
builder.Services
    .AddHttpClient<IOctopusAgileClient, OctopusAgileClient>(client => client.BaseAddress = OctopusAgileClient.BaseAddress)
    .AddStandardResilienceHandler();

// Wavin Sentio cloud.
builder.Services.AddSingleton<WavinAuthenticator>();
builder.Services.AddTransient<WavinAuthHandler>();
builder.Services.AddHttpClient(WavinAuthenticator.HttpClientName).AddStandardResilienceHandler();
var wavinHttp = builder.Services.AddHttpClient<IWavinClient, WavinClient>(client => client.BaseAddress = WavinClient.BaseAddress);
wavinHttp.AddStandardResilienceHandler();          // outer: retries/timeouts
wavinHttp.AddHttpMessageHandler<WavinAuthHandler>(); // inner: fresh token on every attempt

builder.Services.AddHostedService<PriceFetchService>();
builder.Services.AddHostedService<HeatingControlService>();
if (builder.Configuration.GetValue($"{TuningOptions.SectionName}:{nameof(TuningOptions.Enabled)}", true))
{
    builder.Services.AddHostedService<ThermalTuningService>();
}

builder.Build().Run();
