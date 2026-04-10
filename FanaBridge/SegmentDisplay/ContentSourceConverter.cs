using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// JSON converter for the <see cref="ContentSource"/> polymorphic hierarchy.
    /// Reads the "Type" discriminator to instantiate the correct subclass.
    /// </summary>
    internal class ContentSourceConverter : JsonConverter<ContentSource>
    {
        [System.ThreadStatic] private static bool _isWriting;

        public override bool CanWrite { get { return !_isWriting; } }

        public override ContentSource ReadJson(
            JsonReader reader, Type objectType, ContentSource existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;

            JObject obj = JObject.Load(reader);
            string type = (string)obj["Type"];

            ContentSource source;
            switch (type)
            {
                case "Property":
                    source = new PropertyContent();
                    break;
                case "FixedText":
                    source = new FixedTextContent();
                    break;
                case "Expression":
                    source = new ExpressionContent();
                    break;
                case "Sequence":
                    source = new SequenceContent();
                    break;
                case "DeviceCommand":
                    source = new DeviceCommandContent();
                    break;
                default:
                    throw new JsonSerializationException(
                        "Unknown ContentSource type: " + (type ?? "(null)"));
            }

            using (var subReader = obj.CreateReader())
            {
                serializer.Populate(subReader, source);
            }

            return source;
        }

        public override void WriteJson(
            JsonWriter writer, ContentSource value, JsonSerializer serializer)
        {
            _isWriting = true;
            try
            {
                serializer.Serialize(writer, value);
            }
            finally
            {
                _isWriting = false;
            }
        }
    }
}
