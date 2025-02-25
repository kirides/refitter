using System.Text.Json;
using Exceptionless;
using Exceptionless.Plugins;
using Spectre.Console.Cli;

namespace Refitter;

public static class Analytics
{
    public static void Configure()
    {
        ExceptionlessClient.Default.Configuration.SetUserIdentity(
            SupportInformation.GetAnonymousIdentity(),
            SupportInformation.GetSupportKey());

        ExceptionlessClient.Default.Configuration.UseSessions();
        ExceptionlessClient.Default.Configuration.SetVersion(typeof(GenerateCommand).Assembly.GetName().Version!);
        ExceptionlessClient.Default.Startup("pRql7vmgecZ0Iph6MU5TJE5XsZeesdTe0yx7TN4f");
    }
    
    public static Task LogFeatureUsage(Settings settings)
    {
        if (settings.NoLogging)
            return Task.CompletedTask;

        foreach (var property in typeof(Settings).GetProperties())
        {
            var value = property.GetValue(settings);
            if (value is null or false)
                continue;

            property.GetCustomAttributes(typeof(CommandOptionAttribute), true)
                .OfType<CommandOptionAttribute>()
                .Where(
                    attribute =>
                        !attribute.LongNames.Contains("namespace") &&
                        !attribute.LongNames.Contains("output") &&
                        !attribute.LongNames.Contains("no-logging"))
                .ToList()
                .ForEach(
                    attribute =>
                        ExceptionlessClient.Default
                            .CreateFeatureUsage(attribute.LongNames.FirstOrDefault() ?? property.Name)
                            .Submit());
        }

        return ExceptionlessClient.Default.ProcessQueueAsync();
    }

    public static Task LogError(Exception exception, Settings settings)
    {
        if (settings.NoLogging)
            return Task.CompletedTask;

        exception
            .ToExceptionless(
                new ContextData(
                    JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(settings))!))
            .Submit();

        return ExceptionlessClient.Default.ProcessQueueAsync();
    }
}