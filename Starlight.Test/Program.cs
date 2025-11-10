using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Starlight.Asus.AnimeMatrix;
using Starlight.Engine;

class Program
{
    // Keep native handle so the library stays loaded
    private static IntPtr _nativeLuaHandle = IntPtr.Zero;
    static async Task<int> Main(string[] args)
    {
        // Try to load lua54.dll from embedded resources (if present)
        TryLoadNativeFromResources("lua54.dll");

        var exeDir = AppContext.BaseDirectory;
        var libsDir = Path.Combine(exeDir, "libs");
        if (Directory.Exists(libsDir))
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            Environment.SetEnvironmentVariable("PATH", libsDir + Path.PathSeparator + path);

            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
            {
                try
                {
                    var name = new AssemblyName(eventArgs.Name).Name + ".dll";
                    var candidate = Path.Combine(libsDir, name);
                    if (File.Exists(candidate))
                        return Assembly.LoadFrom(candidate);
                }
                catch { }
                return null;
            };
        }

        // Print loaded native modules for troubleshooting (helps diagnose BadImageFormat / wrong dll)
        try
        {
            Console.WriteLine("Loaded native modules:");
            var proc = Process.GetCurrentProcess();
            foreach (ProcessModule mod in proc.Modules)
            {
                // show only native DLLs (you can remove condition to print everything)
                if (mod.ModuleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"{mod.ModuleName} -> {mod.FileName}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to enumerate native modules: {ex.Message}");
        }

        var scriptPath = args.Length > 0 ? args[0] : Path.Combine(exeDir, "anim.lua");

        Console.WriteLine($"Executable folder: {exeDir}");
        Console.WriteLine($"Using script: {scriptPath}");

        if (!File.Exists(scriptPath))
        {
            Console.WriteLine("Script not found. Place a Lua file named 'anim.lua' next to the executable or pass a path as the first argument.");
            return 1;
        }

        Console.WriteLine("Tip: run elevated and close Armoury Crate / ASUS RGB software if the device is locked.");

        using var device = new AnimeMatrixDevice();
        using var renderer = new AnimeMatrixRenderer(device);

        try
        {
            // initial load
            try
            {
                renderer.LoadScript(scriptPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load Lua script: {ex}");
                return 2;
            }

            var cts = new CancellationTokenSource();
            var reloadLock = new SemaphoreSlim(1, 1);
            Task runTask = renderer.Run(30); // start renderer

            // Setup file watcher
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(scriptPath) ?? exeDir)
            {
                Filter = Path.GetFileName(scriptPath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };

            DateTime lastEvent = DateTime.MinValue;
            const int debounceMs = 250;

            FileSystemEventHandler onChange = (s, e) =>
            {
                var now = DateTime.UtcNow;
                if ((now - lastEvent).TotalMilliseconds < debounceMs)
                    return;
                lastEvent = now;

                // run reload on a background task to avoid blocking watcher thread
                _ = Task.Run(async () =>
                {
                    await reloadLock.WaitAsync();
                    try
                    {
                        Console.WriteLine($"Change detected ({e.ChangeType}). Reloading script...");
                        // stop renderer and wait for loop to finish
                        renderer.Stop();
                        try { await runTask; } catch { /* ignore */ }

                        // small delay to ensure file write is complete
                        await Task.Delay(100);

                        try
                        {
                            renderer.LoadScript(scriptPath);
                            Console.WriteLine("Reloaded script successfully.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Reload failed: {ex}");
                        }

                        // restart renderer
                        runTask = renderer.Run(30);
                    }
                    finally
                    {
                        reloadLock.Release();
                    }
                });
            };

            RenamedEventHandler onRename = (s, e) =>
            {
                // treat rename as a change (file replaced)
                onChange(s, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath) ?? exeDir, Path.GetFileName(e.FullPath)));
            };

            watcher.Changed += onChange;
            watcher.Created += onChange;
            watcher.Renamed += onRename;

            // Stop renderer on Ctrl+C or ENTER
            var stopTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("Ctrl+C pressed — stopping...");
                stopTcs.TrySetResult(null);
            };

            Console.WriteLine("Renderer started. Edit the Lua file to hot-reload. Press ENTER or Ctrl+C to stop.");

            var readLineTask = Task.Run(() => Console.ReadLine());
            var completed = await Task.WhenAny(readLineTask, stopTcs.Task);

            // shutdown sequence
            Console.WriteLine("Stopping renderer...");
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            cts.Cancel();

            renderer.Stop();
            try { await runTask; } catch { /* ignore */ }

            Console.WriteLine("Renderer stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Runtime error: {ex}");
            return 3;
        }
    }

    private static void TryLoadNativeFromResources(string nativeFileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        // choose resource by architecture if you embedded per-arch paths like "native.win-x64.lua54.dll"
        var resourceCandidates = asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(nativeFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (resourceCandidates.Length == 0)
            return; // nothing embedded

        // Prefer x64 resource for 64-bit process, otherwise fallback
        string chosen = null;
        if (Environment.Is64BitProcess)
            chosen = resourceCandidates.FirstOrDefault(n => n.IndexOf("win-x64", StringComparison.OrdinalIgnoreCase) >= 0)
                  ?? resourceCandidates.FirstOrDefault();
        else
            chosen = resourceCandidates.FirstOrDefault(n => n.IndexOf("win-x86", StringComparison.OrdinalIgnoreCase) >= 0)
                  ?? resourceCandidates.FirstOrDefault();

        if (chosen == null)
            return;

        using var stream = asm.GetManifestResourceStream(chosen);
        if (stream == null)
            return;

        // Write to a temp file with .dll ext (can't load from stream directly)
        var tempDir = Path.Combine(Path.GetTempPath(), asm.GetName().Name + "_" + asm.GetName().Version);
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, nativeFileName);

        // Overwrite to handle updates during development
        using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.CopyTo(fs);
        }

        // Load native library and keep handle
        try
        {
            _nativeLuaHandle = NativeLibrary.Load(tempFile);
            // optionally mark file for deletion on exit (Windows will not delete while loaded).
            // Could schedule cleanup of tempDir on application exit.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load embedded native {nativeFileName}: {ex.Message}");
        }
    }
}