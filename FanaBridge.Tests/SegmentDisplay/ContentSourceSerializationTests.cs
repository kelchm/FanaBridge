using FanaBridge.SegmentDisplay;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests.SegmentDisplay
{
    public class ContentSourceSerializationTests
    {
        [Fact]
        public void PropertyContent_RoundTrips()
        {
            var original = new PropertyContent
            {
                PropertyName = "DataCorePlugin.GameData.Gear",
                Format = SegmentFormat.Gear,
            };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<ContentSource>(json);

            var typed = Assert.IsType<PropertyContent>(result);
            Assert.Equal("DataCorePlugin.GameData.Gear", typed.PropertyName);
            Assert.Equal(SegmentFormat.Gear, typed.Format);
            Assert.Null(typed.TimeFormat);
        }

        [Fact]
        public void PropertyContent_WithTimeFormat_RoundTrips()
        {
            var original = new PropertyContent
            {
                PropertyName = "DataCorePlugin.GameData.CurrentLapTime",
                Format = SegmentFormat.Time,
                TimeFormat = @"mm\:ss\.f",
            };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<ContentSource>(json);

            var typed = Assert.IsType<PropertyContent>(result);
            Assert.Equal(SegmentFormat.Time, typed.Format);
            Assert.Equal(@"mm\:ss\.f", typed.TimeFormat);
        }

        [Fact]
        public void FixedTextContent_RoundTrips()
        {
            var original = new FixedTextContent { Text = "PIT" };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<ContentSource>(json);

            var typed = Assert.IsType<FixedTextContent>(result);
            Assert.Equal("PIT", typed.Text);
        }

        [Fact]
        public void ExpressionContent_RoundTrips()
        {
            var original = new ExpressionContent
            {
                Expression = "[SpeedKmh] * 0.621371",
                Format = SegmentFormat.Number,
            };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<ContentSource>(json);

            var typed = Assert.IsType<ExpressionContent>(result);
            Assert.Equal("[SpeedKmh] * 0.621371", typed.Expression);
            Assert.Equal(SegmentFormat.Number, typed.Format);
        }

        [Fact]
        public void SequenceContent_RoundTrips()
        {
            var original = new SequenceContent
            {
                Items = new ContentSource[]
                {
                    new FixedTextContent { Text = "LOW" },
                    new FixedTextContent { Text = "FUL" },
                },
                IntervalMs = 800,
            };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<ContentSource>(json);

            var typed = Assert.IsType<SequenceContent>(result);
            Assert.Equal(800, typed.IntervalMs);
            Assert.Equal(2, typed.Items.Length);
            Assert.IsType<FixedTextContent>(typed.Items[0]);
            Assert.Equal("LOW", ((FixedTextContent)typed.Items[0]).Text);
            Assert.Equal("FUL", ((FixedTextContent)typed.Items[1]).Text);
        }

        [Fact]
        public void DeviceCommandContent_RoundTrips()
        {
            var original = new DeviceCommandContent { Command = DeviceCommand.FanatecLogo };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<ContentSource>(json);

            var typed = Assert.IsType<DeviceCommandContent>(result);
            Assert.Equal(DeviceCommand.FanatecLogo, typed.Command);
        }

        [Fact]
        public void UnknownContentType_Throws()
        {
            string json = "{\"Type\":\"Unknown\"}";
            Assert.Throws<JsonSerializationException>(
                () => JsonConvert.DeserializeObject<ContentSource>(json));
        }
    }
}
