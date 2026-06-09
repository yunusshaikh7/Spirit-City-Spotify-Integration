using System.Reflection;
using CUE4Parse;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

if (args.Length > 0 && args[0].Equals("dump", StringComparison.OrdinalIgnoreCase))
{
    DumpAsset(args);
    return;
}

ListTypes(args);

static void DumpAsset(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: dump <game-root> <asset-path> [game-version]");
        Environment.ExitCode = 2;
        return;
    }

    var gameRoot = args[1];
    var assetPath = args[2];
    var gameVersion = args.Length > 3
        ? Enum.Parse<EGame>(args[3], ignoreCase: true)
        : EGame.GAME_UE5_4;

    var versions = new VersionContainer(gameVersion, ETexturePlatform.DesktopMobile);
    using var provider = new DefaultFileProvider(gameRoot, SearchOption.AllDirectories, true, versions)
    {
        ReadScriptData = true,
        ReadShaderMaps = false,
        SkipReferencedTextures = true,
    };

    var vfsFiles = Directory.GetFiles(gameRoot, "*.pak", SearchOption.AllDirectories)
        .Concat(Directory.GetFiles(gameRoot, "*.utoc", SearchOption.AllDirectories))
        .ToArray();
    provider.RegisterVfs(vfsFiles);
    provider.Initialize();
    var mounted = provider.Mount();
    var defaultKey = new FAesKey(new byte[32]);
    var manualMounted = 0;
    foreach (var unloaded in provider.UnloadedVfs.ToArray())
    {
        try
        {
            unloaded.MountTo(provider.Files, provider.PathComparer, defaultKey, null);
            manualMounted++;
        }
        catch (Exception error)
        {
            Console.WriteLine($"Manual mount failed for {unloaded}: {error.GetType().Name}: {error.Message}");
        }
    }

    Console.WriteLine($"Project: {provider.ProjectName}");
    Console.WriteLine($"Game display: {provider.GameDisplayName}");
    Console.WriteLine($"Files: {provider.Files.Count}");
    Console.WriteLine($"Registered VFS files: {vfsFiles.Length}");
    Console.WriteLine($"Mounted VFS: {mounted}");
    Console.WriteLine($"Manual mounted VFS: {manualMounted}");
    Console.WriteLine($"Mounted VFS count: {provider.MountedVfs.Count}");
    Console.WriteLine($"Unloaded VFS count: {provider.UnloadedVfs.Count}");
    Console.WriteLine($"Required keys count: {provider.RequiredKeys.Count}");
    foreach (var unloaded in provider.UnloadedVfs.Take(10))
    {
        Console.WriteLine($"Unloaded: {unloaded}");
        Console.WriteLine($"  Type: {unloaded.GetType().FullName}");
        foreach (var property in unloaded.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Take(12))
        {
            object? value;
            try
            {
                value = property.GetValue(unloaded);
            }
            catch (Exception error)
            {
                value = error.GetType().Name;
            }

            Console.WriteLine($"  {property.Name}: {value}");
        }
    }
    Console.WriteLine($"Loading: {assetPath}");

    var package = provider.LoadPackage(assetPath);
    Console.WriteLine($"Package type: {package.GetType().FullName}");

    var objects = provider.LoadPackageObjects(assetPath).ToArray();
    Console.WriteLine($"Objects: {objects.Length}");

    var settings = new JsonSerializerSettings
    {
        Formatting = Formatting.Indented,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        Converters =
        {
            new FStructFallbackConverter(),
            new FPropertyTagTypeConverter(),
        },
    };

    foreach (var obj in objects)
    {
        Console.WriteLine("---- object ----");
        Console.WriteLine($"{obj.ExportType} {obj.Name}");
        Console.WriteLine(JsonConvert.SerializeObject(ProjectObject(obj), settings));
    }
}

static object ProjectObject(UObject obj)
{
    var projected = new Dictionary<string, object?>
    {
        ["name"] = obj.Name,
        ["exportType"] = obj.ExportType,
        ["path"] = obj.GetPathName(),
    };

    foreach (var field in GetFields(obj.GetType()))
    {
        if (field.Name.Contains("k__BackingField", StringComparison.Ordinal))
        {
            continue;
        }

        var value = field.GetValue(obj);
        if (value is null)
        {
            continue;
        }

        projected[field.Name] = value;
    }

    return projected;
}

static IEnumerable<FieldInfo> GetFields(Type type)
{
    while (type != typeof(object) && type is not null)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            yield return field;
        }

        type = type.BaseType;
    }
}

static void ListTypes(string[] args)
{
    var query = args.Length > 0 ? args[0] : string.Empty;
    var showMembers = args.Contains("--members");
    var assembly = typeof(CUE4Parse.UE4.IUStruct).Assembly;

    var types = assembly.GetTypes()
        .Where(type => string.IsNullOrWhiteSpace(query) ||
                       type.FullName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
        .OrderBy(type => type.FullName, StringComparer.OrdinalIgnoreCase);

    foreach (var type in types)
    {
        Console.WriteLine(type.FullName);

        if (!showMembers)
        {
            continue;
        }

        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
        {
            Console.WriteLine($"  ctor {FormatParameters(constructor.GetParameters())}");
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {method.ReturnType.Name} {method.Name}({FormatParameters(method.GetParameters())})");
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  prop {property.PropertyType.Name} {property.Name}");
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .OrderBy(field => field.Name, StringComparer.OrdinalIgnoreCase))
        {
            var value = field.IsLiteral ? $" = {field.GetRawConstantValue()}" : string.Empty;
            Console.WriteLine($"  field {field.FieldType.Name} {field.Name}{value}");
        }
    }
}

static string FormatParameters(IEnumerable<ParameterInfo> parameters)
{
    return string.Join(", ", parameters.Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}"));
}
