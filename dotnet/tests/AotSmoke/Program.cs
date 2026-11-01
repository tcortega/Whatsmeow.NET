using Google.Protobuf;
using Whatsmeow.Proto.WAWebProtobufsE2E;

var message = new Message { Conversation = "aot smoke" };
var parsed = Message.Parser.ParseFrom(message.ToByteArray());

return parsed.Equals(message) ? 0 : 1;
