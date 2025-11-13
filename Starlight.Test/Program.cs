using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Starlight.Asus.AnimeMatrix;
using Starlight.Engine;

static class Program
{
    private static IntPtr _nativeLuaHandle = IntPtr.Zero;

    [STAThread]
    static void Main(string[] args)
    {
        // Load native Lua if embedded
        TryLoadNativeFromResources("lua54.dll");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

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

        var scriptPath = args.Length > 0
            ? args[0]
            : Path.Combine(exeDir, "anim.lua");

        if (!File.Exists(scriptPath))
        {
            MessageBox.Show(
                "Script not found.\nPlace 'anim.lua' next to the executable or pass a path as the first argument.",
                "Starlight",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return;
        }

        // Everything else runs inside an async context kicked off from here
        RunTrayApp(scriptPath);
    }

    private static void RunTrayApp(string scriptPath)
    {
        var exeDir = AppContext.BaseDirectory;

        // Device and renderer
        var device = new AnimeMatrixDevice();
        var renderer = new AnimeMatrixRenderer(device);

        // initial load
        try
        {
            renderer.LoadScript(scriptPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to load Lua script:\n{ex}",
                "Starlight",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            device.Dispose();
            renderer.Dispose();
            return;
        }

        var reloadLock = new SemaphoreSlim(1, 1);
        Task runTask = renderer.Run(30);

        // File watcher for hot reload
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

            _ = Task.Run(async () =>
            {
                await reloadLock.WaitAsync();
                try
                {
                    renderer.Stop();
                    try { await runTask; } catch { }

                    await Task.Delay(100);

                    try
                    {
                        renderer.LoadScript(scriptPath);
                    }
                    catch
                    {
                        // Keep black as error indicator.
                        // Swallow here; user sees failure on the device.
                    }

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
            onChange(s, new FileSystemEventArgs(
                WatcherChangeTypes.Changed,
                Path.GetDirectoryName(e.FullPath) ?? exeDir,
                Path.GetFileName(e.FullPath)
            ));
        };

        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Renamed += onRename;

        // Tray icon setup
        var notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "Starlight AnimeMatrix"
        };

        // Use the application's own icon (embedded via ApplicationIcon)
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon != null)
        {
            notifyIcon.Icon = appIcon;
        }
        else
        {
            notifyIcon.Icon = System.Drawing.SystemIcons.Application;
        }

   
        var menu = new ContextMenuStrip();

        var editItem = new ToolStripMenuItem("Edit anim.lua");
        editItem.Click += (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = scriptPath,
                    UseShellExecute = true
                });
            }
            catch { }
        };
        menu.Items.Add(editItem);

        var reloadItem = new ToolStripMenuItem("Reload now");
        reloadItem.Click += async (s, e) =>
        {
            await reloadLock.WaitAsync();
            try
            {
                renderer.Stop();
                try { await runTask; } catch { }

                try
                {
                    renderer.LoadScript(scriptPath);
                }
                catch
                {
                    // keep black on error
                }

                runTask = renderer.Run(30);
            }
            finally
            {
                reloadLock.Release();
            }
        };
        menu.Items.Add(reloadItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += async (s, e) =>
        {
            // Clean shutdown
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();

            renderer.Stop();
            try { await runTask; } catch { }

            notifyIcon.Visible = false;
            notifyIcon.Dispose();

            renderer.Dispose();
            device.Dispose();

            Application.Exit();
        };
        menu.Items.Add(exitItem);

        notifyIcon.ContextMenuStrip = menu;

        // Optional: double-click opens anim.lua
        notifyIcon.DoubleClick += (s, e) => editItem.PerformClick();

        // Start message loop (no window; tray-only)
        Application.Run();

        // Fallback cleanup if Application.Run exits other than via ExitItem
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();

        renderer.Stop();
        try { runTask.Wait(500); } catch { }

        notifyIcon.Visible = false;
        notifyIcon.Dispose();

        renderer.Dispose();
        device.Dispose();
    }

    private static void TryLoadNativeFromResources(string nativeFileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceCandidates = asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(nativeFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (resourceCandidates.Length == 0)
            return;

        string? chosen;
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

        var tempDir = Path.Combine(Path.GetTempPath(), asm.GetName().Name + "_" + asm.GetName().Version);
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, nativeFileName);

        using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.CopyTo(fs);
        }

        try
        {
            _nativeLuaHandle = NativeLibrary.Load(tempFile);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load embedded native {nativeFileName}: {ex.Message}");
        }
    }
}
