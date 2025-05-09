using System.ComponentModel.DataAnnotations;
using MessagePack;

namespace ClienteSGR.Models
{
    [MessagePackObject]
    public class SendDataRequest
    {
        [MessagePack.Key(0)]
        public string RecipientAlias { get; set; } = string.Empty;

        [MessagePack.Key(1)]
        public DataBatchContainer BatchContainer { get; set; } = new DataBatchContainer();
    }
}