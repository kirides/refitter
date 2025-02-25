﻿using NSwag;

using System.Text;

namespace Refitter.Core;

/// <summary>
/// Generates Refit clients and interfaces based on an OpenAPI specification.
/// </summary>
public class RefitGenerator
{
    private readonly RefitGeneratorSettings settings;
    private readonly OpenApiDocument document;
    private readonly CSharpClientGeneratorFactory factory;

    private RefitGenerator(RefitGeneratorSettings settings, OpenApiDocument document)
    {
        this.settings = settings;
        this.document = document;
        factory = new CSharpClientGeneratorFactory(settings, document);
    }

    /// <summary>
    /// Creates a new instance of the <see cref="RefitGenerator"/> class asynchronously.
    /// </summary>
    /// <param name="settings">The settings used to configure the generator.</param>
    /// <returns>A new instance of the <see cref="RefitGenerator"/> class.</returns>
    public static async Task<RefitGenerator> CreateAsync(RefitGeneratorSettings settings)
    {
        if (IsHttp(settings.OpenApiPath) && IsYaml(settings.OpenApiPath))
            return new RefitGenerator(settings, await OpenApiYamlDocument.FromUrlAsync(settings.OpenApiPath));
        if (IsHttp(settings.OpenApiPath))
            return new RefitGenerator(settings, await OpenApiDocument.FromUrlAsync(settings.OpenApiPath));
        if (IsYaml(settings.OpenApiPath))
            return new RefitGenerator(settings, await OpenApiYamlDocument.FromFileAsync(settings.OpenApiPath));
        return new RefitGenerator(settings, await OpenApiDocument.FromFileAsync(settings.OpenApiPath));
    }

    /// <summary>
    /// Generates Refit clients and interfaces based on an OpenAPI specification and returns the generated code as a string.
    /// </summary>
    /// <returns>The generated code as a string.</returns>
    public string Generate()
    {
        var generator = factory.Create();
        var contracts = RefitInterfaceImports
            .GetImportedNamespaces(settings)
            .Aggregate(
                generator.GenerateFile(),
                (current, import) => current.Replace($"{import}.", string.Empty));

        IRefitInterfaceGenerator interfaceGenerator = settings.MultipleInterfaces
            ? new RefitMultipleInterfaceGenerator(settings, document, generator)
            : new RefitInterfaceGenerator(settings, document, generator);

        var client = GenerateClient(interfaceGenerator);
        return new StringBuilder()
            .AppendLine(client)
            .AppendLine()
            .AppendLine(settings.GenerateContracts ? contracts : string.Empty)
            .ToString();
    }

    /// <summary>
    /// Generates the client code based on the specified interface generator.
    /// </summary>
    /// <param name="interfaceGenerator">The interface generator used to generate the client code.</param>
    /// <returns>The generated client code as a string.</returns>
    private string GenerateClient(IRefitInterfaceGenerator interfaceGenerator)
    {
        var code = new StringBuilder();
        GenerateAutoGeneratedHeader(code);
        code.AppendLine(RefitInterfaceImports.GenerateNamespaceImports(settings))
            .AppendLine();

        if (settings.AdditionalNamespaces.Any())
        {
            foreach (var ns in settings.AdditionalNamespaces)
            {
                code.AppendLine($"using {ns};");
            }

            code.AppendLine();
        }

        code.AppendLine($$"""
            namespace {{settings.Namespace}}
            {
            {{interfaceGenerator.GenerateCode()}}
            }
            """);

        return code.ToString();
    }

    /// <summary>
    /// Generates the auto-generated header if the setting is enabled.
    /// </summary>
    /// <param name="code">The string builder to append the header to.</param>
    private void GenerateAutoGeneratedHeader(StringBuilder code)
    {
        if (!settings.AddAutoGeneratedHeader)
            return;

        code.AppendLine("""
            // <auto-generated>
            //     This code was generated by Refitter.
            // </auto-generated>

            """);
    }

    /// <summary>
    /// Determines whether the specified path is an HTTP URL.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if the path is an HTTP URL, otherwise false.</returns>
    private static bool IsHttp(string path)
    {
        return path.StartsWith("http://") || path.StartsWith("https://");
    }

    /// <summary>
    /// Determines whether the specified path is a YAML file.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if the path is a YAML file, otherwise false.</returns>
    private static bool IsYaml(string path)
    {
        return path.EndsWith("yaml") || path.EndsWith("yml");
    }
}