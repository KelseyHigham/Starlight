using System.Reflection;
using NLua;
using Starlight.Asus.AnimeMatrix;

namespace Starlight.Engine
{
    public class AnimeMatrixRenderer : IDisposable
    {
        private LuaFunction? _pixelCallback;
        private LuaFunction? _initCallback;

        private DateTime _oldFrameTime;
        private DateTime _nowFrameTime;

        protected readonly AnimeMatrixDevice _device;

        protected Lua? Lua { get; private set; }

        public double Time { get; private set; }
        public double DeltaTime { get; private set; }
        public ulong TotalFrames { get; private set; }

        public bool Running { get; private set; }

        public AnimeMatrixRenderer(AnimeMatrixDevice device)
        {
            _device = device;

            Reset();
        }

        public void LoadScript(string fileName)
        {
            // Always go through Reset: stops loop, clears display, fresh Lua
            Reset();

            try
            {
                Lua!.DoFile(fileName);
                _initCallback = Lua["init"] as LuaFunction;
                _pixelCallback = Lua["pixel"] as LuaFunction;

                if (_pixelCallback == null)
                    throw new InvalidOperationException("`function pixel(x,y)` is missing from your script.");

                _initCallback?.Call();
            }
            catch
            {
                // On error:
                // - We already cleared screen in Reset() => black as error indicator.
                // - Leave callbacks null so Run() cannot start with a bad state.
                _pixelCallback = null;
                _initCallback = null;
                throw;
            }
        }


        public async Task Run(double framerate = 30)
        {
            if (Running)
                return;

            // Do not start if no valid pixel() is loaded
            if (_pixelCallback == null)
                throw new InvalidOperationException("Cannot start renderer: no valid Lua pixel(x,y) function loaded.");

            _oldFrameTime = DateTime.Now;
            _nowFrameTime = DateTime.Now;

            Running = true;

            while (Running)
            {
                Frame();
                _device.Present();
                await Task.Delay((int)((1 / framerate) * 1000));
            }
        }


        public void Stop()
        {
            Running = false;
        }

        private void Frame()
        {
            _nowFrameTime = DateTime.Now;
            var deltaTime = (_nowFrameTime.Ticks - _oldFrameTime.Ticks) / 10000000f;

            Time += deltaTime;
            Lua!["_time"] = Time;
            Lua!["_delta"] = deltaTime;
            Lua!["_frames"] = TotalFrames++;

            for (var y = 0; y < _device.Rows; y++)
            {
                var cols = _device.Columns(y);

                for (var x = 0; x < cols; x++)
                {
                    try
                    {
                        var value = Convert.ToDouble(_pixelCallback!.Call(x, y)[0]);
                        _device.SetLedPlanar(x, y, (byte)(Math.Clamp(255f * value, 0, 255)));
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            }

            _oldFrameTime = _nowFrameTime;
        }

        public void Reset()
        {
            Stop();

            Lua?.Dispose();
            _device.Clear(true);

            TotalFrames = 0;
            Time = 0;
            DeltaTime = 0;

            // fresh Lua state
            Lua = new(true);
            Lua["_frames"] = 0UL;
            Lua["_time"] = 0.0;
            Lua["_delta"] = 0.0;
            Lua["_rows"] = _device.Rows;
            Lua["columns"] = Lua.RegisterFunction(
                "columns",
                this,
                GetType().GetMethod(
                    nameof(AnimeColumns),
                    BindingFlags.NonPublic | BindingFlags.Instance
                )!
            );

            // no script bound yet
            _pixelCallback = null;
            _initCallback = null;
        }


        private int AnimeColumns(int row)
            => _device.Columns(row);

        public void Dispose()
        {
            Lua.Dispose();
        }
    }
}
