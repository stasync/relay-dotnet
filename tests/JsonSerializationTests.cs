using System.Text.Json;
using System.Text.Json.Serialization;

namespace UdpRelay.Tests
{
    public class JsonSerializationTests
    {
        [Fact]
        public void ConfigSerializationTest()
        {
            var config = new TestRoomSettings();
            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.True(config.EqualsTo(deserializedConfig));
        }

        [Fact]
        public void AnonymousSerializationSuccess()
        {
            var data = new
            {
                TargetPort = 1234,
                MaxClientCount = 8,
                ConnectionKey = "hello_world"
            };

            var json = JsonSerializer.Serialize(data);
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.Equal(data.TargetPort, deserializedConfig.TargetPort);
            Assert.Equal(data.MaxClientCount, deserializedConfig.MaxClientCount);
            Assert.Equal(data.ConnectionKey, deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void AnonymousSerializationFailure()
        {
            var data = new
            {
                target_port = 1234,
                max_clients_count = 8,
                connection_key = "hello_world"
            };

            var json = JsonSerializer.Serialize(data);
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.NotEqual(data.target_port, deserializedConfig.TargetPort);
            Assert.NotEqual(data.max_clients_count, deserializedConfig.MaxClientCount);
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
            var defaultConfig = new TestRoomSettings();
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.NotEqual(data.target_port, deserializedConfig.TargetPort);
            Assert.NotEqual(data.connection_key, deserializedConfig.ConnectionKey);

            // Make sure it has default state.
            Assert.Equal(defaultConfig.MaxClientCount, deserializedConfig.MaxClientCount);
        }

        [Fact]
        public void NoJsonIncludeAttributeTest()
        {
            var data = new
            {
                Name = "hello_world",
                TargetPort = 1234,
                MaxClientCount = 8
            };

            // Internal values won't be serialized.
            var json = JsonSerializer.Serialize(data);
            var deserializedConfig = JsonSerializer.Deserialize<NoJsonIncludeTestRoomSettings>(json);
            var defaultConfig = new TestRoomSettings();
            Assert.Equal(defaultConfig.Name, deserializedConfig.Name);
            Assert.Equal(defaultConfig.TargetPort, deserializedConfig.TargetPort);
            Assert.Equal(defaultConfig.MaxClientCount, deserializedConfig.MaxClientCount);
        }

        [Fact]
        public void EmptyJsonDeserializationTest()
        {
            var json = "{}";
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            var defaultConfig = new TestRoomSettings();
            Assert.True(defaultConfig.EqualsTo(deserializedConfig));
        }

        [Fact]
        public void NullValuesDeserializationTest()
        {
            var json = "{\"Name\": null, \"ConnectionKey\": null}";
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.Null(deserializedConfig.Name);
            Assert.Null(deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void NumericBoundariesTest()
        {
            var config = new TestRoomSettings
            {
                TargetPort = int.MaxValue,
                MaxClientCount = 0,
                ProtocolKey = ushort.MaxValue,
                UpdateIntervalMs = int.MinValue
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.Equal(int.MaxValue, deserializedConfig.TargetPort);
            Assert.Equal(0, deserializedConfig.MaxClientCount);
            Assert.Equal(ushort.MaxValue, deserializedConfig.ProtocolKey);
            Assert.Equal(int.MinValue, deserializedConfig.UpdateIntervalMs);
        }

        [Fact]
        public void SpecialCharactersInStringsTest()
        {
            var config = new TestRoomSettings
            {
                Name = "worker\"with\\special/chars\n\r\t",
                ConnectionKey = "key!@#$%^&*()_+-=[]{}|;':\",./<>?~`"
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.Equal(config.Name, deserializedConfig.Name);
            Assert.Equal(config.ConnectionKey, deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void UnicodeStringsTest()
        {
            var config = new TestRoomSettings
            {
                Name = "worker_世界_🌍",
                ConnectionKey = "ключ_клавиша_日本語"
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.Equal(config.Name, deserializedConfig.Name);
            Assert.Equal(config.ConnectionKey, deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void VeryLongStringsTest()
        {
            var longString = new string('A', 10000);
            var config = new TestRoomSettings
            {
                Name = longString,
                ConnectionKey = longString
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.Equal(longString, deserializedConfig.Name);
            Assert.Equal(longString, deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void CaseSensitivityTest()
        {
            var json = "{\"name\": \"test\", \"targetPort\": 9999}";
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            var defaultConfig = new TestRoomSettings();

            // JSON deserialization is case-sensitive by default
            Assert.Equal(defaultConfig.Name, deserializedConfig.Name);
            Assert.Equal(defaultConfig.TargetPort, deserializedConfig.TargetPort);
        }

        [Fact]
        public void ExtraFieldsIgnoredTest()
        {
            var json = "{\"TargetPort\": 8080, \"UnknownField\": \"ignored\", \"AnotherField\": 12345}";
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.Equal(8080, deserializedConfig.TargetPort);
        }

        [Fact]
        public void InvalidJsonThrowsExceptionTest()
        {
            var invalidJson = "{\"TargetPort\": \"not a number\"}";
            Assert.Throws<JsonException>(() =>
                JsonSerializer.Deserialize<TestRoomSettings>(invalidJson));
        }

        [Fact]
        public void MalformedJsonThrowsExceptionTest()
        {
            var malformedJson = "{\"TargetPort\": 8080,}";
            Assert.Throws<JsonException>(() =>
                JsonSerializer.Deserialize<TestRoomSettings>(malformedJson));
        }

        [Fact]
        public void RoundTripMultipleConfigsTest()
        {
            var configs = new[]
            {
                new TestRoomSettings
                {
                    Name = "worker1",
                    TargetPort = 8080,
                    MaxClientCount = 10
                },
                new TestRoomSettings
                {
                    Name = "worker2",
                    TargetPort = 9000,
                    ConnectionKey = "secret123"
                },
                new TestRoomSettings
                {
                    ProtocolKey = 1234,
                    UpdateIntervalMs = 100
                }
            };

            foreach (var config in configs)
            {
                var json = JsonSerializer.Serialize(config);
                var deserialized = JsonSerializer.Deserialize<TestRoomSettings>(json);
                Assert.True(config.EqualsTo(deserialized));
            }
        }

        [Fact]
        public void EmptyStringsPreservedTest()
        {
            var config = new TestRoomSettings
            {
                Name = "",
                ConnectionKey = ""
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.Equal("", deserializedConfig.Name);
            Assert.Equal("", deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void WhitespaceStringsPreservedTest()
        {
            var config = new TestRoomSettings
            {
                Name = "   \t\n   ",
                ConnectionKey = "  key  "
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.Equal(config.Name, deserializedConfig.Name);
            Assert.Equal(config.ConnectionKey, deserializedConfig.ConnectionKey);
        }

        [Fact]
        public void NegativePortValuesTest()
        {
            var config = new TestRoomSettings
            {
                TargetPort = -1,
                MaxClientCount = -100,
                UpdateIntervalMs = -50
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.Equal(-1, deserializedConfig.TargetPort);
            Assert.Equal(-100, deserializedConfig.MaxClientCount);
            Assert.Equal(-50, deserializedConfig.UpdateIntervalMs);
        }

        [Fact]
        public void ZeroValuesTest()
        {
            var config = new TestRoomSettings
            {
                TargetPort = 0,
                MaxClientCount = 0,
                ProtocolKey = 0,
                UpdateIntervalMs = 0
            };

            var json = JsonSerializer.Serialize(config);
            var deserializedConfig = JsonSerializer.Deserialize<TestRoomSettings>(json);
            Assert.Equal(0, deserializedConfig.TargetPort);
            Assert.Equal(0, deserializedConfig.MaxClientCount);
            Assert.Equal(0, deserializedConfig.ProtocolKey);
            Assert.Equal(0, deserializedConfig.UpdateIntervalMs);
        }

        private sealed class TestRoomSettings
        {
            [JsonInclude] internal string Name { get; set; }
            [JsonInclude] internal int TargetPort { get; set; }
            [JsonInclude] internal int MaxClientCount { get; set; } = 16;
            [JsonInclude] internal ushort ProtocolKey { get; set; }
            [JsonInclude] internal string ConnectionKey { get; set; } = string.Empty;
            [JsonInclude] internal int UpdateIntervalMs { get; set; } = 50;

            public bool EqualsTo(TestRoomSettings other)
            {
                if (other is null)
                    return false;

                return
                    Name == other.Name &&
                    TargetPort == other.TargetPort &&
                    MaxClientCount == other.MaxClientCount &&
                    ProtocolKey == other.ProtocolKey &&
                    ConnectionKey == other.ConnectionKey &&
                    UpdateIntervalMs == other.UpdateIntervalMs;
            }
        }

        private sealed class NoJsonIncludeTestRoomSettings
        {
            internal string Name { get; init; }
            internal int TargetPort { get; init; }
            internal int MaxClientCount { get; init; } = 16;

            public bool EqualsTo(NoJsonIncludeTestRoomSettings other)
            {
                if (other is null)
                    return false;

                return
                    Name == other.Name &&
                    TargetPort == other.TargetPort &&
                    MaxClientCount == other.MaxClientCount;
            }
        }
    }
}