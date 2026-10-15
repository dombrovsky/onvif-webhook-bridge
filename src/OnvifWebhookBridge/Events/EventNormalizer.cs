namespace OnvifWebhookBridge.Events
{
    using SharpOnvifCommon.Onvif;
    using System.Globalization;
    using System.Text;
    using System.Xml;

    public sealed class EventNormalizer
    {
        public const int MaximumEventBytes = 64 * 1024;
        public const int MaximumStringBytes = 16 * 1024;
        public const int MaximumFields = 256;
        private const string Schema = "http://www.onvif.org/ver10/schema";

        public NormalizedEvent Normalize(NotificationMessageHolderType notification,
            string cameraId, string cameraName, DateTimeOffset receivedAt)
        {
            ArgumentNullException.ThrowIfNull(notification);
            var budget = new Budget();
            var id = budget.Take(cameraId);
            var name = budget.Take(cameraName);
            var topic = budget.Topic(notification.Topic?.Any ?? []);
            var dialect = budget.Optional(notification.Topic?.Dialect);
            var attributes = new List<TopicAttribute>();
            foreach (var attribute in notification.Topic?.AnyAttr ?? [])
            {
                if (attributes.Count == MaximumFields)
                {
                    budget.Truncated = true;
                    break;
                }
                attributes.Add(new(budget.Take(attribute.Name), budget.Take(attribute.NamespaceURI), budget.Take(attribute.Value)));
            }
            var topicXml = new List<string>();
            foreach (var node in notification.Topic?.Any ?? [])
            {
                if (topicXml.Count == MaximumFields)
                {
                    budget.Truncated = true;
                    break;
                }
                topicXml.Add(budget.Xml(node));
            }
            var message = notification.Message;
            // Only the ONVIF schema Message is semantic content; vendor lookalikes remain raw.
            var semantic = message?.LocalName == "Message" && message.NamespaceURI == Schema
                ? message : message?.GetElementsByTagName("Message", Schema).OfType<XmlElement>().FirstOrDefault();
            var timeText = semantic?.HasAttribute("UtcTime") == true ? budget.Take(semantic.GetAttribute("UtcTime")) : null;
            DateTimeOffset? cameraTime = null;
            var diagnostics = new List<string>();
            if (timeText is not null)
            {
                try
                {
                    cameraTime = XmlConvert.ToDateTimeOffset(timeText);
                }
                catch (FormatException) { diagnostics.Add("InvalidCameraTimestamp"); }
            }
            if (semantic is null)
            {
                diagnostics.Add("MissingSchemaMessage");
            }
            var operation = semantic?.HasAttribute("PropertyOperation") == true
                ? budget.Take(semantic.GetAttribute("PropertyOperation")) : null;
            var source = Fields("Source");
            var data = Fields("Data");
            var key = Fields("Key");
            var raw = message is null ? "" : budget.Xml(message);
            if (budget.Truncated)
            {
                diagnostics.Add("ContentTruncated");
            }
            return new(id, name, receivedAt, topic, dialect, attributes.AsReadOnly(), topicXml.AsReadOnly(),
                timeText, cameraTime, operation, source, data, key, raw, budget.Truncated,
                diagnostics.AsReadOnly(), budget.Used);

            IReadOnlyList<EventField> Fields(string section)
            {
                var fields = new List<EventField>();
                if (semantic is not null)
                {
                    foreach (var container in semantic.ChildNodes.OfType<XmlElement>()
                        .Where(e => e.NamespaceURI == Schema && e.LocalName == section))
                    {
                        foreach (var item in container.ChildNodes.OfType<XmlElement>())
                        {
                            if (++budget.FieldCount > MaximumFields)
                            {
                                budget.Truncated = true;
                                break;
                            }
                            fields.Add(new(budget.Take(item.NamespaceURI == Schema ? item.LocalName : $"{{{item.NamespaceURI}}}{item.LocalName}"),
                                item.HasAttribute("Name") ? budget.Take(item.GetAttribute("Name")) : null,
                                item.HasAttribute("Value") ? budget.Take(item.GetAttribute("Value")) : null,
                                budget.Xml(item), budget.Metadata(item)));
                        }
                    }
                }
                return fields.AsReadOnly();
            }
        }

        private sealed class Budget
        {
            public int Used
            {
                get; private set;
            }
            public int FieldCount
            {
                get; set;
            }
            public bool Truncated
            {
                get; set;
            }
            public string? Optional(string? value) => value is null ? null : Take(value);
            public string Metadata(XmlElement element)
            {
                using var output = new CappedWriter(Math.Min(MaximumStringBytes, MaximumEventBytes - Used));
                try
                {
                    foreach (var attribute in element.Attributes.OfType<XmlAttribute>()
                        .Where(a => a.NamespaceURI != "http://www.w3.org/2000/xmlns/" &&
                            !(a.NamespaceURI.Length == 0 && a.LocalName is "Name" or "Value"))
                        .OrderBy(a => a.NamespaceURI, StringComparer.Ordinal).ThenBy(a => a.LocalName, StringComparer.Ordinal))
                    {
                        WritePart(attribute.NamespaceURI);
                        WritePart(attribute.LocalName);
                        WritePart(attribute.Value);
                    }
                    using var writer = XmlWriter.Create(output, new XmlWriterSettings
                    {
                        OmitXmlDeclaration = true,
                        ConformanceLevel = ConformanceLevel.Fragment
                    });
                    foreach (XmlNode child in element.ChildNodes)
                    {
                        child.WriteTo(writer);
                    }
                }
                catch (LimitReachedException) { Truncated = true; }
                var result = output.ToString();
                Used += Encoding.UTF8.GetByteCount(result);
                return result;
                void WritePart(string value)
                {
                    output.Write($"{value.Length}:");
                    output.Write(value);
                }
            }
            public string Topic(XmlNode[] nodes)
            {
                using var output = new CappedWriter(Math.Min(MaximumStringBytes, MaximumEventBytes - Used));
                try
                {
                    foreach (var node in nodes)
                    {
                        output.Write(node.InnerText);
                    }
                }
                catch (LimitReachedException) { Truncated = true; }
                var result = output.ToString();
                Used += Encoding.UTF8.GetByteCount(result);
                return result;
            }
            public string Take(string value)
            {
                var limit = Math.Min(MaximumStringBytes, MaximumEventBytes - Used);
                var count = 0;
                var length = 0;
                foreach (var rune in value.EnumerateRunes())
                {
                    if (count + rune.Utf8SequenceLength > limit)
                    {
                        Truncated = true;
                        break;
                    }
                    count += rune.Utf8SequenceLength;
                    length += rune.Utf16SequenceLength;
                }
                Used += count;
                return value[..length];
            }

            // Stream XML into a capped writer: no unbounded OuterXml copy. Small messages
            // retain complete XML; a clipped XML preview may deliberately be incomplete.
            public string Xml(XmlNode node)
            {
                var limit = Math.Min(MaximumStringBytes, MaximumEventBytes - Used);
                using var output = new CappedWriter(limit);
                try
                {
                    using var writer = XmlWriter.Create(output, new XmlWriterSettings
                    {
                        OmitXmlDeclaration = true,
                        ConformanceLevel = ConformanceLevel.Fragment
                    });
                    node.WriteTo(writer);
                }
                catch (LimitReachedException) { Truncated = true; }
                var result = output.ToString();
                Used += Encoding.UTF8.GetByteCount(result);
                return result;
            }
        }

        private sealed class LimitReachedException : Exception;
        private sealed class CappedWriter(int limit) : System.IO.TextWriter
        {
            private readonly StringBuilder text = new();
            private int bytes;
            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
            public override void Write(char value) => Write(value.ToString(CultureInfo.InvariantCulture));
            public override void Write(string? value)
            {
                if (value is null)
                {
                    return;
                }
                foreach (var rune in value.EnumerateRunes())
                {
                    if (bytes + rune.Utf8SequenceLength > limit)
                    {
                        throw new LimitReachedException();
                    }
                    text.Append(rune.ToString());
                    bytes += rune.Utf8SequenceLength;
                }
            }
            public override string ToString() => text.ToString();
        }
    }
}
