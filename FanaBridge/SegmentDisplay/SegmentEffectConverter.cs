using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// JSON converter for the <see cref="SegmentEffect"/> polymorphic hierarchy.
    /// Reads the "Type" discriminator to instantiate the correct subclass.
    /// </summary>
    internal class SegmentEffectConverter : JsonConverter<SegmentEffect>
    {
        [System.ThreadStatic] private static bool _isWriting;

        public override bool CanWrite { get { return !_isWriting; } }

        public override SegmentEffect ReadJson(
            JsonReader reader, Type objectType, SegmentEffect existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;

            JObject obj = JObject.Load(reader);
            string type = (string)obj["Type"];

            SegmentEffect effect;
            switch (type)
            {
                case "Blink":
                    effect = new BlinkEffect();
                    break;
                case "Flash":
                    effect = new FlashEffect();
                    break;
                default:
                    throw new JsonSerializationException(
                        "Unknown SegmentEffect type: " + (type ?? "(null)"));
            }

            using (var subReader = obj.CreateReader())
            {
                serializer.Populate(subReader, effect);
            }

            return effect;
        }

        public override void WriteJson(
            JsonWriter writer, SegmentEffect value, JsonSerializer serializer)
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
