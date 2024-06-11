using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using static PKHeX.WinForms.PluginLoadSetting;

namespace PKHeX.WinForms;

public static class PluginLoader
{
    public static IEnumerable<T> LoadPlugins<T>(string pluginPath, PluginLoadSetting loadSetting) where T : class
    {
        var assemblies = new List<Assembly>();

        if (loadSetting.IsMerged())
        {
            // Load plugins from the merged assembly
            assemblies.Add(Assembly.GetExecutingAssembly());
            assemblies.AddRange(LoadEmbeddedPlugins<T>());
        }
        else
        {
            // Load plugins from the specified plugin directory
            if (Directory.Exists(pluginPath))
            {
                var dllFileNames = Directory.EnumerateFiles(pluginPath, "*.dll", SearchOption.AllDirectories);
                assemblies.AddRange(GetAssemblies(dllFileNames, loadSetting));
            }
        }

        // Get plugin types and instantiate them
        var pluginTypes = GetPluginsOfType<T>(assemblies);
        return LoadPlugins<T>(pluginTypes).ToList(); // Call ToList to force immediate execution
    }

    private static IEnumerable<Assembly> LoadEmbeddedPlugins<T>()
    {
        var executingAssembly = Assembly.GetExecutingAssembly();
        var resources = executingAssembly.GetManifestResourceNames();

        foreach (var resourceName in resources)
        {
            if (resourceName.EndsWith(".dll"))
            {
                using var stream = executingAssembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                var assemblyData = new byte[stream.Length];
                stream.Read(assemblyData, 0, assemblyData.Length);
                yield return Assembly.Load(assemblyData);
            }
        }
    }

    private static IEnumerable<T> LoadPlugins<T>(IEnumerable<Type> pluginTypes) where T : class
    {
        foreach (var t in pluginTypes)
        {
            T? activate;
            try { activate = (T?)Activator.CreateInstance(t); }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to load plugin [{t.Name}]: {t.FullName}");
                Debug.WriteLine(ex.Message);
                continue;
            }
            if (activate != null)
                yield return activate;
        }
    }

    private static IEnumerable<Assembly> GetAssemblies(IEnumerable<string> dllFileNames, PluginLoadSetting loadSetting)
    {
        var loadMethod = GetPluginLoadMethod(loadSetting);
        foreach (var file in dllFileNames)
        {
            Assembly x;
            try { x = loadMethod(file); }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to load plugin from file: {file}");
                Debug.WriteLine(ex.Message);
                continue;
            }
            yield return x;
        }
        if (loadSetting.IsMerged())
            yield return Assembly.GetExecutingAssembly(); // load merged too
    }

    private static Func<string, Assembly> GetPluginLoadMethod(PluginLoadSetting pls) => pls switch
    {
        LoadFrom or LoadFromMerged => Assembly.LoadFrom,
        LoadFile or LoadFileMerged => Assembly.LoadFile,
        UnsafeLoadFrom or UnsafeMerged => Assembly.UnsafeLoadFrom,
        _ => throw new IndexOutOfRangeException($"PluginLoadSetting: {pls} method not defined."),
    };

    public static bool IsMerged(this PluginLoadSetting loadSetting) => loadSetting is LoadFromMerged or LoadFileMerged or UnsafeMerged;

    private static IEnumerable<Type> GetPluginsOfType<T>(IEnumerable<Assembly> assemblies)
    {
        var pluginType = typeof(T);
        return assemblies.SelectMany(z => GetPluginTypes(z, pluginType));
    }

    private static IEnumerable<Type> GetPluginTypes(Assembly z, Type plugin)
    {
        try
        {
            // Handle Costura merged plugin dll's; need to Attach for them to correctly retrieve their dependencies.
            var assemblyLoaderType = z.GetType("Costura.AssemblyLoader", false);
            var attachMethod = assemblyLoaderType?.GetMethod("Attach", BindingFlags.Static | BindingFlags.Public);
            attachMethod?.Invoke(null, []);

            var types = z.GetExportedTypes();
            return types.Where(type => IsTypePlugin(type, plugin));
        }
        // User plugins can be out of date, with mismatching API surfaces.
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to load plugin [{plugin.FullName}]: {z.FullName}");
            Debug.WriteLine(ex.Message);
            if (ex is not ReflectionTypeLoadException rtle)
                return [];

            foreach (var le in rtle.LoaderExceptions)
            {
                if (le is not null)
                    Debug.WriteLine(le.Message);
            }
            return [];
        }
    }

    private static bool IsTypePlugin(Type type, Type plugin)
    {
        if (type.IsInterface || type.IsAbstract)
            return false;
        return plugin.IsAssignableFrom(type);
    }
}
