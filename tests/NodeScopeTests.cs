using Keel.DependencyInjection;
using Keel.Utils;
using System.Reflection;

namespace UdpRelay.Tests
{
    public class NodeScopeTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Keel provides null for a type that isn't bound, instead of throwing, so a missing binding would only show up later as a NullReferenceException.
        [Fact]
        public void EveryServiceAndInjectedFieldIsBound()
        {
            // Parse takes a whole command line, starting with the program.
            var startupArguments = LaunchArguments.CreateInstance<StartupArguments>(LaunchArguments.Parse("relay.dll -httpListenerUrl http://127.0.0.1:8080/ -httpApiKey test-key"));
            var scope = Entry.BuildScope(startupArguments);
            try
            {
                // Every class with [Inject] fields, plus the server that Main provides directly.
                var serviceTypes = typeof(Entry).Assembly.GetTypes()
                    .Where(type => type.GetFields(InstanceFields).Any(field => field.IsDefined(typeof(InjectAttribute))))
                    .Append(typeof(HttpServer))
                    .ToArray();
                Assert.Contains(typeof(RoomRegistry), serviceTypes);
                Assert.Contains(typeof(HttpRequestHandler), serviceTypes);

                foreach (var serviceType in serviceTypes)
                {
                    var service = scope.Provide(serviceType);
                    Assert.True(service != null, $"'{serviceType.Name}' is not bound in {nameof(Entry)}.{nameof(Entry.BuildScope)}.");

                    foreach (var field in serviceType.GetFields(InstanceFields).Where(field => field.IsDefined(typeof(InjectAttribute))))
                        Assert.True(field.GetValue(service) != null, $"'{serviceType.Name}.{field.Name}' ({field.FieldType.Name}) is not bound in {nameof(Entry)}.{nameof(Entry.BuildScope)}.");
                }

                Assert.Same(startupArguments, scope.Provide<StartupArguments>());
            }
            finally
            {
                scope.Provide<HttpWebhookSender>().Dispose();
                scope.Dispose();
            }
        }
    }
}