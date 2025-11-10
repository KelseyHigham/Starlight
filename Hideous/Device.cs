using System.Reflection;
using Hideous.Platform;

namespace Hideous
{
    public abstract class Device : IDisposable
    {
        private UsbProvider _usbProvider;

        protected Device(DeviceCharacteristics characteristics)
        {
            if (OperatingSystem.IsLinux())
            {
                _usbProvider = new LinuxUsbProvider(characteristics);
            }
            else if (OperatingSystem.IsWindows())
            {
                _usbProvider = new WindowsUsbProvider(characteristics);
            }
            else
            {
                throw new PlatformNotSupportedException("Your platform is not supported.");
            }
        }

        public T Feature<T>(params byte[] command) where T : FeaturePacket
        {
            return (T)CreateInstanceSafe(typeof(T), command)!;
        }

        public T Packet<T>(params byte[] command) where T : Packet
        {
            return (T)CreateInstanceSafe(typeof(T), command)!;
        }

        private object? CreateInstanceSafe(Type type, byte[] command)
        {
            // Prefer a constructor whose first parameter is byte[] (or params byte[]).
            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var ctor in ctors)
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length >= 1 && parameters[0].ParameterType == typeof(byte[]))
                {
                    // Found a ctor that accepts byte[] as first parameter
                    return ctor.Invoke(new object[] { command });
                }
            }

            // Fallback: try parameterless constructor
            var parameterless = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (parameterless != null)
                return parameterless.Invoke(null);

            // As a last resort, attempt Activator (will throw if nothing matches)
            return Activator.CreateInstance(type, nonPublic: true);
        }

        public void Set(FeaturePacket packet)
            => _usbProvider.Set(packet.Data);

        public byte[] Get(FeaturePacket packet)
            => _usbProvider.Get(packet.Data);

        public void Write(Packet packet)
            => _usbProvider.Write(packet.Data);

        public void Dispose()
            => _usbProvider.Dispose();
    }
}