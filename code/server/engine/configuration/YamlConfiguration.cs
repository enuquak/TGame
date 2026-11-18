using System;
using System.IO;
using System.Collections.Generic;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TGame;

/// <summary>独立解析 include 文件并保存只读的配置快照，由宿主注册为 IoC 单例。</summary>
public sealed class YamlConfiguration
{
    public const string FileName = "server.yaml";
    public const string IncludeKey = "include";
    public const string RunDirectoryKey = "runDirectory";
    private const int MaximumNodeDepth = 64;
    private readonly YamlMappingNode root;

    public static string ConfigurationPath => Path.Combine("code", "server", "config", FileName);
    public string SourcePath { get; }

    public YamlConfiguration(string path)
    {
        SourcePath = Path.GetFullPath(path);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        root = Load(SourcePath, new HashSet<string>(comparer));
    }

    public T Read<T>(string? section = null)
    {
        YamlNode node = root;
        if (section is not null && !root.Children.TryGetValue(new YamlScalarNode(section), out node!))
        {
            throw new InvalidDataException($"Missing configuration section '{section}' in {SourcePath}.");
        }

        var builder = new DeserializerBuilder();
        builder.WithNamingConvention(CamelCaseNamingConvention.Instance);
        builder.WithDuplicateKeyChecking();
        try
        {
            var value = builder.Build().Deserialize<T>(Serialize(node));
            if (value is null)
            {
                throw new InvalidDataException($"Empty configuration section '{section}' in {SourcePath}.");
            }

            return value;
        }
        catch (YamlException error)
        {
            throw new InvalidDataException($"Invalid configuration section '{section ?? "root"}' in {SourcePath}.", error);
        }
    }

    public string ResolveRunDirectory(string workingDirectory)
    {
        var relative = Read<string>(RunDirectoryKey);
        ValidateRunDirectory(relative);
        return Path.GetFullPath(relative, Path.GetFullPath(workingDirectory));
    }

    public void SaveSnapshot(string path, string? runDirectory = null)
    {
        // 只修改本次输出的顶层映射，已加载实例和其他运行组的快照保持不变。
        var snapshot = new YamlMappingNode(root.Children);
        if (runDirectory is not null)
        {
            ValidateRunDirectory(runDirectory);
            snapshot.Children[new YamlScalarNode(RunDirectoryKey)] = new YamlScalarNode(runDirectory);
        }

        File.WriteAllText(path, Serialize(snapshot));
    }

    internal void SaveSection(string path, string section, YamlNode value)
    {
        var snapshot = new YamlMappingNode(root.Children);
        snapshot.Children[new YamlScalarNode(section)] = value;
        File.WriteAllText(path, Serialize(snapshot));
    }

    internal static void ValidateRunDirectory(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("runDirectory must be a nonempty relative path from the working directory.");
        }
    }

    private static YamlMappingNode Load(string path, HashSet<string> loading)
    {
        if (!loading.Add(path))
        {
            throw new InvalidDataException($"Circular configuration include: {path}.");
        }

        try
        {
            using var input = File.OpenText(path);
            var stream = new YamlStream();
            stream.Load(input);
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode document)
            {
                throw new InvalidDataException($"Configuration must contain one YAML mapping document: {path}.");
            }

            // 展开本文件的别名，避免合并后不同文件使用同名锚点时互相影响。
            var expanded = (YamlMappingNode)CopyNode(document, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance), 0);
            var merged = new YamlMappingNode();
            var includeKey = new YamlScalarNode(IncludeKey);
            if (expanded.Children.TryGetValue(includeKey, out var includes))
            {
                if (includes is not YamlSequenceNode sequence)
                {
                    throw new InvalidDataException($"include must be a list of file names: {path}.");
                }

                var names = new HashSet<string>(loading.Comparer);
                foreach (var item in sequence.Children)
                {
                    if (item is not YamlScalarNode scalar || !IsIncludeName(scalar.Value) || !names.Add(scalar.Value!))
                    {
                        throw new InvalidDataException($"Invalid or repeated include name in {path}.");
                    }

                    var included = Path.Combine(Path.GetDirectoryName(path)!, scalar.Value + ".yaml");
                    Merge(merged, Load(included, loading), path);
                }

                expanded.Children.Remove(includeKey);
            }

            Merge(merged, expanded, path);
            return merged;
        }
        catch (YamlException error)
        {
            throw new InvalidDataException($"Invalid YAML configuration: {path}.", error);
        }
        finally
        {
            loading.Remove(path);
        }
    }

    private static bool IsIncludeName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            name is not "." and not ".." &&
            !Path.HasExtension(name) &&
            !name.Contains('/') &&
            !name.Contains('\\') &&
            name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static void Merge(YamlMappingNode target, YamlMappingNode source, string path)
    {
        foreach (var pair in source.Children)
        {
            if (pair.Key is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
            {
                throw new InvalidDataException($"Configuration keys must be nonempty strings: {path}.");
            }

            if (target.Children.ContainsKey(key))
            {
                throw new InvalidDataException($"Duplicate top-level configuration key '{key.Value}' in {path}.");
            }

            target.Add(key, pair.Value);
        }
    }

    private static YamlNode CopyNode(YamlNode node, HashSet<YamlNode> visiting, int depth)
    {
        if (depth > MaximumNodeDepth || !visiting.Add(node))
        {
            throw new InvalidDataException($"Configuration aliases must be acyclic and nesting must be at most {MaximumNodeDepth} levels.");
        }

        try
        {
            if (node is YamlScalarNode scalar)
            {
                return new YamlScalarNode(scalar.Value) { Style = scalar.Style, Tag = scalar.Tag };
            }

            if (node is YamlSequenceNode sequence)
            {
                var result = new YamlSequenceNode();
                foreach (var child in sequence.Children)
                {
                    result.Add(CopyNode(child, visiting, depth + 1));
                }

                return result;
            }

            if (node is YamlMappingNode mapping)
            {
                var result = new YamlMappingNode();
                foreach (var pair in mapping.Children)
                {
                    result.Add(CopyNode(pair.Key, visiting, depth + 1), CopyNode(pair.Value, visiting, depth + 1));
                }

                return result;
            }

            throw new InvalidDataException("Unsupported YAML configuration node.");
        }
        finally
        {
            visiting.Remove(node);
        }
    }

    private static string Serialize(YamlNode node)
    {
        using var output = new StringWriter();
        var stream = new YamlStream(new YamlDocument(node));
        stream.Save(output, assignAnchors: false);
        return output.ToString();
    }
}
