using System.Text.Json;
using System.Text.Json.Serialization;

namespace UdpRelay.Tests
{
    public class JsonSerializationTests
    {
        [Fact]
        public void ConfigSerializationTest()
        {
            var config = new RelayInstanceStartupConfig();
            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.True(config.EqualsTo(deserializedConfig));
        }

        [Fact]
        public void AnonymousSerializationSuccess()
        {
            var data = new
            {
                TargetPort = 1234,
                MaxConnectionCount = 8,
                ConnectionKey = "hello_world"
            };

            var json = JsonSerializer.Serialize(data);
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.Equal(data.TargetPort, deserializedConfig.TargetPort);
            Assert.Equal(data.MaxConnectionCount, deserializedConfig.MaxConnectionCount);
            Assert.Equal(data.ConnectionKey, deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void AnonymousSerializationFailure()
        {
            var data = new
            {
                target_port = 1234,
                max_connections_count = 8,
                connection_key = "hello_world"
            };

            var json = JsonSerializer.Serialize(data);
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.NotEqual(data.target_port, deserializedConfig.TargetPort);
            Assert.NotEqual(data.max_connections_count, deserializedConfig.MaxConnectionCount);
            Assert.NotEqual(data.connection_key, deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void PartialDeserializationFailure()
        {
            var data = new
            {
                target_port = 1234,
                connection_key = "hello_world"
            };

            var json = JsonSerializer.Serialize(data);
            var defaultConfig = new RelayInstanceStartupConfig();
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.NotEqual(data.target_port, deserializedConfig.TargetPort);
            Assert.NotEqual(data.connection_key, deserializedConfig.ConnectionKey);

            // Make sure it has default state.
            Assert.Equal(defaultConfig.MaxConnectionCount, deserializedConfig.MaxConnectionCount);
        }

        [Fact]
        public void NoJsonIncludeAttributeTest()
        {
            var data = new
            {
                WorkerName = "hello_world",
                TargetPort = 1234,
                MaxConnectionCount = 8
            };

            // Internal values won't be serialized.
            var json = JsonSerializer.Serialize(data);
            var deserializedConfig = JsonSerializer.Deserialize<NoJsonIncludeRelayInstanceStartupConfig>(json);
            var defaultConfig = new RelayInstanceStartupConfig();
            Assert.Equal(defaultConfig.WorkerName, deserializedConfig.WorkerName);
            Assert.Equal(defaultConfig.TargetPort, deserializedConfig.TargetPort);
            Assert.Equal(defaultConfig.MaxConnectionCount, deserializedConfig.MaxConnectionCount);
        }

        [Fact]
        public void EmptyJsonDeserializationTest()
        {
            var json = "{}";
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            var defaultConfig = new RelayInstanceStartupConfig();
            Assert.True(defaultConfig.EqualsTo(deserializedConfig));
        }

        [Fact]
        public void NullValuesDeserializationTest()
        {
            var json = "{\"WorkerName\": null, \"ConnectionKey\": null}";
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.Null(deserializedConfig.WorkerName);
            Assert.Null(deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void NumericBoundariesTest()
        {
            var config = new RelayInstanceStartupConfig
            {
                TargetPort = int.MaxValue,
                MaxConnectionCount = 0,
                ProtocolKey = ushort.MaxValue,
                ThreadMillisecondsTimeStep = int.MinValue
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.Equal(int.MaxValue, deserializedConfig.TargetPort);
            Assert.Equal(0, deserializedConfig.MaxConnectionCount);
            Assert.Equal(ushort.MaxValue, deserializedConfig.ProtocolKey);
            Assert.Equal(int.MinValue, deserializedConfig.ThreadMillisecondsTimeStep);
        }

        [Fact]
        public void SpecialCharactersInStringsTest()
        {
            var config = new RelayInstanceStartupConfig
            {
                WorkerName = "worker\"with\\special/chars\n\r\t",
                ConnectionKey = "key!@#$%^&*()_+-=[]{}|;':\",./<>?~`"
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.Equal(config.WorkerName, deserializedConfig.WorkerName);
            Assert.Equal(config.ConnectionKey, deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void UnicodeStringsTest()
        {
            var config = new RelayInstanceStartupConfig
            {
                WorkerName = "worker_世界_🌍",
                ConnectionKey = "ключ_клавиша_日本語"
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.Equal(config.WorkerName, deserializedConfig.WorkerName);
            Assert.Equal(config.ConnectionKey, deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void VeryLongStringsTest()
        {
            var longString = new string('A', 10000);
            var config = new RelayInstanceStartupConfig
            {
                WorkerName = longString,
                ConnectionKey = longString
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.Equal(longString, deserializedConfig.WorkerName);
            Assert.Equal(longString, deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void CaseSensitivityTest()
        {
            var json = "{\"workerName\": \"test\", \"targetPort\": 9999}";
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            var defaultConfig = new RelayInstanceStartupConfig();

            // JSON deserialization is case-sensitive by default
            Assert.Equal(defaultConfig.WorkerName, deserializedConfig.WorkerName);
            Assert.Equal(defaultConfig.TargetPort, deserializedConfig.TargetPort);
        }

        [Fact]
        public void ExtraFieldsIgnoredTest()
        {
            var json = "{\"TargetPort\": 8080, \"UnknownField\": \"ignored\", \"AnotherField\": 12345}";
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.Equal(8080, deserializedConfig.TargetPort);
        }

        [Fact]
        public void InvalidJsonThrowsExceptionTest()
        {
            var invalidJson = "{\"TargetPort\": \"not a number\"}";
            Assert.Throws<JsonException>(() =>
                JsonSerializer.Deserialize<RelayInstanceStartupConfig>(invalidJson));
        }

        [Fact]
        public void MalformedJsonThrowsExceptionTest()
        {
            var malformedJson = "{\"TargetPort\": 8080,}";
            Assert.Throws<JsonException>(() =>
                JsonSerializer.Deserialize<RelayInstanceStartupConfig>(malformedJson));
        }

        [Fact]
        public void RoundTripMultipleConfigsTest()
        {
            var configs = new[]
            {
                new RelayInstanceStartupConfig
                {
                    WorkerName = "worker1",
                    TargetPort = 8080,
                    MaxConnectionCount = 10
                },
                new RelayInstanceStartupConfig
                {
                    WorkerName = "worker2",
                    TargetPort = 9000,
                    ConnectionKey = "secret123"
                },
                new RelayInstanceStartupConfig
                {
                    ProtocolKey = 1234,
                    ThreadMillisecondsTimeStep = 100
                }
            };

            foreach (var config in configs)
            {
                var json = JsonSerializer.Serialize(config);
                var deserialized = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
                Assert.True(config.EqualsTo(deserialized));
            }
        }

        [Fact]
        public void EmptyStringsPreservedTest()
        {
            var config = new RelayInstanceStartupConfig
            {
                WorkerName = "",
                ConnectionKey = ""
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.Equal("", deserializedConfig.WorkerName);
            Assert.Equal("", deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void WhitespaceStringsPreservedTest()
        {
            var config = new RelayInstanceStartupConfig
            {
                WorkerName = "   \t\n   ",
                ConnectionKey = "  key  "
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.Equal(config.WorkerName, deserializedConfig.WorkerName);
            Assert.Equal(config.ConnectionKey, deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void NegativePortValuesTest()
        {
            var config = new RelayInstanceStartupConfig
            {
                TargetPort = -1,
                MaxConnectionCount = -100,
                ThreadMillisecondsTimeStep = -50
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.Equal(-1, deserializedConfig.TargetPort);
            Assert.Equal(-100, deserializedConfig.MaxConnectionCount);
            Assert.Equal(-50, deserializedConfig.ThreadMillisecondsTimeStep);
        }

        [Fact]
        public void ZeroValuesTest()
        {
            var config = new RelayInstanceStartupConfig
            {
                TargetPort = 0,
                MaxConnectionCount = 0,
                ProtocolKey = 0,
                ThreadMillisecondsTimeStep = 0
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<RelayInstanceStartupConfig>(json);
            Assert.Equal(0, deserializedConfig.TargetPort);
            Assert.Equal(0, deserializedConfig.MaxConnectionCount);
            Assert.Equal(0, deserializedConfig.ProtocolKey);
            Assert.Equal(0, deserializedConfig.ThreadMillisecondsTimeStep);
        }

        private sealed class RelayInstanceStartupConfig
        {
            [JsonInclude] internal string WorkerName { get; set; }
            [JsonInclude] internal int TargetPort { get; set; }
            [JsonInclude] internal int MaxConnectionCount { get; set; } = 16;
            [JsonInclude] internal ushort ProtocolKey { get; set; }
            [JsonInclude] internal string ConnectionKey { get; set; } = string.Empty;
            [JsonInclude] internal int ThreadMillisecondsTimeStep { get; set; } = 50;

            public bool EqualsTo(RelayInstanceStartupConfig other)
            {
                if (other is null)
                    return false;

                return
                    WorkerName == other.WorkerName &&
                    TargetPort == other.TargetPort &&
                    MaxConnectionCount == other.MaxConnectionCount &&
                    ProtocolKey == other.ProtocolKey &&
                    ConnectionKey == other.ConnectionKey &&
                    ThreadMillisecondsTimeStep == other.ThreadMillisecondsTimeStep;
            }
        }

        private sealed class NoJsonIncludeRelayInstanceStartupConfig
        {
            internal string WorkerName { get; init; }
            internal int TargetPort { get; init; }
            internal int MaxConnectionCount { get; init; } = 16;

            public bool EqualsTo(NoJsonIncludeRelayInstanceStartupConfig other)
            {
                if (other is null)
                    return false;

                return
                    WorkerName == other.WorkerName &&
                    TargetPort == other.TargetPort &&
                    MaxConnectionCount == other.MaxConnectionCount;
            }
        }
    }
}