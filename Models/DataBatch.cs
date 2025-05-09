
using MessagePack;

namespace ClienteSGR.Models
{
    public enum MessageType : byte
    {
        Unknown = 0,
        FileFragment = 1,
        IpPacket = 2,   
    }

    [MessagePackObject]
    public class DataBatch
    {
        [Key(0)]
        public MessageType Type { get; set; } = MessageType.Unknown; 

        [Key(1)]
        public Guid TransferId { get; set; } 
                                             

        [Key(2)]
        public int Sequence { get; set; } 

        [Key(3)]
        public bool IsFirst { get; set; } 

        [Key(4)]
        public bool IsLast { get; set; }

        [Key(5)]
        public byte[] Data { get; set; } = Array.Empty<byte>(); 



        [Key(6)]
        public string? Filename { get; set; } 

        [Key(7)]
        public long? OriginalSize { get; set; }

        [Key(8)] public string? SourceIp { get; set; } 
        [Key(9)] public string? DestinationIp { get; set; } 
        [Key(10)] public int? ProtocolType { get; set; }
    }
}