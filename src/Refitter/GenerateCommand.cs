using System.Diagnostics;

using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers.Exceptions;

using Refitter.Core;
using Refitter.Validation;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Refitter;

public sealed class GenerateCommand : AsyncCommand<Settings>
{
    private static readonly string Crlf = Environment.NewLine;

    public override ValidationResult Validate(CommandContext context, Settings settings)
    {
        if (!settings.NoLogging)
            Analytics.Configure();

        return SettingsValidator.Validate(settings);
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var refitGeneratorSettings = new RefitGeneratorSettings
        {
            OpenApiPath = settings.OpenApiPath!,
            Namespace = settings.Namespace ?? "GeneratedCode",
            AddAutoGeneratedHeader = !settings.NoAutoGeneratedHeader,
            AddAcceptHeaders = !settings.NoAcceptHeaders,
            GenerateContracts = !settings.InterfaceOnly,
            GenerateClients = !settings.ContractOnly,
            ReturnIApiResponse = settings.ReturnIApiResponse,
            ReturnIObservable = settings.ReturnIObservable,
            UseCancellationTokens = settings.UseCancellationTokens,
            GenerateOperationHeaders = !settings.NoOperationHeaders,
            UseIsoDateFormat = settings.UseIsoDateFormat,
            TypeAccessibility = settings.InternalTypeAccessibility
                ? TypeAccessibility.Internal
                : TypeAccessibility.Public,
            AdditionalNamespaces = settings.AdditionalNamespaces!,
            ExcludeNamespaces = settings.ExcludeNamespaces ?? Array.Empty<string>(),
            MultipleInterfaces = settings.MultipleInterfaces,
            IncludePathMatches = settings.MatchPaths ?? Array.Empty<string>(),
            IncludeTags = settings.Tags ?? Array.Empty<string>(),
            GenerateDeprecatedOperations = !settings.NoDeprecatedOperations,
            OperationNameTemplate = settings.OperationNameTemplate,
            OptionalParameters = settings.OptionalNullableParameters,
            TrimUnusedSchema = settings.TrimUnusedSchema,
            KeepSchemaPatterns = settings.KeepSchemaPatterns ?? Array.Empty<string>(),
            OperationNameGenerator = settings.OperationNameGenerator,
            GenerateDefaultAdditionalProperties = !settings.SkipDefaultAdditionalProperties,
            ImmutableRecords = settings.ImmutableRecords,
            ApizrSettings = settings.UseApizr ? new ApizrSettings() : null,
            UseDynamicQuerystringParameters = settings.UseDynamicQuerystringParameters,
            GenerateMultipleFiles = settings.GenerateMultipleFiles || !string.IsNullOrWhiteSpace(settings.ContractsOutputPath),
            ContractsOutputFolder = settings.ContractsOutputPath ?? settings.OutputPath,
            ContractsNamespace = settings.ContractsNamespace
        };

        try
        {
            var stopwatch = Stopwatch.StartNew();
            AnsiConsole.MarkupLine($"[green]Refitter v{GetType().Assembly.GetName().Version!}[/]");
            AnsiConsole.MarkupLine(
                settings.NoLogging
                    ? "[green]Support key: Unavailable when logging is disabled[/]"
                    : $"[green]Support key: {SupportInformation.GetSupportKey()}[/]");

            if (!string.IsNullOrWhiteSpace(settings.SettingsFilePath))
            {
                var json = await File.ReadAllTextAsync(settings.SettingsFilePath);
                refitGeneratorSettings = Serializer.Deserialize<RefitGeneratorSettings>(json);
                refitGeneratorSettings.OpenApiPath = settings.OpenApiPath!;

                if (!string.IsNullOrWhiteSpace(refitGeneratorSettings.ContractsOutputFolder))
                    refitGeneratorSettings.GenerateMultipleFiles = true;
            }

            var generator = await RefitGenerator.CreateAsync(refitGeneratorSettings);
            if (!settings.SkipValidation)
                await ValidateOpenApiSpec(refitGeneratorSettings.OpenApiPath);

            await (refitGeneratorSettings.GenerateMultipleFiles
                ? WriteMultipleFiles(generator, settings, refitGeneratorSettings)
                : WriteSingleFile(generator, settings, refitGeneratorSettings));

            await Analytics.LogFeatureUsage(settings, refitGeneratorSettings);
            AnsiConsole.MarkupLine($"[green]Duration: {stopwatch.Elapsed}{Crlf}[/]");

            if (!settings.NoBanner)
                DonationBanner();

            ShowDeprecationWarning(refitGeneratorSettings);
            return 0;
        }
        catch (Exception exception)
        {
            AnsiConsole.WriteLine();
            if (exception is OpenApiUnsupportedSpecVersionException unsupportedSpecVersionException)
            {
                AnsiConsole.MarkupLine($"[red]Unsupported OpenAPI version: {unsupportedSpecVersionException.SpecificationVersion}[/]");
                AnsiConsole.WriteLine();
            }

            if (exception is not OpenApiValidationException)
            {
                AnsiConsole.MarkupLine($"[red]Error: {exception.Message}[/]");
                AnsiConsole.MarkupLine($"[red]Exception: {exception.GetType()}[/]");
                AnsiConsole.MarkupLine($"[yellow]Stack Trace:{Crlf}{exception.StackTrace}[/]");
                AnsiConsole.WriteLine();
            }

            if (!settings.SkipValidation)
            {
                AnsiConsole.MarkupLine("[yellow]Try using the --skip-validation argument.[/]");
                AnsiConsole.WriteLine();
            }

            AnsiConsole.MarkupLine("[yellow]####################################################################[/]");
            AnsiConsole.MarkupLine("[yellow]#  Consider reporting the problem if you are unable to resolve it  #[/]");
            AnsiConsole.MarkupLine("[yellow]#  https://github.com/christianhelle/refitter/issues               #[/]");
            AnsiConsole.MarkupLine("[yellow]####################################################################[/]");
            AnsiConsole.WriteLine();

            await Analytics.LogError(exception, settings);
            return exception.HResult;
        }
    }

    private static async Task WriteSingleFile(
        RefitGenerator generator,
        Settings settings,
        RefitGeneratorSettings refitGeneratorSettings)
    {
        var code = generator.Generate().ReplaceLineEndings();
        var outputPath = GetOutputPath(settings, refitGeneratorSettings);

        AnsiConsole.MarkupLine($"[green]Output: {Path.GetFullPath(outputPath)}[/]");
        AnsiConsole.MarkupLine($"[green]Length: {code.Length} bytes[/]");

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(outputPath, code);
    }

    private async Task WriteMultipleFiles(
        RefitGenerator generator,
        Settings settings,
        RefitGeneratorSettings refitGeneratorSettings)
    {
        var generatorOutput = generator.GenerateMultipleFiles();
        foreach (var outputFile in generatorOutput.Files)
        {
            if (
                !string.IsNullOrWhiteSpace(refitGeneratorSettings.ContractsOutputFolder)
                && refitGeneratorSettings.ContractsOutputFolder != RefitGeneratorSettings.DefaultOutputFolder
                && outputFile.Filename == FilenameConstants.Contracts
            )
            {
                var root = string.IsNullOrWhiteSpace(settings.SettingsFilePath)
                    ? string.Empty
                    : Path.GetDirectoryName(settings.SettingsFilePath) ?? string.Empty;

                var contractsFolder = Path.GetFullPath(Path.Combine(root, refitGeneratorSettings.ContractsOutputFolder));
                if (!string.IsNullOrWhiteSpace(contractsFolder) && !Directory.Exists(contractsFolder))
                    Directory.CreateDirectory(contractsFolder);

                var contractsFile = Path.Combine(contractsFolder ?? "./", outputFile.Filename);
                AnsiConsole.MarkupLine($"[green]Output: {Path.GetFullPath(contractsFile)}[/]");
                AnsiConsole.MarkupLine($"[green]Length: {outputFile.Content.Length} bytes[/]");

                await File.WriteAllTextAsync(contractsFile, outputFile.Content);
                continue;
            }

            var code = outputFile.Content;
            var outputPath = GetOutputPath(settings, refitGeneratorSettings, outputFile);

            AnsiConsole.MarkupLine($"[green]Output: {Path.GetFullPath(outputPath)}[/]");
            AnsiConsole.MarkupLine($"[green]Length: {code.Length} bytes[/]");

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(outputPath, code);
        }
    }

    private static void ShowDeprecationWarning(RefitGeneratorSettings refitGeneratorSettings)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        if (refitGeneratorSettings.DependencyInjectionSettings?.UsePolly is true)
#pragma warning restore CS0618 // Type or member is obsolete
        {
            AnsiConsole.MarkupLine("[yellow]###############################################################[/]");
            AnsiConsole.MarkupLine("[yellow]#  The 'usePolly' property in the settings file is deprecated #[/]");
            AnsiConsole.MarkupLine("[yellow]#  and will be removed in the near future.                    #[/]");
            AnsiConsole.MarkupLine("[yellow]#                                                             #[/]");
            AnsiConsole.MarkupLine("[yellow]#  Use 'transientErrorHandler: Polly' instead                 #[/]");
            AnsiConsole.MarkupLine("[yellow]###############################################################[/]");
            AnsiConsole.WriteLine();
        }
    }

    private static void DonationBanner()
    {
        AnsiConsole.MarkupLine("[dim]###################################################################[/]");
        AnsiConsole.MarkupLine("[dim]#  Do you find this tool useful and feel a bit generous?          #[/]");
        AnsiConsole.MarkupLine("[dim]#  https://github.com/sponsors/christianhelle                     #[/]");
        AnsiConsole.MarkupLine("[dim]#  https://www.buymeacoffee.com/christianhelle                    #[/]");
        AnsiConsole.MarkupLine("[dim]#                                                                 #[/]");
        AnsiConsole.MarkupLine("[dim]#  Does this tool not work or does it lack something you need?    #[/]");
        AnsiConsole.MarkupLine("[dim]#  https://github.com/christianhelle/refitter/issues              #[/]");
        AnsiConsole.MarkupLine("[dim]###################################################################[/]");
        AnsiConsole.WriteLine();
    }

    private static string GetOutputPath(Settings settings, RefitGeneratorSettings refitGeneratorSettings)
    {
        var outputPath = settings.OutputPath != Settings.DefaultOutputPath &&
                         !string.IsNullOrWhiteSpace(settings.OutputPath)
            ? settings.OutputPath
            : refitGeneratorSettings.OutputFilename ?? "Output.cs";

        if (!string.IsNullOrWhiteSpace(refitGeneratorSettings.OutputFolder) &&
            refitGeneratorSettings.OutputFolder != RefitGeneratorSettings.DefaultOutputFolder)
        {
            outputPath = Path.Combine(refitGeneratorSettings.OutputFolder, outputPath);
        }

        return outputPath;
    }

    private string GetOutputPath(
        Settings settings,
        RefitGeneratorSettings refitGeneratorSettings,
        GeneratedCode outputFile)
    {
        var root = string.IsNullOrWhiteSpace(settings.SettingsFilePath)
            ? string.Empty
            : Path.GetDirectoryName(settings.SettingsFilePath) ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(refitGeneratorSettings.OutputFolder) &&
            refitGeneratorSettings.OutputFolder != RefitGeneratorSettings.DefaultOutputFolder)
        {
            return Path.Combine(root, refitGeneratorSettings.OutputFolder, outputFile.Filename);
        }

        return !string.IsNullOrWhiteSpace(settings.OutputPath)
            ? Path.Combine(root, settings.OutputPath, outputFile.Filename)
            : outputFile.Filename;
    }

    private static async Task ValidateOpenApiSpec(string openApiPath)
    {
        var validationResult = await OpenApiValidator.Validate(openApiPath);
        if (!validationResult.IsValid)
        {
            AnsiConsole.MarkupLine($"[red]{Crlf}OpenAPI validation failed:{Crlf}[/]");

            foreach (var error in validationResult.Diagnostics.Errors)
            {
                TryWriteLine(error, "red", "Error");
            }

            foreach (var warning in validationResult.Diagnostics.Warnings)
            {
                TryWriteLine(warning, "yellow", "Warning");
            }

            validationResult.ThrowIfInvalid();
        }

        AnsiConsole.MarkupLine($"[green]{Crlf}OpenAPI statistics:{Crlf}{validationResult.Statistics}{Crlf}[/]");
    }

    private static void TryWriteLine(
        OpenApiError error,
        string color,
        string label)
    {
        try
        {
            AnsiConsole.MarkupLine($"[{color}]{label}:{Crlf}{error}{Crlf}[/]");
        }
        catch
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = color switch
            {
                "red" => ConsoleColor.Red,
                "yellow" => ConsoleColor.Yellow,
                _ => originalColor
            };

            Console.WriteLine($"{label}:{Crlf}{error}{Crlf}");

            Console.ForegroundColor = originalColor;
        }
    }
}
