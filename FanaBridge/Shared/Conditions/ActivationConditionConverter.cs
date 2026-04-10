using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Shared.Conditions
{
    /// <summary>
    /// JSON converter for the <see cref="ActivationCondition"/> polymorphic hierarchy.
    /// Reads the "Type" discriminator to instantiate the correct subclass.
    /// </summary>
    internal class ActivationConditionConverter : JsonConverter<ActivationCondition>
    {
        // Thread-static reentry guard. The [JsonConverter] attribute on the base
        // class would otherwise cause WriteJson to recurse infinitely when called
        // from JObject.FromObject. By making CanWrite return false during the
        // recursive call, we delegate to the default serializer for that pass.
        [System.ThreadStatic] private static bool _isWriting;

        public override bool CanWrite { get { return !_isWriting; } }

        public override ActivationCondition ReadJson(
            JsonReader reader, Type objectType, ActivationCondition existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;

            JObject obj = JObject.Load(reader);
            string type = (string)obj["Type"];

            ActivationCondition condition;
            switch (type)
            {
                case "AlwaysActive":
                    condition = new AlwaysActive();
                    break;
                case "WhilePropertyTrue":
                    condition = new WhilePropertyTrue();
                    break;
                case "OnValueChange":
                    condition = new OnValueChange();
                    break;
                case "WhileExpressionTrue":
                    condition = new WhileExpressionTrue();
                    break;
                default:
                    throw new JsonSerializationException(
                        "Unknown ActivationCondition type: " + (type ?? "(null)"));
            }

            using (var subReader = obj.CreateReader())
            {
                serializer.Populate(subReader, condition);
            }

            return condition;
        }

        public override void WriteJson(
            JsonWriter writer, ActivationCondition value, JsonSerializer serializer)
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
