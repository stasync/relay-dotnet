using Keel.Utils;
using Keel.Utils.Debug;
using System;
using System.Reflection;

namespace UdpRelay
{
    internal sealed class StartupArguments
    {
        [InjectCommandArgument("logLevel")]
        internal readonly LogLevel LogLevel = LogLevel.All;

        [InjectCommandArgument("logOutputRelativePath")]
        internal readonly string LogOutputRelativePath = "logs";

        [InjectCommandArgument("httpListenerUrl")]
        internal readonly string HttpListenerUrl = "";

        [InjectCommandArgument("httpApiKey")]
        internal readonly string HttpApiKey = "";

        [InjectCommandArgument("blacklistDurationMs")]
        internal readonly int BlacklistDurationMs = 10000;

        [InjectCommandArgument("roomPort")]
        internal readonly int[] DefaultRoomPorts = Array.Empty<int>();

        [InjectCommandArgument("maxClientsPerRoom")]
        internal readonly int MaxClientsPerRoom = 16;

        [InjectCommandArgument("protocolKey")]
        internal readonly ushort ProtocolKey = 0;

        [InjectCommandArgument("roomUpdateIntervalMs")]
        internal readonly int RoomUpdateIntervalMs = 50;

        [InjectCommandArgument("logConnectionRejections")]
        internal readonly bool LogConnectionRejections = false;

        public override string ToString()
        {
            var result = $"{GetType().FullName}:";
            foreach (var field in GetType().GetFields(bindingAttr: BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
            {
                var fieldValue = field.GetValue(this);
                // Null references.
                if (fieldValue == null)
                {
                    result += $"\n  {field.Name} : null";
                    continue;
                }

                // Never log secrets.
                if (field.Name == nameof(HttpApiKey))
                {
                    result += $"\n  {field.Name} : <hidden>";
                    continue;
                }

                // Handle string differently.
                if (field.FieldType == typeof(string))
                {
                    var stringValue = (string)fieldValue;
                    result += $"\n  {field.Name} : \"{stringValue}\"";
                    continue;
                }

                // Simple primitives.
                if (!field.FieldType.IsArray)
                {
                    result += $"\n  {field.Name} : {fieldValue}";
                    continue;
                }

                // Arrays.
                var valueAsArray = (Array)fieldValue;
                var objectArray = new object[valueAsArray!.Length];
                for (var i = 0; i < valueAsArray.Length; i++)
                    objectArray[i] = valueAsArray.GetValue(i);

                result += $"\n  {field.Name} : [{string.Join(", ", objectArray)}]";
            }

            return result;
        }
    }
}